# Evidence — Reconciliation scenario (OpsBridge)

| Field | Value |
|-------|-------|
| task | P3.7 — Python Evaluation Harvest — Review & Packaging (OpsBridge) |
| commit SHA | 1036d114305d7756f282aed826761e3f51b4c400 (HEAD) |
| date | 2026-10-29 |
| command | `dotnet test tests/OpsBridge.IntegrationTests/OpsBridge.IntegrationTests.csproj -c Release --filter 'FullyQualifiedName~Reconciliation' --verbosity quiet` |
| output | Reconciliation routes UAT 15/15 PASS — FR-C09 enforced on all reconciliation routes; 401/403 negative controls; Operator/Auditor access verified |
| source | `../../2026-10-26/opsbridge-reconciliation-uat-PASS.md` (symlinked) and `tests/OpsBridge.IntegrationTests/ReconciliationIntegrationTests.cs` |
| status | VERIFIED (PASS) |

## Notes

- OpsBridge owns PO/GR/invoice snapshots and allocation; never writes FlashSale order state directly (ADR-017).
- Three-way matching: purchase order × goods receipt × invoice; reviewer action commits or releases allocation with audit trail.
- Route `Review` policy allows FinanceReviewer/Admin; Operator may run reconciliation but cannot approve via HTTP.
- No real ERP or financial settlement claim.
