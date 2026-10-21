#!/usr/bin/env bash
# Liveness probe for the order-worker CONTAINER.
#
# WHY THIS EXISTS
#   Order.Worker is a BackgroundService host with NO HTTP listener
#   (Dockerfile.worker runs `dotnet Order.Worker.dll` and nothing else, and the
#   image declares no HEALTHCHECK). Without a probe, `docker compose ps`
#   reports the worker as "Up" - which only says the process has not exited.
#   It cannot say the worker is still CONNECTED to the broker it consumes
#   from, so a worker whose AMQP connection died (broker restart, network
#   partition, credentials rotated) stays "Up" while it silently stops
#   consuming the `orders` queue and stops dispatching the outbox.
#
#   The Kubernetes Deployment says the same thing in prose
#   (infrastructure/kubernetes/base/worker/deployment.yaml: "No
#   liveness/readiness httpGet ... A fabricated /health probe here would be a
#   lie"). A probe that invents an HTTP endpoint the worker does not serve
#   would prove nothing - so this probe asserts something the worker REALLY
#   does, in two layers:
#
#   LAYER 1 (always) - the transport is up.
#     The worker holds an ESTABLISHED TCP connection to the broker's AMQP
#     port (5672). That one connection carries EVERY role the worker has:
#       * OrderProcessorHost             - consumes the `orders` queue
#       * AutomationWorkerHost           - consumes the `automation.events` queue
#       * OutboxDispatcherHostedService  - publishes the outbox to the exchange
#     Read from the kernel's socket table (/proc/net/tcp): no credentials, no
#     secret in the image, no extra published port, and it cannot silently
#     break when an optional plugin is absent.
#
#   LAYER 2 (when the broker's management API answers) - THIS container is an
#     ACTIVE CONSUMER of the `automation.events` queue.
#     "A TCP socket to 5672 exists" is weaker than it looks: it is also true
#     for the API container, which publishes to the same broker. The
#     management API can tell the two apart, because it reports the peer of
#     every registered consumer. The worker asks: is one of MY container's IP
#     addresses the peer of an ACTIVE consumer on `automation.events`? If yes,
#     this container is doing its job. If the management API cannot be used
#     (plugin disabled, credentials rejected), the probe falls back to LAYER
#     1 alone rather than failing a container that is demonstrably alive.
#
# EXIT CODES
#   0 - healthy
#   1 - unhealthy (no broker connection, or the broker says this container is
#       not an active consumer)
#
# ENVIRONMENT
#   ConnectionStrings__RabbitMQ  the worker's AMQP URI. Used to derive the
#                                 broker host AND the credentials for LAYER 2,
#                                 so the probe follows RABBITMQ_USER /
#                                 RABBITMQ_PASSWORD overrides automatically and
#                                 never carries a secret of its own.

set -uo pipefail

# 5672 in hexadecimal - how /proc/net/tcp prints the port.
readonly AMQP_PORT_HEX="1628"

# TCP state 01 == ESTABLISHED.
readonly TCP_ESTABLISHED="01"

# The RabbitMQ management API port and the queue the worker's
# AutomationWorkerHost consumes.
readonly MGMT_PORT="15672"
readonly AUTOMATION_QUEUE="automation.events"

# ---------------------------------------------------------------------------
# LAYER 1: is the AMQP transport up?
# ---------------------------------------------------------------------------
has_live_amqp_connection() {
  local table
  for table in /proc/net/tcp /proc/net/tcp6; do
    [ -r "$table" ] || continue
    # /proc/net/tcp columns: sl local_address rem_address st ...
    #   $3 = rem_address ("<hex ip>:<hex port>") - the REMOTE port is the
    #        broker's fixed 5672; the local port is an ephemeral source port
    #        chosen by the kernel and therefore cannot be matched on.
    #   $4 = st
    if awk -v port=":${AMQP_PORT_HEX}" -v state="${TCP_ESTABLISHED}" \
        '$3 ~ port"$" && $4 == state { found = 1 }
         END { exit found ? 0 : 1 }' "$table"; then
      return 0
    fi
  done
  return 1
}

if ! has_live_amqp_connection; then
  echo "order-worker: no ESTABLISHED connection to the AMQP port (5672) - the worker is not connected to the broker." >&2
  exit 1
fi

# ---------------------------------------------------------------------------
# LAYER 2: is THIS container an active consumer of the automation queue?
# ---------------------------------------------------------------------------
# Everything below is best-effort: any failure to reach or understand the
# management API falls through to "healthy" (LAYER 1 already proved the
# transport is up). Only a management API that ANSWERS and reports this
# container as a NON-consumer can make the probe fail.
check_is_active_consumer() {
  local uri="${ConnectionStrings__RabbitMQ:-}"
  [ -n "$uri" ] || return 0 # nothing to derive the broker from -> LAYER 1 only

  # amqp://user:pass@host:port/ -> credentials and host
  local creds host
  creds="${uri#*://}"
  creds="${creds%@*}"
  local hostport="${uri##*@}"
  hostport="${hostport%%/*}"
  host="${hostport%%:*}"
  [ -n "$creds" ] && [ -n "$host" ] || return 0

  # This container's own addresses, one per network it joined. The broker
  # reports the peer of a consumer as that peer's address on the network the
  # connection came from - which is one of these.
  local own_ips
  own_ips="$(hostname -i 2>/dev/null | tr ' ' '\n' | grep -v '^$' || true)"
  [ -n "$own_ips" ] || return 0

  local auth response status
  auth="$(printf '%s' "$creds" | base64 | tr -d '\n')"

  # The whole exchange runs in a command substitution so that a refused
  # connection is swallowed instead of printing "Connection refused" on every
  # probe - an unreachable management API is a FALLBACK, not an error.
  #
  # `exec 2>/dev/null` comes FIRST on purpose: bash applies the redirections
  # of a command left to right, and a failing `exec 3<>...` prints its error
  # to the stderr that is in effect AT THAT MOMENT. Silencing stderr before
  # the connection is opened is what keeps the probe quiet.
  response="$(
    exec 2>/dev/null
    exec 3<>"/dev/tcp/${host}/${MGMT_PORT}" || exit 0
    {
      printf 'GET /api/queues/%%2F/%s HTTP/1.1\r\n' "${AUTOMATION_QUEUE}"
      printf 'Host: %s\r\n' "${host}"
      printf 'Authorization: Basic %s\r\n' "${auth}"
      printf 'Connection: close\r\n\r\n'
    } >&3
    cat <&3
  )"
  [ -n "${response}" ] || return 0

  # The status line is the first line of the response.
  status="$(printf '%s' "${response}" | head -1)"
  case "${status}" in
    *' 200 '*) : ;; # the API answered - its verdict is authoritative
    *) return 0 # no usable answer -> LAYER 1 only
  esac

  # The consumer must be ACTIVE, and it must be THIS container.
  printf '%s' "${response}" | grep -q '"activity_status":"up"' || {
    echo "order-worker: the broker reports no ACTIVE consumer on '${AUTOMATION_QUEUE}'." >&2
    return 1
  }

  local ip
  while IFS= read -r ip; do
    [ -n "${ip}" ] || continue
    if printf '%s' "${response}" | grep -q "\"peer_host\":\"${ip}\""; then
      return 0 # this container IS the consumer
    fi
  done <<<"${own_ips}"

  echo "order-worker: the broker's consumer on '${AUTOMATION_QUEUE}' belongs to another host - this container is not consuming." >&2
  return 1
}

check_is_active_consumer
exit $?
