#!/usr/bin/env bash
# Waits for the OpsBridge API to answer its first request, and FAILS the job
# when the cold start is too slow.
#
# WHY THIS IS A SCRIPT (and not an inline `run:` block)
#   The cold-start budget is part of the contract of the CI job, so it has to
#   be executable and verifiable OUTSIDE GitHub Actions.
#
# THE COLD-START BUDGET
#   The first request to a cold .NET 10 process pays the JIT cost: measured
#   ~9.5 s for the very first /healthz on the CI runner. The budget is 30 s:
#   about 3x the measured value - room for a loaded 2-vCPU runner - and still
#   far below the 60 s that used to pass unnoticed.
#
#   Why it matters: the old step used `curl --max-time 120`, so a cold start
#   that regressed to 60 s was ACCEPTED here, and the regression only surfaced
#   later as a confusing timeout inside the browser run (the pre-existing spec
#   asserts the post-login URL with a 5 s expectation timeout). A cold-start
#   regression is exactly the kind of defect this job exists to catch, so it
#   has to fail HERE, with this diagnosis.
#
# ENVIRONMENT OVERRIDES
#   OPSBRIDGE_API                       base URL of the API (default 127.0.0.1:5000)
#   OPSBRIDGE_COLD_START_BUDGET_SECONDS budget in seconds (default 30)
#   OPSBRIDGE_HEALTH_ATTEMPTS           how often to retry (default 60)
#   OPSBRIDGE_HEALTH_INTERVAL_SECONDS   pause between attempts (default 2)

set -euo pipefail

API="${OPSBRIDGE_API:-http://127.0.0.1:5000}"
COLD_START_BUDGET_SECONDS="${OPSBRIDGE_COLD_START_BUDGET_SECONDS:-30}"
HEALTH_ATTEMPTS="${OPSBRIDGE_HEALTH_ATTEMPTS:-60}"
HEALTH_INTERVAL_SECONDS="${OPSBRIDGE_HEALTH_INTERVAL_SECONDS:-2}"

started=$(date +%s)

for attempt in $(seq 1 "${HEALTH_ATTEMPTS}"); do
  if curl -sf --max-time 120 "${API}/healthz"; then
    elapsed=$(( $(date +%s) - started ))
    echo " <- API healthy after ${attempt} attempt(s), ${elapsed}s after the first attempt"
    if [ "${elapsed}" -gt "${COLD_START_BUDGET_SECONDS}" ]; then
      echo "::error::The API answered its first request after ${elapsed}s, which is over the ${COLD_START_BUDGET_SECONDS}s cold-start budget. A cold-start regression must fail this job HERE, with this diagnosis - not later as a timeout inside the browser run." >&2
      exit 1
    fi
    exit 0
  fi
  sleep "${HEALTH_INTERVAL_SECONDS}"
done

echo "::error::The API never became healthy at ${API}/healthz after ${HEALTH_ATTEMPTS} attempts." >&2
exit 1
