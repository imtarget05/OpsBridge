# Evidence — Concurrency harness (50 buyers / 10 stock)

| Field | Value |
|-------|-------|
| task | P3.7 — Python Evaluation Harvest — Review & Packaging (OpsBridge) |
| commit SHA | 1036d114305d7756f282aed826761e3f51b4c400 (HEAD) |
| date | 2026-10-29 |
| command | `python3 load-tests/concurrency/oversell_demo.py --stock 10 --requests 50 --psql-container <pg> --settle-seconds 3` |
| output | `RESULT: PASS` — 10 accepted, 40 × HTTP 409, 0 transport failures, 10 orders persisted, final PostgreSQL stock 0 (p95 131 ms, single local run) |
| source | `../../2026-10-23-50-contenders-10-units.md` (symlinked) |
| status | VERIFIED (PASS) |

## Notes

- Zero-oversell invariant: exactly 10 of 50 concurrent buyers succeed, 40 rejected with 409, no negative/phantom stock.
- Redis Lua CAS pre-filter + PostgreSQL conditional UPDATE (`AvailableStock >= qty`) in one transaction.
- Enforced on every push via `concurrency-harness` CI job (`RESULT: PASS` required).
- Do not claim latency (p95 131 ms is one local run, not an SLO).
