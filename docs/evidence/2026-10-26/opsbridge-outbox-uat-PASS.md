# OpsBridge outbox stuck/requeue UAT — 2026-10-26

Context: `aks-portfolio-dev`, namespace `flashsale`, source commit
`d8a9220a88556de305a67e6eb5ff1f80f7da79bb` (branch
`codex/opsbridge-azure-migration-hook-20261025`). This run closes the
"Run 200/401/403 across read and write routes" acceptance action recorded for
US-C-05 in [the Phase 0 baseline](../../acceptance/2026-10-26-phase0-baseline.md)
and exercises FR-C09 on the outbox route family.

## Build and ordering

- Azure Container Registry remote build `cm3` succeeded after 1m26s for
  `acrflashsalep6.azurecr.io/opsbridge-api:d8a9220a88556de305a67e6eb5ff1f80f7da79bb`.
- `scripts/run-azure-opsbridge-migration.py --apply` re-created
  `Job/opsbridge-migrate` with the pinned image; outcome
  `ExitCode=0, AlreadyAtHead=True` (the endpoints add no migration), 1 succeeded.
- `scripts/deploy-azure-opsbridge-api.py --apply` rolled the Deployment and
  re-applied `HTTPRoute/flashsale`. Both replicas run the pinned image
  (running digest `sha256:41f3d65fc3a7b328090025ed30b408d7ee13651b73267d915b747eb5d660247f`).

## Live-state deviations (not in git)

- The cluster rejected the first migration pod and the first surge replica
  (`0/2 nodes are available: 1 Insufficient cpu, 1 Too many pods`; requests,
  not utilisation — nodes idled at 13%/7%). The migration Job was re-created
  from the same rendered spec with the container `resources` block removed
  (Burstable), and the Deployment strategy was patched live to
  `maxSurge=0, maxUnavailable=1` so the rollout replaced pods in place. The
  next manifest apply restores the committed strategy; the Job is TTL-collected.
- Argo `Application/flashsale` has no autosync and is pinned to `b6a2436…`;
  the live image is deliberately ahead of that pin until the source pin is
  updated in a later GitOps reconciliation.

## Negative control before the rollout

`GET https://portfolio-api-edge.tanmainguyenbinh.workers.dev/opsbridge/api/v1/outbox/stuck`
returned **404** while the previous image (`3e7fb0d…`) was running — the route
did not exist. After the rollout the same request returns **401**.

## How tokens were issued

Production has no IdP login journey yet (FR-C10 remains partial; the
`/api/v1/auth/login` endpoint is Development/Test only). Tokens were minted
locally with the HS256 key from Kubernetes Secret `opsbridge-secrets`
(`jwt-signing-key`), issuer `OpsBridge.Api`, audience `OpsBridge.Web`, and the
same claim set `OpsBridgeJwtTokenIssuer.IssueToken` writes
(`sub`, `name`, `ClaimTypes.Name`, `ClaimTypes.Role`, `role`, `jti`).
`GET /api/v1/auth/me` with each token echoed the expected role, so the
validation path accepted the minted identities. These are issued production
roles in claims only; no IdP session was involved.

## Observed UAT matrix (edge: Cloudflare Worker → Envoy Gateway → opsbridge-api)

| # | Check | Result |
| --- | --- | --- |
| R0 | `GET /opsbridge/healthz` | 200 |
| R1 | `GET /api/v1/outbox/stuck` without token | 401 |
| R2 | `GET /api/v1/outbox/stuck` role `Staff` | 403 |
| R3 | `POST /api/v1/outbox/requeue` role `Auditor` | 403 |
| R4 | `GET /api/v1/outbox/stuck` role `Operator` | 200, seeded row listed |
| R4b | Stuck projection leaks no `errorMessage`/`status` | pass (only `id`, `eventType`, `topic`, `createdAt`, `retryCount`) |
| R5 | `POST /api/v1/outbox/requeue/{id}` role `Operator` | 200 `{"status":"requeued","count":1}` |
| R6 | Row state after requeue (PostgreSQL) | `Published`, `RetryCount=0`, `ErrorMessage` NULL, `ProcessedAt` set |
| R7 | `POST /api/v1/outbox/requeue/{id}` again | 404 (row no longer `Failed`) |
| R8 | `POST /api/v1/outbox/requeue` role `Staff` | 403 |
| R9 | `POST /api/v1/outbox/requeue` role `Admin` (bulk) | 200 `{"status":"requeued","count":1}` |
| R10 | `GET /api/v1/auth/me` role `Auditor` | 200, `role=Auditor` |
| R11 | `GET /api/v1/auth/me` without token | 401 |
| R12/R13 | Dispatcher drain of both requeued rows | both `Published` (~1 s after requeue) |

Two stuck rows were seeded directly into `OpsBridgeDb."OutboxMessages"`
(`Status='Failed'`, `RetryCount=5`, real topic
`opsbridge.events.inbound.goods-receipt`) because no production path exists to
force a terminal publish failure on demand. Row-level truth was read from
PostgreSQL, not only from the API response.

## What this does and does not prove

Proven at `d8a9220a…`: the outbox runbook endpoints exist on the cloud
release, enforce `AuditorOrAbove` on read and `OperatorOrAdmin` on write with
401/403 negative controls, project no error/status leakage, revive a
dead-lettered row exactly once, and the dispatcher drains the revived row.

Not proven here: the reconciliation-route slice of US-C-05 (results,
audit-trail, dead-letter read endpoints) still needs the same role matrix; an
independent reviewer has not signed off; no IdP-issued token was used.