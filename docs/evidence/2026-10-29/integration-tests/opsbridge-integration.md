# Evidence — OpsBridge integration test results (40/40)

| Field | Value |
|-------|-------|
| task | P3.7 — Python Evaluation Harvest — Review & Packaging (OpsBridge) |
| commit SHA | 1036d114305d7756f282aed826761e3f51b4c400 (HEAD) |
| date | 2026-10-29 |
| command | `dotnet test tests/OpsBridge.IntegrationTests/OpsBridge.IntegrationTests.csproj -c Release --no-restore --verbosity quiet` |
| output | `Passed! - Failed: 0, Passed: 40, Skipped: 0, Total: 40` (real PostgreSQL/RabbitMQ via Testcontainers) |
| source | `../../ci-verification.md` and `../../portfolio-audit-2026-10-21.md` (OpsBridge integration 40/40) |
| status | VERIFIED (PASS) |

## Notes

- 17 test files in `tests/OpsBridge.IntegrationTests/` (outbox crash window, outbox endpoints, outbox requeue, outbox metrics, webhook, auth flow, checkout saga concurrency, order flow, async flow, endpoint authorization, daily report, low-stock automation, payment automation, resilience, stuck order, connector ping network policy, migration lifecycle).
- Count per task spec P3.9: OpsBridge integration 40/40.
