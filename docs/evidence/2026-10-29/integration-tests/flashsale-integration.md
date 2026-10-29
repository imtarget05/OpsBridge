# Evidence — FlashSale integration test results (81/81)

| Field | Value |
|-------|-------|
| task | P3.7 — Python Evaluation Harvest — Review & Packaging (OpsBridge) |
| commit SHA | 1036d114305d7756f282aed826761e3f51b4c400 (HEAD) |
| date | 2026-10-29 |
| command | `dotnet test tests/IntegrationTests/FlashSale.IntegrationTests.csproj -c Release --no-restore --verbosity quiet` |
| output | `Passed! - Failed: 0, Passed: 81, Skipped: 0, Total: 81` (real PostgreSQL/Redis/RabbitMQ via Testcontainers) |
| source | `../../portfolio-audit-2026-10-21.md` (FlashSale integration 81/81) |
| status | VERIFIED (PASS) |

## Notes

- Runs against real `postgres:15-alpine`, `redis:7-alpine`, `rabbitmq:3-management-alpine` Testcontainers via `FlashSaleFixture`.
- Each test gets a fresh schema (`EnsureDeletedAsync` + `MigrateAsync`).
- Covers order flow, auth flow, outbox crash window, low-stock automation, daily report, payment automation, resilience.
- Count per task spec P3.9: FlashSale integration 81/81.
