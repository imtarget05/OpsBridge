# OpsBridge — Order Reliability (FlashSale) + Event-Driven Operations

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-4169E1?logo=postgresql)](https://postgresql.org/)
[![Redis](https://img.shields.io/badge/Redis-Cache-DC382D?logo=redis)](https://redis.io/)
[![RabbitMQ](https://img.shields.io/badge/RabbitMQ-Messaging-FF6600?logo=rabbitmq)](https://rabbitmq.com/)

**This repository contains two products in one tree.**

| Product | What it is | Code | Decision record |
|---|---|---|---|
| **FlashSale — order reliability** | A flash-sale order platform whose load-bearing claim is a **zero-oversell invariant**: 50 buyers race for 10 units → exactly 10 orders persist, 40 get honest fast-fails. | `src/FlashSale.*`, `src/Order.Api`, `src/Order.Worker`, `src/Payment.Service` | `docs/adr/001-016`, `docs/adr/019` |
| **OpsBridge — event-driven operations** | An operations context that owns supplier ingestion, procurement snapshots, reconciliation (allocations / discrepancies), staff decisions and DLQ replay audit. It never writes FlashSale order state directly. | `src/OpsBridge.Api`, `src/OpsBridge.Application`, `src/OpsBridge.Domain`, `src/OpsBridge.Infrastructure`, `src/OpsBridge.Web` | `docs/adr/017`, `docs/adr/018`, `docs/adr/020`, `docs/adr/021` |

> **Repository note — one history, two names.** `imtarget05/OpsBridge` was created
> from `imtarget05/FlashSale-Backend` and currently **shares the full commit
> history** with it, so both bounded contexts live in one repository **by
> design** (ADR-017 bounded contexts, ADR-018 migration and legacy baseline).
> They are separate *contexts*, not separate repositories, and each product's
> evidence is filed separately:
>
> - **FlashSale** → `docs/RECRUITER-EVIDENCE.md`, `docs/benchmarks/`, `docs/findings/`, `docs/adr/001-016` + `docs/adr/019`
> - **OpsBridge** → `docs/evidence/ci-verification.md`, `docs/known-issues.md`, `docs/requirements/`, `docs/adr/017-018`, `docs/adr/020-021`
>
> If the history is ever split, the **FlashSale-only** lineage stays at
> **<https://github.com/imtarget05/FlashSale-Backend>** and the **OpsBridge**
> lineage continues here. Nothing in this README depends on which name the
> repository carries.

## 📦 Product 1 — FlashSale: order reliability under concurrency

A portfolio-grade, event-driven backend built to handle high-concurrency "flash sale" scenarios. The core engineering challenge is processing thousands of simultaneous purchase attempts for limited inventory without overselling, while keeping latency low and the database healthy.

> **Role:** Backend Engineer (C# / .NET 10, PostgreSQL, Redis, RabbitMQ).
> **One-liner for HR:** I built an order system where 50 buyers race for 10 items — exactly 10 sales succeed, 40 get honest fast-fails. That invariant is re-checked on every CI run (the `concurrency-harness` job fails the build unless the harness prints `RESULT: PASS`); the latency figures quoted below are single local runs, not a CI gate.
> **For Tech Lead:** Clean Architecture (Domain <- Application <- Infrastructure <- Api/Worker) with dependency-direction guards in `tests/UnitTests/ArchitectureTests.cs`. Evidence in `docs/benchmarks/` + `docs/adr/001-005`.

## 📦 Product 2 — OpsBridge: event-driven operations

The operational half of the platform, kept in its own bounded context so that
operational work can never mutate authoritative order state (ADR-017).

| Concern | Decision | Where to verify |
|---|---|---|
| Ownership | OpsBridge owns FactoryFlow ingestion, procurement snapshots, reconciliation allocations/discrepancies, staff decisions and DLQ replay audit — **not** FlashSale orders, payments or inventory. A cross-context change must go through a versioned contract, never a shared database write. | `docs/adr/017-opsbridge-bounded-context-foundation.md` |
| Data access | OpsBridge persists **only** through its own `OpsBridgeDbContext`; EF Core adapters stay in Infrastructure, API endpoints depend on Application use cases and repository ports. | `docs/adr/017`, `src/OpsBridge.Infrastructure/` |
| Staff authorization | Roles `Operator`, `Admin`, `Auditor`, `FinanceReviewer` come from a trusted JWT. Unauthenticated staff routes → 401, authenticated-but-out-of-policy → 403, and connector secrets never appear in responses. | `docs/adr/017`, `src/OpsBridge.Api/Endpoints/` |
| API contract | One global `JsonStringEnumConverter` owns the JSON contract, so an enum cannot ship as an ordinal by omission; the integration-test client binds with the same options. | `docs/adr/021-enums-cross-the-api-boundary-as-names.md` |
| Web tier | `src/OpsBridge.Web` (browser journey) is a CI gate, and its deployment shape plus the client-side token risk are recorded as one explicit decision with the risk stated plainly. | `docs/adr/020-opsbridge-web-deployment-and-client-token-risk.md`, `infrastructure/kubernetes/base/opsbridge-web/` |
| Schema changes | OpsBridge has its own PostgreSQL database and its own EF Core migration. The schema is changed **only** by the dedicated `OpsBridge.Api --migrate` step, under a separate DDL-capable identity — the runtime identity holds DML rights only. | `docs/adr/018-opsbridge-migration-and-legacy-baseline-strategy.md` |

**Recorded OpsBridge test evidence** (`docs/evidence/ci-verification.md`, commit
`1ecd302`): unit **41**, contract **7**, integration **22** (Testcontainers,
PostgreSQL) — **70 / 70 passing** in that recorded run. Re-run the suites for
current numbers.

**Stated plainly, so nothing here is over-read** (the same list is maintained in
`docs/known-issues.md` and in issue #1 of this repository):

- `POST /api/v1/auth/login` is a **local demo persona selector**, mapped only in
  Development/Test — it is **not** credential authentication and there is no
  external identity provider (ADR-017).
- The payment service in the local runs is a **simulation** (`sim_tx_…`); no real
  PSP contract, credential or behaviour is exercised anywhere in this repository.
- The Kubernetes `prod` overlay is **configuration, not a deployed system** — it
  has not been applied to a real AKS cluster (ADR-020, `docs/evidence/kubernetes/phase7c-readiness.md`).
- The worker has a **container-level healthcheck** but no Pod-level
  liveness/readiness probe; a Pod probe would need a real health endpoint, and
  that decision was explicitly not taken (`docs/known-issues.md`).

## 🧠 Skills Demonstrated (scan in 30s)

| Area | What I did | Where to verify |
|---|---|---|
| Concurrency control | Redis Lua CAS gatekeeper + PG atomic conditional `UPDATE` | `docs/adr/002-003-*`, `docs/benchmarks/phase2-005-*` |
| Async / resilience | RabbitMQ at-least-once, ack-after-persist, DLQ, idempotency keys | `docs/adr/004-005-*`, `src/Order.Worker/` |
| Data integrity | Zero-oversell invariant: 50 req / stock 10 → 10 sales, stock 0 | `docs/benchmarks/phase2-oversell-experiment.md` |
| Performance | Local single run: p95 29ms tiered vs 574ms naive sync; pool cap 80/100 to keep operator headroom. Not a CI gate — re-measure before quoting | `docs/benchmarks/phase4-highload-experiment.md`, `phase5-tiered-experiment.md` |
| Authorization | `StaffOrAdmin` closed every ops endpoint; a source-level guard fails the build if a route is mapped without a policy | `tests/UnitTests/EndpointAuthorizationGuardTests.cs`, `tests/IntegrationTests/EndpointAuthorizationTests.cs` |
| Operability | pg_dump -Fc backup + SHA-256 + restore smoke test | `docs/adr/008-009-*` |
| Architecture discipline | Port + adapter for every infra addition; thin `Program.cs` | `src/FlashSale.Application/`, `tests/UnitTests/ArchitectureTests.cs` |
| Bounded contexts | OpsBridge owns ingestion / reconciliation / staff review **without** touching FlashSale order state; route roles enforced in code, not by convention | `docs/adr/017-opsbridge-bounded-context-foundation.md`, `src/OpsBridge.Api/Endpoints/` |
| Contract discipline | One global JSON enum contract (names, not ordinals), bound by the test client as well | `docs/adr/021-enums-cross-the-api-boundary-as-names.md`, `src/OpsBridge.Api/OpsBridgeJsonContract.cs` |
| Browser as a gate | The OpsBridge browser journey (click → row in PostgreSQL) is wired into `ci-gate`, so a red journey blocks the PR | `.github/workflows/ci.yml` → `opsbridge-web-e2e` |

## 🔍 Problem → Decision → Tradeoff (how I think)

1. **Naive sync oversells under race** → reproduced first (`docs/benchmarks/phase2-*`), then fixed with atomic conditional UPDATE. Tradeoff: hot-row lock queues (p95 574ms at 200 conc. in one local run) — accepted as correctness baseline.
2. **90% wasted DB round-trips just to say "no"** → Redis Lua CAS pre-filter (single RTT, atomic). Tradeoff: cache/DB divergence risk — mitigated by PG as authoritative truth + Redis mirror on cancel path.
3. **Traffic spikes kill DB** → RabbitMQ buffer + worker persist + ack-after-persist. Tradeoff: at-least-once duplicates — mitigated by idempotency keys + guarded UPDATEs.
4. **Pool saturation locks out operators** → cap API pool 80 < server 100. Small fix, big SRE lesson: always leave headroom.

## 📖 The Business Problem & Engineering Solution

During a flash sale, inventory is strictly limited (e.g., 10 items). A naive synchronous approach falls apart under concurrent load, leading to race conditions (overselling) and database timeouts.

**The Solution:**
- **Redis Lua CAS (Compare-And-Swap):** Fast, atomic inventory reservations at the cache layer to act as an immediate gatekeeper, returning fast-fails for excess traffic.
- **RabbitMQ (At-least-once delivery):** Buffers valid requests into an async queue, protecting the primary database from load spikes.
- **PostgreSQL (Authoritative Truth):** Handles the final atomic conditional `UPDATE`, ensuring absolute data consistency.

## 🚀 Key Achievements & Evidence

**The claim CI actually enforces: the zero-oversell invariant.** Every push runs
`.github/workflows/ci.yml` → job `concurrency-harness`, which fires 50 concurrent
purchase attempts at a stock of 10 through
`load-tests/concurrency/oversell_demo.py` and then **fails the job unless the
output contains `RESULT: PASS`**. `RESULT: PASS` means the database audit agrees
exactly: 10 sales persisted, final stock 0, and `accepted + rejected == 50` with no
lost responses. Reproduce it locally with the same command the job runs.

Everything below that is a latency number is a **local single-run observation** from
the `docs/benchmarks/` experiments, not a CI gate and not a promise. Re-measure
before quoting one.

- **Zero Overselling Invariant (CI-enforced):** 50 concurrent purchase attempts
  against a stock of 10 → exactly 10 persisted sales, final stock 0. Checked on
  every CI run; see above.
- **Latency (local single run, not reproducible in CI):** p95 **29 ms** for the
  tiered path versus **574 ms** for the naive synchronous iteration. Host- and
  load-dependent; the numbers of record live in `docs/benchmarks/`.
- **Idempotency & Resiliency:** Verified Redis failure fallbacks, RabbitMQ `ack-after-persist` behaviors, and safe retry mechanisms for duplicate/failed requests.
- **Disaster Recovery:** Automated PostgreSQL backup and clean-restore procedures using `pg_dump -Fc` with SHA-256 verification and application smoke testing against the recovered DB.

### Test suite (verified locally, `-c Release`)

| Suite | Count | Command |
|---|---|---|
| Unit (use cases, architecture + authorization guards) | **167** | `dotnet test tests/UnitTests/FlashSale.UnitTests.csproj -c Release` |
| Integration (Testcontainers: real Postgres + Redis + RabbitMQ) | **60** | `dotnet test tests/IntegrationTests/FlashSale.IntegrationTests.csproj -c Release` |

### 🔐 Authorization model (ADR-013 §5)

Roles are a closed set — `CUSTOMER`, `STAFF`, `ADMIN` — and they are **never chosen
by the caller**:

- `POST /api/auth/register` always creates a `CUSTOMER`. A `{"role":"ADMIN"}` in the
  body is ignored, and `RegisterRequest` has no role member for it to bind to.
- `/internal/**`, `/api/outbox/**` and `POST /api/inbox/consume` require
  `StaffOrAdmin`. `POST /api/inbox/consume` is deliberately staff-only rather than
  merely authenticated: it writes the deduplication ledger the real consumers read,
  so an ordinary caller could make a genuine delivery be silently skipped.
- `POST /api/orders/{key}/pay` and `GET /api/orders/{key}` require an authenticated
  caller **and** ownership: the order's `UserId` must equal the token's `sub`, or
  the caller must be Staff/Admin. An anonymous order is owned by nobody, so only
  Staff/Admin can poll or settle it.
- `POST /api/saga/checkout` requires any authenticated caller — it is the customer
  checkout path and it already attributes the saga to the caller's `sub`.
- `POST /api/orders` is **intentionally anonymous** (ADR-013 §6) — the zero-oversell
  evidence above depends on unauthenticated buyers. `/api/products/{id}` and the
  `/health*` probes are intentionally public too.

`tests/UnitTests/EndpointAuthorizationGuardTests.cs` enforces this from the source: a
route that is mapped without `RequireAuthorization()` and is not on the
documented public allowlist fails the build, so the hole cannot silently reopen.

#### Bootstrap: how the first ADMIN and STAFF accounts come into existence

There is no self-service promotion, and no credential in the repository. Two
opt-in environment variables (never committed — supply them from a secret store):

| Variable | Effect when set | When unset |
|---|---|---|
| `Bootstrap:AdminToken` (env `Bootstrap__AdminToken`) | Enables `POST /api/auth/bootstrap`. Present the secret in the `X-Bootstrap-Token` header to create the initial `ADMIN` (403 on a wrong secret). | The endpoint does not exist (**404**) and no ADMIN can be created. A warning is logged at startup either way. |
| `Bootstrap:StaffPassword` (env `Bootstrap__StaffPassword`) | Seeds the `STAFF` account (`staff@flashsale.local`) on first boot **only**. An existing account is left untouched — no re-hash, no role reset. | No STAFF account is created; the `/internal/**` runbook endpoints are unreachable without a bootstrap admin. |

Run the bootstrap once, then unset `Bootstrap:AdminToken`:

```bash
curl -X POST localhost:5099/api/auth/bootstrap \
  -H 'X-Bootstrap-Token: <Bootstrap:AdminToken>' -H 'Content-Type: application/json' \
  -d '{"email":"ops-admin@example.com","password":"<at least 10 characters>"}'
```

The bootstrap **creates and never promotes**: if the email is already registered it
returns 409 and the existing account keeps its role, so it cannot be used to seize
an address somebody else signed up with.

## 🏗️ Architecture

```mermaid
flowchart LR
    Client([Client]) --> API[Order API]
    API -->|1. Lua CAS| Redis[(Redis)]
    API -->|2. Publish| RMQ[RabbitMQ]
    RMQ -->|3. Consume| Worker[Order Worker]
    Worker -->|4. Atomic Update| DB[(PostgreSQL)]
```

## 🤖 Automation Platform (Business Automation Spec)

Event-driven automation layered on the same order path — no core transactional
logic was moved, and the pre-automation HTTP contract is unchanged.

```mermaid
flowchart TD
    O[Order accepted] -->|correlationId| E1{{order.created}}
    O --> E2{{inventory.reserved}}
    E1 --> W[Automation worker<br/>audit-first]
    E2 --> W
    W --> R[(automation_runs)]
    P[Payment window] -->|due!| S[Payment timeout scan]
    S -->|inside grace| N{{reminder + audit}}
    S -->|past grace| C[Cancel order<br/>+ release stock]
    C --> E3{{payment.expired}}
    C --> E4{{order.cancelled}}
    C --> E5{{inventory.released}}
    PAY[POST /api/orders/:key/pay] -->|completed| E6{{payment.completed}}
    PAY --> E6b{{order.confirmed}}
    PAY -->|failed| E7{{payment.failed}}
```

### Implemented workflows

| Workflow | Trigger | Condition | Action | Failure path | Retry | Audit | Human approval |
|---|---|---|---|---|---|---|---|
| Order events (spec §4) | order accept + worker persist | always | publish `order.created` / `inventory.reserved` | best-effort publish, order stays valid | queue retry ×4 → DLQ | `automation_runs` (worker) | no |
| Payment timeout (spec §5) | timer (`PaymentTimeoutHostedService`) or `POST /internal/automation/payment-timeout-scan` | `PendingPayment` & past due → remind; past due+grace → cancel | reminder counter; cancel + DB stock release + Redis mirror | guarded UPDATE ⇒ no-op on replay; publish best-effort | bounded scan batch (200) | 1 run per scan (`Success`/`Failed`) | no |
| Payment recording (spec §4) | `POST /api/orders/{key}/pay` | order is `PendingPayment` | `completed` ⇒ `Confirmed`; `failed` ⇒ stays pending | second call ⇒ 409 (status guard) | n/a (idempotent) | 1 run per call | simulated gateway — no real processor |
| Low-stock alert (spec §6) | timer (`LowStockScanHostedService`) or `POST /internal/automation/low-stock-scan` | `AvailableStock <= ReorderThreshold` (inclusive) | create deduplicated Open `StockAlerts` row + publish `inventory.low_stock` | rule re-checked in-process; duplicate insert rejected by partial unique index ⇒ reported as deduped | periodic rescan | 1 run per scan (`InventoryAutomation`) | no — advisory only, stock numbers never come from AI |
| Daily report (spec §7) | timer (`DailyReportHostedService`, `RunAtHourUtc`) or `POST /internal/automation/daily-report` | every report day (UTC window) | aggregate orders/revenue/failed payments/cancellations/top products/low stock from PostgreSQL → persist one `DailyReports` row (rerun upserts) | run recorded `Failed`, row untouched | rerun recomputes the day | 1 run per generation (`DailyReport`) | no — all numbers are DB aggregates; AI summary NOT implemented (PLANNED) |
| Automation dashboard (spec §14) | `GET /internal/automation/summary` + `GET /internal/automation/alerts` | on demand | runs today/success/failed/retrying/manual-review/avg duration/top failing workflow from `AutomationRuns`; open `StockAlerts` list | empty day → zeroed summary | n/a (read) | reads `AutomationRuns` | no |

### Configuration (never hard-coded — spec §5)

| Key | Default | Meaning |
|---|---|---|
| `Automation:Payment:TimeoutMinutes` | 15 | payment window per order |
| `Automation:Payment:GracePeriodMinutes` | 15 | extra time before cancellation |
| `Automation:Payment:MaxPaymentReminders` | 3 | reminder cap per order |
| `Automation:Payment:ScanIntervalSeconds` | 60 | timer scan cadence (worker) |
| `Automation:Inventory:DefaultReorderThreshold` | 5 | fallback reorder point (product value wins when > 0) |
| `Automation:Inventory:ScanIntervalSeconds` | 60 | low-stock scan cadence (worker) |
| `Automation:Reporting:RunAtHourUtc` | 0 | UTC hour the daily report fires |
| `Automation:Reporting:ScanIntervalSeconds` | 300 | how often the scheduler checks the hour |
| `Automation:Reporting:TopProductCount` | 5 | rows in the top-products ranking |

### Demo — scenario A (timeout → remind → cancel → release → audit)

```bash
# 0) get a caller token. The pay endpoint is owner-scoped, and the ops scans are
#    Staff/Admin, so the demo uses two identities.
CUSTOMER=$(curl -s -X POST localhost:5099/api/auth/register -H 'Content-Type: application/json' \
  -d '{"email":"demo-customer@example.com","password":"<at least 10 characters>"}' \
  | grep -o '"accessToken":"[^"]*' | cut -d'"' -f4)
STAFF=$(curl -s -X POST localhost:5099/api/auth/login -H 'Content-Type: application/json' \
  -d '{"email":"staff@flashsale.local","password":"<Bootstrap:StaffPassword>"}' \
  | grep -o '"accessToken":"[^"]*' | cut -d'"' -f4)

# 1) place an order, then walk it through the payment lifecycle
curl -X POST localhost:5099/api/orders -H 'Idempotency-Key: demo-1' \
     -H 'Content-Type: application/json' -d '{"productId":1,"quantity":1}'
curl -X POST localhost:5099/api/orders/demo-1/pay \
     -H "Authorization: Bearer $CUSTOMER" \
     -H 'Content-Type: application/json' -d '{"outcome":"completed"}'   # → confirmed
curl -X POST localhost:5099/api/orders/demo-1/pay \
     -H "Authorization: Bearer $CUSTOMER" \
     -H 'Content-Type: application/json' -d '{"outcome":"completed"}'   # → 409 (guarded)

# 2) run the abandoned-payment scan (same use case the timer runs) — Staff/Admin
curl -X POST localhost:5099/internal/automation/payment-timeout-scan \
     -H "Authorization: Bearer $STAFF"
#    grace elapsed ⇒ {"scanned":N,"reminded":0,"cancelled":N}, stock restored

# 3) audit trail
docker exec <postgres> psql -U postgres -d FlashSaleDb -c \
  'SELECT "WorkflowName","TriggerType","Status","ResultSummary" FROM "AutomationRuns" ORDER BY "Id" DESC LIMIT 5;'
```

*Verified locally: 167 unit + 60 integration tests green, and the live smoke
scripts still pass. The legacy `GET /api/orders/{key}` wording
(`processing|completed`) was deliberately left untouched — only its authorization
changed (authenticated + owner-scoped).*

## 📂 Project Structure (Clean Architecture)
```text
src/
├── FlashSale.Domain/         # Entities, Value Objects, Exceptions (No external dependencies)
├── FlashSale.Application/    # Use Cases & Ports (Interfaces)
├── FlashSale.Infrastructure/ # Adapters (EF Core, Redis, RabbitMQ)
├── Order.Api/                # Presentation Layer (API endpoints)
├── Order.Worker/             # Background processing daemon
├── OpsBridge.Domain/         # OpsBridge entities (No external dependencies)
├── OpsBridge.Application/    # OpsBridge use cases & ports (ingestion, reconciliation, inbox/outbox)
├── OpsBridge.Infrastructure/ # OpsBridge adapters (EF Core, connectors, messaging)
├── OpsBridge.Api/            # OpsBridge API (staff routes, JSON contract)
├── OpsBridge.Web/            # OpsBridge web tier (browser journey)
├── Payment.Service/          # Simulated payment service (no real PSP — see Product 2 caveats)
└── HealthProbe/              # Container health probes
tests/
├── UnitTests/                # Includes Architecture Tests (enforcing layer boundaries)
├── IntegrationTests/         # FlashSale — Testcontainers: Postgres, Redis, RabbitMQ
├── OpsBridge.UnitTests/      # OpsBridge unit tests
├── OpsBridge.ContractTests/  # OpsBridge API contract tests
└── OpsBridge.IntegrationTests/ # OpsBridge — Testcontainers: Postgres
```

Two solutions, two bounded contexts, one repository:
`AzureFlashSale.slnx` (FlashSale + shared) and `OpsBridge.slnx` (OpsBridge).
