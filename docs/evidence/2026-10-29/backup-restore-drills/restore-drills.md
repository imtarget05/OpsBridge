# Evidence — Backup / restore drills (3 drills)

| Field | Value |
|-------|-------|
| task | P3.7 — Python Evaluation Harvest — Review & Packaging (OpsBridge) |
| commit SHA | 1036d114305d7756f282aed826761e3f51b4c400 (HEAD) |
| date | 2026-10-29 |
| command | `./scripts/backup/postgres-backup.sh` then `./scripts/backup/postgres-restore.sh <dump> <new-db>`, then Order API smoke against restored DB |
| output | 3 drills PASS — drill-001: restore ~1s, Products=1/Orders=2/constraints identical, `GET /health/live` healthy, `GET /api/products/1` stock 98, order decrement correct; drill-002 offsite; drill-003 integrated |
| source | `../../backup/restore-drill-001.md`, `../../backup/restore-drill-002-offsite.md`, `../../backup/restore-drill-003-integrated.md` (symlinked) |
| status | VERIFIED (PASS) |

## Notes

- `pg_dump -Fc` from live compose Postgres → restore into NEW clean database → verify data/constraints → run real Order API against restored DB.
- RPO target ≤ 24h / RTO target ≤ 2h are portfolio reference values, not yet guaranteed (manual-only backup, no off-host copy).
- Drill DB retained for evidence; drop in follow-up cleanup.
