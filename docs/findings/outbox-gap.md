# Finding: the FlashSale business flow publishes domain events OUTSIDE the write path (no outbox)

- **Status:** **RESOLVED — Option A implemented** (2026-10-20, branch `recovery/g2-20261020`)
- **Found:** 2026-10-20, during the production-fidelity pass on the `docker-compose` stack
- **Severity:** P1 (design gap, no live incident)
- **Resolution:** [ADR-019 — Transactional Outbox for the business write path](../adr/019-transactional-outbox.md)
- **This note keeps the original analysis.** The sections below describe the gap
  as it was found; the "What changed" section at the end says how it was closed.
- **Merged note (2026-10-21).** The same finding was captured independently on the
  payment-stream branch (`codex/portfolio-opsbridge`, commit `8ff3212`), where the
  note was still in its original "OPEN — analysis only" form. Merging the payment
  stream into `recovery/g2-20261020` produced an add/add conflict on this file; the
  resolution keeps BOTH: the historical analysis (§1–§5, verbatim, including the
  original "this is a decision, not an implementation task" framing and the
  "Update (2026-10-20)" note that records the decision) AND the implemented status
  (§6 "What changed"). The merged branch is the authoritative one.

---

## 1. What the gap is

The **business** flow of the flash-sale app (submit order → worker fulfils → record payment →
checkout saga) publishes its domain events **directly** to the event transport, in a
best-effort `try/catch`, *after* the PostgreSQL write has already been committed. The
`OutboxMessages` table is never written by that flow.

The **ingestion** flow of the OpsBridge bounded context (signed webhook → `ReceiveWebhookUseCase`)
*does* write the outbox inside the same transaction, so ADR-015 is honoured there.

| Producer | Event(s) | Writes `OutboxMessages`? | Publishes |
|---|---|---|---|
| `src/FlashSale.Application/Orders/SubmitOrderUseCase.cs:117` | `inventory.reserved` | ❌ | direct, best-effort (`try/catch` around `eventPublisher.PublishAsync`) |
| `src/FlashSale.Application/Orders/OrderProcessor.cs:58` | `order.created` | ❌ | direct, best-effort (worker side, after `PersistAsync` committed) |
| `src/FlashSale.Application/Automation/RecordPaymentUseCase.cs:139` (`PublishSafeAsync`) | `payment.completed` / `payment.failed` | ❌ | direct, best-effort |
| `src/FlashSale.Application/Saga/CheckoutSagaCoordinator.cs:268` (`PublishSafeAsync`) | `checkout.saga.started` / `checkout.saga.completed` / compensation events | ❌ | direct, best-effort |
| `src/OpsBridge.Application/UseCases/ReceiveWebhookUseCase.cs:157` | `goods.receipt.confirmed` | ✅ (same transaction) | via `OutboxDispatcherHostedService` |

The code says so itself — `SubmitOrderUseCase` comments the publish as
*"Best-effort publish — a bus outage must NOT fail acceptance (the order row is the truth;
**no-outbox is a documented gap**)"*.

### Evidence from the running stack (2026-10-20, `docker compose` project `flashsale-backend`)

Business flow executed for real (order accepted → worker → payment recorded → saga checkout):

```text
POST /api/orders              -> 202 {"status":"processing"}
worker consumes 'orders'      -> Orders.Id=8 Status=PendingPayment
POST /api/orders/{key}/pay    -> 200 {"status":"confirmed"}   (Orders.Status=Confirmed)
POST /api/saga/checkout       -> 200 {"status":"completed"}    (tx_319eaaccb48e4e4ea1e958c529f55355)
```

…and the outbox table was still empty for that traffic:

```text
$ psql -c 'SELECT count(*) FROM "OutboxMessages";'
 count
-------
     1      <-- the single row is a manual POST /api/outbox/enqueue (Program.cs:686) from a
                previous verification, NOT from the business flow
```

The events themselves *did* reach RabbitMQ (the backbone now works — see
`docker-compose.yml`, `Events__Provider=RabbitMQ`): the worker's `AutomationWorkerHost`
consumed `inventory.reserved`, `order.created`, `payment.completed`, `order.confirmed` from the
`automation.events` topic exchange and wrote `AutomationRuns` rows with `TriggerType='domain.event'`,
`Status='Success'`.

