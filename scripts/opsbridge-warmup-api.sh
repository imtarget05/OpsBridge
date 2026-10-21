#!/usr/bin/env bash
# Warms the OpsBridge API before the browser run of the CI browser journey.
#
# WHY THIS IS A SCRIPT AND NOT AN INLINE `run:` BLOCK
#   The budgets below are part of the contract of the CI job, so they have to
#   be executable and verifiable OUTSIDE GitHub Actions: a cold-start
#   regression (the first request to a fresh .NET process paying the JIT
#   cost) is exactly the kind of defect this job exists to catch, and the
#   budget that catches it has to be testable.
#
# WHAT IT DOES
#   1. logs in once - the first POST /api/v1/auth/login pays the biggest
#      share of the cold start,
#   2. requests every API route the browser journey touches, so the JIT and
#      the EF query compilation are paid HERE, in the job, with no assertion
#      attached, instead of inside the browser run (whose budget is 5 s for
#      the pre-existing spec's post-login URL assertion),
#   3. FAILS, with a diagnostic, when a single request exceeds
#      REQUEST_BUDGET_SECONDS, or when the whole phase exceeds
#      WARMUP_BUDGET_SECONDS.
#
# THE BUDGETS (and why these numbers)
#   REQUEST_BUDGET_SECONDS = 30
#     The measured cold start of a fresh .NET 10 process is ~9.5 s for the very
#     first request (/healthz). 30 s leaves ~3x headroom for a loaded CI
#     runner (2 vCPU shared by postgres + API + vite + chromium) and still
#     fails a cold start that regressed to 60 s - the regression this budget
#     exists to catch. A regression is NOT allowed to hide behind the 120 s
#     curl timeout that the old warm-up used: that timeout is exactly what
#     let a 60 s cold start pass the warm-up and then blow the browser's 5 s
#     budget with a confusing timeout instead of a clear diagnosis.
#
#   WARMUP_BUDGET_SECONDS = 60
#     The whole phase (login + 10 routes) must finish inside a minute. The
#     per-request budget already catches a single slow request; this one
#     catches a phase that is individually fine but collectively too slow
#     (many small regressions, or a runner that is starved).
#
# ENVIRONMENT OVERRIDES (used by the CI job and by the local verification)
#   OPSBRIDGE_API                    base URL of the API (default 127.0.0.1:5000)
#   OPSBRIDGE_REQUEST_BUDGET_SECONDS per-request budget in seconds (default 30)
#   OPSBRIDGE_WARMUP_BUDGET_SECONDS  whole-phase budget in seconds (default 60)

set -euo pipefail

API="${OPSBRIDGE_API:-http://127.0.0.1:5000}"
REQUEST_BUDGET_SECONDS="${OPSBRIDGE_REQUEST_BUDGET_SECONDS:-30}"
WARMUP_BUDGET_SECONDS="${OPSBRIDGE_WARMUP_BUDGET_SECONDS:-60}"

# Every route the browser journey touches. The variable is named `route`, not
# `path`: `path` is a SPECIAL VARIABLE in zsh (it is tied to $PATH), and a
# `for path in ...` loop in zsh corrupts the PATH array - the symptom is
# "zsh:1: command not found: curl". GitHub's default shell is bash today, so
# the bug is latent, not active; the name is wrong either way.
ROUTES=(
  purchase-orders
  goods-receipts
  invoices
  dashboard/metrics
  reconciliation/discrepancies
  reconciliation/audit-trail
  reconciliation/batches
  events/dead-letter
  connectors
  ingestion/events
)

started_at=$(date +%s)

echo "Warming the OpsBridge API at ${API} (per-request budget ${REQUEST_BUDGET_SECONDS}s, phase budget ${WARMUP_BUDGET_SECONDS}s)"

login_response=""
login_rc=0
login_response=$(curl -s --max-time "${REQUEST_BUDGET_SECONDS}" \
  -X POST "${API}/api/v1/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"username":"warmup_operator","role":"Operator"}') || login_rc=$?

if [ "${login_rc}" -ne 0 ]; then
  echo "::error::The warm-up login request POST ${API}/api/v1/auth/login failed after at most ${REQUEST_BUDGET_SECONDS}s (curl exit ${login_rc}). The API cold start regressed - this job must fail here, not inside the browser run." >&2
  exit 1
fi

TOKEN=$(printf '%s' "${login_response}" | python3 -c 'import json,sys; print(json.load(sys.stdin)["accessToken"])') || {
  echo "::error::The warm-up login response did not contain an accessToken: ${login_response}" >&2
  exit 1
}

for route in "${ROUTES[@]}"; do
  request_started_at=$(date +%s)
  code=""
  rc=0
  code=$(curl -s --max-time "${REQUEST_BUDGET_SECONDS}" -o /dev/null -w '%{http_code}' \
    -H "Authorization: Bearer ${TOKEN}" "${API}/api/v1/${route}") || rc=$?
  request_elapsed=$(( $(date +%s) - request_started_at ))

  if [ "${rc}" -ne 0 ] || [ "${code}" != "200" ]; then
    echo "::error::Warm-up request GET /api/v1/${route} failed after ${request_elapsed}s (curl exit ${rc}, http status '${code}'). The per-request budget is ${REQUEST_BUDGET_SECONDS}s: a request slower than that means the API cold start regressed, and this job must fail with that diagnosis instead of letting the browser run time out." >&2
    exit 1
  fi

  echo "warm /api/v1/${route} -> ${code} (${request_elapsed}s)"
done

elapsed=$(( $(date +%s) - started_at ))
if [ "${elapsed}" -gt "${WARMUP_BUDGET_SECONDS}" ]; then
  echo "::error::The OpsBridge API warm-up took ${elapsed}s, which is over the ${WARMUP_BUDGET_SECONDS}s budget for the whole phase. The API is slower than this job tolerates (individually fine requests, but too slow in aggregate)." >&2
  exit 1
fi

echo "Warm-up finished in ${elapsed}s (budget ${WARMUP_BUDGET_SECONDS}s)."
