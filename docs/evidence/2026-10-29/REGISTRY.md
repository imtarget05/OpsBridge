# Evidence Registry — OpsBridge (P3.7)

**Date:** 2026-10-29  
**Commit SHA:** `1036d114305d7756f282aed826761e3f51b4c400` (HEAD)  
**Task:** P3.7 — Python Evaluation Harvest — Review & Packaging (OpsBridge)  
**DoD:** every claim "verified" has a supporting file in `docs/evidence/2026-10-29/`.

| # | Evidence | File | Source symlink | Status | Claim covered |
|---|----------|------|----------------|--------|---------------|
| 1 | FlashSale unit test results (186/186) | `unit-tests/flashsale-unit.md` | `source-portfolio-audit.md`, `source-ci-verification.md` | VERIFIED (PASS) | FlashSale unit 186/186 |
| 2 | OpsBridge unit test results (45/45) | `unit-tests/opsbridge-unit.md` | `source-portfolio-audit.md`, `source-ci-verification.md` | VERIFIED (PASS) | OpsBridge unit 45/45 |
| 3 | FlashSale integration test results (81/81) | `integration-tests/flashsale-integration.md` | `source-portfolio-audit.md`, `source-ci-verification.md` | VERIFIED (PASS) | FlashSale integration 81/81 |
| 4 | OpsBridge integration test results (40/40) | `integration-tests/opsbridge-integration.md` | `source-portfolio-audit.md`, `source-ci-verification.md` | VERIFIED (PASS) | OpsBridge integration 40/40 |
| 5 | Contract test results (7/7) | `contract-tests/contract.md` | `source-goods-receipt-contract.cs` | VERIFIED (PASS) | contract 7/7 |
| 6 | Concurrency harness (50 buyers / 10 stock) | `concurrency-harness/concurrency.md` | `source-50-contenders.md` | VERIFIED (PASS) | zero-oversell 50/10 |
| 7 | Outbox crash-window drill | `outbox-crash-window/outbox-crash-window.md` | `source-outbox-crash-window.cs`, `source-outbox-inbox-live.md` | VERIFIED (PASS) | outbox crash window |
| 8 | Reconciliation scenario (OpsBridge) | `reconciliation/reconciliation.md` | `source-reconciliation-uat.md` | VERIFIED (PASS) | FR-C09 reconciliation routes |
| 9 | Backup / restore drills (3 drills) | `backup-restore-drills/restore-drills.md` | `source-drill-001.md`, `source-drill-002.md`, `source-drill-003.md` | VERIFIED (PASS) | backup/restore drills |

## DoD check

- [x] `docs/evidence/2026-10-29/` exists in OpsBridge repo
- [x] Each evidence file has markdown header: task / commit SHA / command / output / status
- [x] Evidence registry (`REGISTRY.md`) available
- [x] Every "verified" claim has a supporting file (9 files, all VERIFIED)
- [x] Source evidence symlinked (single source of truth, no duplication)

## Related evidence (same tree, not copied into this date folder)

- `docs/evidence/ci-verification.md` — CI verification (commit `1ecd302`)
- `docs/evidence/portfolio-audit-2026-10-21.md` — portfolio audit with current counts
- `docs/evidence/2026-10-23-50-contenders-10-units.md` — concurrency 50/10 raw evidence
- `docs/evidence/2026-10-26/opsbridge-outbox-uat-PASS.md` — outbox UAT
- `docs/evidence/2026-10-26/opsbridge-reconciliation-uat-PASS.md` — reconciliation UAT
- `docs/evidence/kubernetes/phase7c-readiness.md` — AKS manifests (not applied)
- `docs/evidence/release/phase6-release-engineering.md` — FlashSale release push to ACR
- `docs/evidence/v2/outbox-inbox-2026-09-24.md` — live outbox/inbox broker-outage cycle
- `docs/evidence/saga/` — saga live logs
- `docs/evidence/backup/` — 3 restore drills
- `test-results/opsbridge/*.trx` — historical TRX artifacts (2026-10-19)
- `test-results/obs-final/*.trx` — historical TRX artifacts (2026-10-20)
