# Evidence — Outbox crash-window drill

| Field | Value |
|-------|-------|
| task | P3.7 — Python Evaluation Harvest — Review & Packaging (OpsBridge) |
| commit SHA | 1036d114305d7756f282aed826761e3f51b4c400 (HEAD) |
| date | 2026-10-29 |
| command | `dotnet test tests/IntegrationTests/FlashSale.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~OutboxCrashWindowTests' --verbosity quiet` |
| output | PASS — zero orders persisted, PostgreSQL stock unchanged (10), exactly one `inventory.reserved` + one `inventory.released` outbox row, two broker deliveries, no third ready message |
| source | `tests/IntegrationTests/OutboxCrashWindowTests.cs` (symlinked) and `../../v2/outbox-inbox-2026-09-24.md` (live broker-outage → DLQ → requeue cycle) |
| status | VERIFIED (PASS) |

## Notes

- Proves the transactional outbox closes the DB→broker loss window during a broker crash window.
- Consumer inbox deduplicates by stable event ID (5× duplicate → 1 inbox row).
- Saga 21/21 live on the Kafka path.
- Does not promise exactly-once transport or instant delivery; exactly-once *business effect* comes from idempotency keys.