**So the transport works; what is missing is atomicity between the database write and the
publish.** The event can still be lost if the process dies between "transaction committed" and
"message published", or if the broker is down during that window — the publish failure is
swallowed and logged as a warning, and nothing replays it.

---

## 2. Two options

### Option A — wrap the publish in the write transaction (classic transactional outbox)

In each producing use case, insert an `OutboxMessages` row **inside the same
`AppDbContext` transaction** as the state change, and let the existing
`OutboxDispatcherHostedService` publish it (it already claims batches with
`FOR UPDATE SKIP LOCKED`, retries with backoff, dead-letters and marks rows processed on
acknowledgement — `src/FlashSale.Infrastructure/Outbox/OutboxDispatcherHostedService.cs`,
`src/FlashSale.Application/Outbox/OutboxDispatcherUseCase.cs`).

- **Pros:** zero message loss; matches ADR-015 and the k8s/`OpsBridge` ingestion path; the
  machinery already exists, so this is wiring, not new infrastructure.
- **Cons / work:** the use cases are not currently transaction-scoped in one place (each
  repository call commits on its own), so the transaction boundary has to be introduced;
  the dispatcher publishes a generic `OutboxDomainEventWrapper`, so the consumer must accept
  both typed direct events and wrapped ones; the saga has a multi-step + compensation
  semantics that must be modelled carefully (a partially-outboxed saga is worse than none).

### Option B — keep the direct publish, add consumer-side idempotency + reconciliation

Leave the producers as they are and make the *loss* detectable and bounded:

- dedupe on the consumer with the existing `InboxMessages` table (idempotent handler),
- add a reconciliation job (compare `Orders.Status`/`AutomationRuns` against the events that
  should have existed) that re-emits missing events,
- alert on the `publish failed` warning that today is only logged.

- **Pros:** no change to the write path; no extra latency; the producers stay simple.
- **Cons:** the gap is *not closed* — a crash between commit and publish still loses the
  event, and reconciliation only repairs it after the fact. Duplicates are possible unless
  the consumer is idempotent.

---

## 3. Risks that apply to both options

- **At-least-once, not exactly-once.** Any outbox (or redelivery) scheme can deliver the same
  event more than once. The consumer must be idempotent; the `Inbox` table is the intended
  mechanism but is currently only used by the OpsBridge ingestion path.
- **Duplicate side effects.** `AutomationRuns` rows and order transitions would be applied
  twice unless the consumer dedupes on `EventId`/`CorrelationId`.
- **Delivery latency.** Outbox dispatch is polled (default ~1 s), so events land after the
  DB write, not with it. Acceptable for automation triggers; it would matter if any
  user-facing flow depended on the event being immediate.
- **Table growth.** `OutboxMessages` needs retention/archival once the business flow writes
  to it (ADR-015 already lists this as a trade-off).
- **Backpressure coupling.** Today a broker outage cannot fail an order acceptance. With an
  outbox, the write still succeeds (rows queue up) — but the queue depth becomes a new
  operational signal that has to be monitored.

---

## 4. Recommendation

**Option A**, scoped in two steps:

1. **Step 1 (low risk, big win):** wire `SubmitOrderUseCase` and `RecordPaymentUseCase` (and
   `OrderProcessor` on the worker) to write the outbox row in the same transaction, reusing the
   existing `OutboxMessages` + dispatcher infrastructure. Add inbox dedup on the consumer so the
   at-least-once delivery is safe.
2. **Step 2 (defer):** the checkout saga. Its compensation path must be modelled first
   (a compensated saga must publish `checkout.saga.compensated`, not a resurrected
   `checkout.saga.completed`), so it should not be swept into step 1.

Option B is only worth it if the business flow is deliberately kept "best-effort" — and then
ADR-015 should be amended to say so explicitly, because today the ADR reads as if the outbox
covers the whole system.

**This is a decision, not an implementation task** — it changes the transaction boundary of the
core write path, and it needs the maintainer's call on scope (step 1 only vs. step 1+2) and on
whether the ADR is amended.

> **Update (2026-10-20):** the decision was taken — **Option A, both steps** — and
> implemented. See [ADR-019](../adr/019-transactional-outbox.md) and the
> "What changed" section below.

---

## 5. Side observation (not part of this gap)

With the event backbone now real, two saga events arrive at the consumer with **no handler**
and are dead-lettered:

