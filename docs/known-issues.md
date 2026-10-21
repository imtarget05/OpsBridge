# Known Issues (OpsBridge)

Two open, low-severity issues that affect **local** runs only. Neither breaks
CI (the GitHub runner has no ControlCenter and no host port conflicts), and
neither affects a deployed environment.

---

## 1. macOS ControlCenter occupies port 5000 — use `OPSBRIDGE_API_URL`

**Severity:** low (local development only)
**Status:** open — worked around, not designed away

### The problem

On macOS, **ControlCenter** (the AirPlay receiver) binds `*:5000` on both IPv4
and IPv6:

```
$ lsof -nP -iTCP:5000 -sTCP:LISTEN
COMMAND    PID    USER           FD   TYPE DEVICE  NODE NAME
ControlCe 1089   mainguyenbinhtan 11u  IPv4  ...    TCP *:5000 (LISTEN)
ControlCe 1089   mainguyenbinhtan 12u  IPv6  ...    TCP *:5000 (LISTEN)
```

Anything that hard-codes `127.0.0.1:5000` therefore either fails to bind or —
worse — silently talks to the AirPlay receiver instead of the application:

| What | Hard-codes 5000 | Consequence on macOS |
| --- | --- | --- |
| `src/OpsBridge.Web/vite.config.ts` | `process.env.OPSBRIDGE_API_URL ?? 'http://127.0.0.1:5000'` | the Vite proxy for `/api` hits ControlCenter, so every browser journey fails with a confusing error instead of a connection-refused |
| `scripts/opsbridge-wait-for-api.sh` | `API="${OPSBRIDGE_API:-http://127.0.0.1:5000}"` | the cold-start wait never sees the API come up |
| `scripts/opsbridge-warmup-api.sh` | `API="${OPSBRIDGE_API:-http://127.0.0.1:5000}"` | the warm-up warms nothing |

Note the IPv6 subtlety that made this worse before the fix: on macOS
`localhost` resolves to `::1` **first**, and the AirPlay receiver owns
`[::1]:5000`. That is why the Vite proxy targets the literal IPv4 address
`127.0.0.1` rather than `localhost`, and why the old
`NODE_OPTIONS=--dns-result-order=ipv4first` flag is no longer needed.

### The workaround (already in the code)

Both the web tier and the shell scripts take an override:

```bash
# The web tier: the Vite dev server proxies /api to THIS address.
OPSBRIDGE_API_URL=http://127.0.0.1:15020 npm run dev

# The shell scripts: point them at the same address.
OPSBRIDGE_API=http://127.0.0.1:15020 bash scripts/opsbridge-wait-for-api.sh
OPSBRIDGE_API=http://127.0.0.1:15020 bash scripts/opsbridge-warmup-api.sh
```

The same override is what the **containerised** journey uses, only through the
other variable:

```bash
# playwright.config.ts skips the Vite dev server entirely and talks to the
# published port of the nginx container instead.
PLAYWRIGHT_BASE_URL=http://127.0.0.1:18090 npx playwright test --workers=1
```

### Verified on this machine (2026-10-21)

The browser journey was run against a disposable PostgreSQL on port `55440`
and the API on port `15020` (neither collides with ControlCenter):

```
OPSBRIDGE_API_URL="http://127.0.0.1:15020" npx playwright test --workers=1
→ 7 passed (12.3s)   # against the containerised stack, PLAYWRIGHT_BASE_URL=…
```

### What is left to do

Nothing is broken; the issue is that the **defaults still say 5000**. A
follow-up could make the local scripts fail fast with a clear message when
port 5000 is owned by another process, instead of timing out silently:

```bash
# proposed: fail fast instead of timing out for 120 s
if ! nc -z 127.0.0.1 5000 2>/dev/null; then
  echo "::error::nothing is listening on 127.0.0.1:5000 - on macOS ControlCenter owns it. Set OPSBRIDGE_API_URL." >&2
fi
```

Until then: **on macOS, always pass `OPSBRIDGE_API_URL` / `OPSBRIDGE_API`
when running the web tier or the OpsBridge scripts locally.**

---

## 2. `order-worker` has no healthcheck in compose (RESOLVED)

