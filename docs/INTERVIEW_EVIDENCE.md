# FlashSale-Backend — Interview Evidence Kit

> **Current correction (2026-10-21):** This kit's older CI and test counts are
> historical. The prior concurrency harness could falsely print `RESULT: PASS`
> for 3/47/stock 7 because resync was dry-run and it checked conservation only.
> The working-tree fix passed six verdict/resync regressions and one disposable
> local 50/10 run (10 accepted, 40 × 409, 10 persisted, stock 0). Remote CI
> remains unverified; see
> [`docs/evidence/portfolio-audit-2026-10-21.md`](evidence/portfolio-audit-2026-10-21.md).

> Event-driven flash-sale reliability platform (.NET 10, PG + Redis + RabbitMQ).
> **Zero-oversell invariant CI-enforced**: 50 buyers vs stock 10 → exactly 10
> sales, stock 0 (`RESULT: PASS` or the build fails). Unit suite **162/162**.
> Latency p95 29 ms tiered vs 574 ms naive is a local single-run observation,
> NOT a CI gate — re-measure before quoting. `docs/EVIDENCE.md` is the claim→
> file index; `docs/interview-demo/` holds the automation runbook + `demo.sh`.

---

## 1. STAR story

**Situation.** Flash-sale hype traffic: thousands race for 10 items. The naive
sync path oversold (reproduced: 50/50 accepted, stock 10→9) and, once fixed
with atomic UPDATE, queued on the hot row (p95 245→574 ms at 200 concurrency,
90% of PG round-trips just to say "no").

**Task.** Hold the zero-oversell invariant at hype concurrency while shedding
hopeless buyers before the DB, absorbing spikes asynchronously, and surviving
Redis/broker outages without overselling or losing orders.

**Action.**
- Reproduced oversell FIRST (ADR-001), then atomic conditional UPDATE (ADR-002:
  10 accepted / 40 rejected / stock 0), then Redis Lua CAS fast-fail filter
  with idempotency ledger (ADR-003: p95 574→29 ms), never source of truth
  (PG authoritative, drift → DLQ + resync runbook).
- RabbitMQ buffer + worker persist + ack-after-persist + idempotency keys +
  DLQ (ADR-004/005: RabbitMQ local + Service Bus Azure behind one port;
  InMemory bounded-5,000 fallback with 503 backpressure).
- Pool headroom (API 80 < server 100, ADR-014), auth closed-set roles + source
  guard (ADR-013), pg_dump -Fc + SHA-256 DR drills, Clean-Architecture
  dependency-direction tests.

**Result.** Oversell = 0 on every CI run (concurrency-harness `RESULT: PASS`
gate); unit 162/162; automation smokes green (payment 21/21, low-stock 11/11,
daily-report 12/12); kind v2 saga 21/21 + outbox recovery 14/14.

## 2. System-design Q&A

**Q1: Why Redis in front instead of just scaling Postgres?**
The bottleneck was not capacity but wasted work: 90% of requests burned a PG
round-trip + hot-row lock wait only to be rejected. A one-RTT Lua CAS rejects
hopeless buyers before the DB, so PG handles only winnable writes.
Trade-off: drift risk — contained by PG-as-truth + `StockDriftException` DLQ +
`POST /internal/resync-stock/{id}`. Redis down degrades to the sync DB path;
it can slow us, never oversell us.

**Q2: Why at-least-once + idempotency instead of exactly-once messaging?**
Exactly-once does not exist across broker + DB without transactions spanning
both. At-least-once (ack-after-persist) may redeliver, and idempotency keys +
guarded UPDATEs make redelivery a no-op. The alternative (ack-before-persist)
loses orders on worker crash — the worse failure.

**Q3: Why cap the API pool below the server max (80/100)?**
Phase-4 reproduced pool saturation locking out operators mid-incident. Leaving
headroom is a one-line SRE lesson: the DB always needs a lane for humans
during the fire. Cost: slightly lower peak throughput — knowingly traded for
operability.

**Q4: Why Clean-Architecture ports for every infra addition?**
Each broker/cache/DB entered as a port + adapter, so RabbitMQ→Service Bus was
an adapter swap with `OrderProcessor` untouched, and `ArchitectureTests.cs`
fails the build on direction violations. Upfront ceremony pays at every
migration; the repo has the receipts (ADR-005, ADR-012).

## 3. Live-demo script (5 steps)

```bash
# 1. Infra + build (Postgres/Redis/RabbitMQ via compose)
docker compose up -d && dotnet build AzureFlashSale.slnx
# 2. Zero-oversell harness: 50 buyers vs stock 10 -> RESULT: PASS
python3 load-tests/concurrency/oversell_demo.py   # 10 sales, stock 0, accepted+rejected==50
# 3. One-command auth-surface demo (register->order->refresh->logout->OpenAPI)
bash docs/interview-demo/demo.sh                  # DEMO_PORT=5099
# 4. Payment automation: timeout -> remind -> cancel -> release -> audit
bash scripts/payment-automation-smoke.sh          # 21/21
# 5. Unit suite (fast, no infra)
dotnet test tests/UnitTests/FlashSale.UnitTests.csproj -c Release   # 162/162
```

| Step | Command / URL | Expected |
|---|---|---|
| 2 | `oversell_demo.py` | `RESULT: PASS`: 10 persisted sales, stock 0 |
| 3 | `docs/interview-demo/demo.sh` | Register→Completed→refresh-rotate→logout→metrics |
| 4 | `payment-automation-smoke.sh` | 21/21: remind→cancel→release→audit row |
| 5 | `dotnet test` unit | 162/162 green |