```text
Unreadable automation event (type=checkout.saga.started) — dead-lettering.
Unreadable automation event (type=checkout.saga.completed) — dead-lettering.
```

That is a consumer-coverage gap (the `AutomationEventProcessor` does not know the saga event
types), not a transport problem. It was invisible while the backbone was in-memory. Worth a
follow-up decision; not changed here.


---

## 6. What changed (2026-10-20, Option A implemented)

**Decision:** Option A, in full — step 1 (order/payment) *and* step 2 (the checkout
saga). Recorded in [ADR-019](../adr/019-transactional-outbox.md).

### The gap is closed for the business write path

(The two automation scans listed under "Still open" below were still direct
publishers at this point; they were converted on 2026-10-21 — see the update
under "Still open" below.)

| producer (before) | now |
|---|---|
| `SubmitOrderUseCase.cs:117` — direct publish, best-effort | writes `inventory.reserved` to the outbox **in the transaction that accepts the order**; a failed write releases the reservation and the order is not accepted |
| `OrderProcessor.cs:58` — direct publish after `PersistAsync` committed | the `Orders` row, the stock decrement and the `order.created` outbox row commit **in one transaction** |
| `RecordPaymentUseCase.cs:139` (`PublishSafeAsync`) — direct publish | the status transition and `payment.completed` / `payment.failed` / `order.confirmed` commit **in one transaction** |
| `CheckoutSagaCoordinator.cs:268` (`PublishSafeAsync`) — direct publish | every saga **step** commits its state change and its events together (`checkout.saga.started` / `.completed` / `.compensated`, `order.created` / `.confirmed` / `.cancelled`) |

There is no direct-publish path left in those use cases: `IDomainEventPublisher` is
no longer a dependency of `SubmitOrderUseCase`, `OrderProcessor`, `RecordPaymentUseCase`
or `CheckoutSagaCoordinator`. The broker is only touched by the dispatcher.

### What was added

* **Canonical envelope** (`event_id`, `event_type`, `aggregate_type`, `aggregate_id`,
  `occurred_at`, `correlation_id`, `schema_version`, `payload`), stored with the outbox
  row so a retry republishes byte-identical content.
* **A stable `event_id`** — generated once when the row is written, reused for every
  retry, and the consumer's dedup key.
* **A transaction scope** (`IWriteTransaction` / `EfWriteTransaction`) that lets the
  business write and the outbox row share one transaction.
* **An idempotent consumer**: the inbox row is *claimed* (unique constraint) before
  the side effects, so a duplicate delivery cannot apply them twice.
* **Dispatcher observability**: outbox lag, publish failures, retries and dead-letters
  are metrics (`flashsale.worker.outbox.*`), and a dead-lettered row is logged at
  error level — a poison message is never silent.
* **Two EF Core migrations** (`AddOutboxEventEnvelope`, `AddInboxClaim`) — verified
  against the model, applied from zero on a disposable database, never stamped onto a
  production database by hand.

### The crash-window matrix is proven, not asserted

`tests/IntegrationTests/OutboxCrashWindowTests.cs` runs the seven windows against a
real PostgreSQL, a real Redis and a real RabbitMQ (the "broker outage" is a stopped
container, the "poison message" is a row PostgreSQL really rejects). See ADR-019 §6
for the table.

### Still open (not part of this decision)

* **Two automation scans still publish directly** and are therefore still
  dual-write: `PaymentAutomationUseCase` (`payment.expired`,
  `order.cancelled`, `inventory.released`) and `LowStockAlertUseCase`
  (`inventory.low_stock`). They are outside the scope of ADR-019, which covers
  the order/payment/saga write paths, and their events are informational
  rather than a business fact a consumer acts on. Converting them is the same
  change and is the obvious follow-up.
  **UPDATE (2026-10-21, branch `recovery/g2-20261020`): DONE.** Both scans now
  write their events inside the transaction that changes the state, so the
  swallowed publish is gone and `IDomainEventPublisher` is used by the
  dispatcher only. See ADR-019 §8.

* The **side observation** in §5 (saga events arriving without a handler) is fixed as
  a side effect: the consumer now knows `checkout.saga.started` / `.completed` /
  `.compensated` instead of dead-lettering them.
* **Retention/archival** of `OutboxMessages` is still missing — the table now receives
  business traffic, so it needs a cleanup job (already listed as a trade-off in
  ADR-015).