**Severity:** low
**Status:** **fixed** — see below

### The problem (as found)

`order-worker` is a `BackgroundService` host with **no HTTP listener**
(`Dockerfile.worker` runs `dotnet Order.Worker.dll` and nothing else), so
`docker-compose.yml` had no `healthcheck` for it. The consequence was that
`docker compose ps` reported it as `Up`, not `Up (healthy)`:

```
NAME                        STATUS
flashsale-order-api         Up 43 seconds (healthy)
flashsale-order-worker      Up 43 seconds                <-- no healthcheck
flashsale-payment-service   Up About a minute (healthy)
flashsale-postgres          Up 2 minutes (healthy)
flashsale-rabbitmq          Up 2 minutes (healthy)
flashsale-redis             Up 2 minutes (healthy)
```

`Up` only says the process has not exited. It cannot say the worker is still
**connected to the broker**, so a worker whose AMQP connection died (broker
restart, network partition, rotated credentials) would stay `Up` while it
silently stopped consuming the `orders` queue and stopped dispatching the
outbox. The Kubernetes Deployment says the same thing in prose
(`infrastructure/kubernetes/base/worker/deployment.yaml`): *"No
liveness/readiness httpGet … A fabricated /health probe here would be a
lie."*

### The fix

A probe that invents an HTTP endpoint the worker does not serve would prove
nothing, so the probe asserts what the worker **really** does, in two layers:

1. **The transport is up** — the worker holds an ESTABLISHED TCP connection to
   the broker's AMQP port (5672), read from the kernel's socket table
   (`/proc/net/tcp`). That one connection carries every role the worker has:
   the `orders` consumer, the `automation.events` consumer and the outbox
   publisher.
2. **This container is an active consumer** — the RabbitMQ management API
   reports the peer of every registered consumer; the probe checks that one of
   *this container's* addresses is the peer of an `automation.events` consumer
   whose `activity_status` is `up`. (Layer 1 alone would also be true for the
   API container, which publishes to the same broker.) If the management API
   cannot be reached at all, the probe falls back to layer 1 rather than
   failing a container that is demonstrably alive.

Implementation:

* `scripts/opsbridge-worker-healthcheck.sh` — the probe.
* `Dockerfile.worker` — bakes the probe into the image at
  `/app/opsbridge-worker-healthcheck.sh` (readable and executable by the
  non-root `app` user, uid 1654).
* `docker-compose.yml` — `order-worker` gains a `healthcheck` block
  (15 s interval, 10 s timeout, 5 retries, 30 s start period).

No secret is baked into the image: the probe derives the broker host and the
credentials from the worker's own `ConnectionStrings__RabbitMQ`, so it follows
`RABBITMQ_USER` / `RABBITMQ_PASSWORD` overrides automatically.

### Verified on this machine (2026-10-21)

```
$ docker compose ps
NAME                        STATUS
flashsale-order-api         Up 39 minutes (healthy)
flashsale-order-worker      Up 3 minutes (healthy)      <-- healthcheck added
flashsale-payment-service   Up 40 minutes (healthy)
flashsale-postgres          Up 40 minutes (healthy)
flashsale-rabbitmq          Up About a minute (healthy)
flashsale-redis             Up 40 minutes (healthy)
```

And the negative test — the probe really does detect a broken worker. With
RabbitMQ stopped, the worker keeps running but is no longer consuming, and the
probe says so:

```
$ docker stop flashsale-rabbitmq
$ docker inspect flashsale-order-worker --format '{{.State.Health.Status}}'
unhealthy     (FailingStreak=5)
```

After RabbitMQ came back, the worker reconnected and the probe recovered on
its own:

```
$ docker start flashsale-rabbitmq
$ docker inspect flashsale-order-worker --format '{{.State.Health.Status}}'
healthy
```

### Note for the Kubernetes side

The Kubernetes `order-worker` Deployment still has no `livenessProbe` /
`readinessProbe`, and that is intentional: a Pod probe needs an HTTP or exec
endpoint, and the worker has neither. The container-level probe used here
does not translate one-to-one into a Pod probe. If a Pod-level probe is
wanted later, the worker would have to grow a real health endpoint — a
separate decision, not taken here.
