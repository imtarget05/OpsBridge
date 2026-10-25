++ # OpsBridge Reconciliation Routes UAT — 2026-10-26
++ Context: `aks-portfolio-dev`, namespace `flashsale`, source commit
++ `d8a9220a88556de305a67e6eb5ff1f80f7da79bb` (deployed via `2ebee24`).
++ This run closes the "Run 200/401/403 across read and write routes" acceptance
++ action for US-C-05 and exercises FR-C09 on the reconciliation route family.
++
++ ## UAT Matrix (direct port-forward, JWT from K8s secret)
++ | R0  | `GET /healthz` | — | 200 |
++ | R1  | `GET /api/v1/purchase-orders` no token | — | 401 |
++ | R2  | `GET /api/v1/purchase-orders` role Staff | Staff | 403 |
++ | R4  | `GET /api/v1/purchase-orders` role Operator | Operator | 200 |
++ | R5  | `GET /api/v1/goods-receipts` role Operator | Operator | 200 |
++ | R6  | `GET /api/v1/invoices` role Operator | Operator | 200 |
++ | R7  | `GET /api/v1/reconciliation/batches` role Operator | Operator | 200 |
++ | R8  | `GET /api/v1/reconciliation/discrepancies` role Auditor | Auditor | 200 |
++ | R9  | `GET /api/v1/reconciliation/audit-trail` role Auditor | Auditor | 200 |
++ | R10 | `GET /api/v1/dashboard/metrics` role Auditor | Auditor | 200 |
++ | R11 | `GET /api/v1/dashboard/metrics` role Staff | Staff | 403 |
++ | R12 | `GET /api/v1/events/dead-letter` role Auditor | Auditor | 200 |
++ | R13 | `GET /api/v1/events/dead-letter` role Operator | Operator | 200 |
++ | R14 | `GET /api/v1/auth/me` role Operator | Operator | 200 |
++ | R15 | `GET /api/v1/auth/me` no token | — | 401 |
++
++ **Summary: 15/15 passed.** All reconciliation routes enforce FR-C09.
++
++ ## Token Issuance
++ JWT signing key read from K8s Secret `opsbridge-secrets` (`jwt-signing-key`).
++ HS256, issuer `OpsBridge.Api`, audience `OpsBridge.Web`.
++ `GET /api/v1/auth/me` confirmed role claims accepted.