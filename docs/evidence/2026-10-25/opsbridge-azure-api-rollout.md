# OpsBridge Azure API rollout — 2026-10-25

Context: `aks-portfolio-dev`, namespace `flashsale`, source commit
`3e7fb0d30dd2a5caffd3a7b9c73ec24367e11bf0`.

## Build and ordering

- Azure Container Registry remote build `cm1` succeeded for
  `acrflashsalep6.azurecr.io/opsbridge-api:3e7fb0d30dd2a5caffd3a7b9c73ec24367e11bf0`
  (digest `sha256:99cabdf650b764d0cee6f19799368ed8d6fbd1b1e907659ad1b2fd5f7c898848`).
- Remote build `cm2` succeeded for the web image at the same source SHA
  (digest `sha256:1b61423add34aa62456bc140c9d157003ff1440d79ad12afeb9be0a81a994459`).
- `scripts/provision-azure-opsbridge.py --apply` created a dedicated
  `OpsBridgeDb`, separate DDL and DML roles, and two Kubernetes Secrets.
  Generated values were not printed or stored in Git. The runtime Secret has
  only the DML database connection, RabbitMQ URI and an independent JWT key.
- `scripts/run-azure-opsbridge-migration.py --apply` completed
  `Job/opsbridge-migrate` with the pinned API image before any API pod rolled.
  The Job is excluded from Argo's combined overlay so subsequent syncs cannot
  race a fresh Deployment against an immutable Job.
- `scripts/deploy-azure-opsbridge-api.py --apply` waited for the API rollout
  before updating `HTTPRoute/flashsale`.

## Observed checks

| Check | Result |
| --- | --- |
| `Deployment/opsbridge-api` | 2/2 Ready, exact pinned image |
| `Job/opsbridge-migrate` | 1 succeeded, `Complete=True`, exact pinned image |
| Gateway route conditions | `Accepted=True`, `ResolvedRefs=True` |
| Cloudflare Edge `/opsbridge/healthz` | HTTP 200 |
| Cloudflare Edge `/opsbridge/health/messaging` | HTTP 200 |
| Cloudflare Edge `/opsbridge/api/v1/auth/me` without token | HTTP 401 |
| Cloudflare Edge `/flashsale/health/live` after route update | HTTP 200 |
| Azure overlay contract tests | 2/2 pass |
| Azure overlay cross-reference validator | 17 objects pass |
| Existing prod manifest validator | pass |
| Runtime PostgreSQL table privileges | 20/20 migrated tables have DML grants |
| Runtime PostgreSQL DDL privileges | database `CREATE=False`, schema `CREATE=False` |
| Kubernetes Secret annotations | empty for both OpsBridge Secrets |

The public web UI is still a separate deployment step. This entry verifies
the OpsBridge API only. Argo reports `OutOfSync` because its pinned source is
still the preceding commit and two pre-existing FlashSale resources also differ;
update the Argo source pin after this PR lands, then inspect those differences
before any broad sync.
