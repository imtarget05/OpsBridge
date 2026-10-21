# ADR 019: Transactional Outbox for the business write path

- **Status:** Accepted
- **Date:** 2026-10-20
- **Supersedes/extends:** ADR-015 (Transactional Outbox & RabbitMQ Reliability — the
  ingestion path), ADR-012 (keep .NET)
- **Closes:** `docs/findings/outbox-gap.md`

## Context

ADR-015 introduced the transactional outbox — but only for the **ingestion**
path of the OpsBridge bounded context (`ReceiveWebhookUseCase`: signed webhook →
`InboxMessages` + `IngestedEvents` + `OutboxMessages` in one transaction, then
`OutboxPublisherHostedService`).

The **business** path of the flash-sale app did something else entirely.
`SubmitOrderUseCase`, `OrderProcessor`, `RecordPaymentUseCase` and
`CheckoutSagaCoordinator` published their domain events **directly to the
broker** in a best-effort `try/catch`, *after* the PostgreSQL write had already
been committed. `SubmitOrderUseCase` even documented the gap in its own
source code:

> *"Best-effort publish — a bus outage must NOT fail acceptance (the order row
> is the truth; no-outbox is a documented gap)."*

The evidence collected on 2026-10-20 (`docs/findings/outbox-gap.md`) showed
the transport working and the `OutboxMessages` table **empty** for business
traffic: the events reached RabbitMQ, and a crash between "transaction
committed" and "message published" — or a broker outage in that window —
silently lost them. Nothing replayed them.

This is the classic dual-write problem: two systems (database and broker) are
updated independently, and no ordering of the two writes can be made atomic
without a distributed transaction.

### The options

* **Option A — transactional outbox (chosen).** Write the event into an
  `OutboxMessages` row *inside the same transaction* as the state change; a
  dispatcher publishes pending rows later. The infrastructure already exists
  (`OutboxMessages` table, `OutboxRepository` with claim/lease/backoff/DLQ,
  `OutboxDispatcherHostedService`), so this is wiring, not new machinery.
* **Option B — keep the direct publish, add consumer-side idempotency and a
  reconciliation job.** Makes the loss *detectable* and repairable after the
  fact, but does not close the gap: the crash window still loses the event.
* **Option C — distributed transaction (2PC) across PostgreSQL and the
  broker.** Rejected: the broker offers no 2PC participant, the latency cost
  is unacceptable, and the failure modes are worse than the gap it fixes.

## Decision

**Option A.** The business write path records its events in the transactional
outbox inside the transaction that changes the state. The broker is never
touched by a business transaction.

### 1. The write boundary

A new port `IWriteTransaction` (implementation `EfWriteTransaction`) owns the
physical transaction. It is **nesting-aware**: the outermost scope owns the
transaction, an inner scope joins it and never commits or rolls it back. The
order and payment repositories now *join* an ambient transaction instead of
opening their own, so the following commit together — or not at all:

| write path | state change | events written in the same transaction |
|---|---|---|
| `SubmitOrderUseCase` | Redis reservation (out of DB scope) + order acceptance | `inventory.reserved` (+ `inventory.released` when the reservation is given back) |
| `OrderProcessor` | `Orders` row + `Products` stock decrement | `order.created` |
| `RecordPaymentUseCase` | `Orders.Status` → `Confirmed`/`failed` | `payment.completed`, `payment.failed`, `order.confirmed` |
| `CheckoutSagaCoordinator` | saga state + `Orders` row + stock | `checkout.saga.started`, `order.created`, `checkout.saga.completed` + `order.confirmed`, `checkout.saga.compensated` + `order.cancelled` |

**The saga is atomic per step, not as a whole.** A saga spans an external
payment call and cannot be one transaction. Each *step* is: write the state
change, write that step's events, commit. The payment call sits between two
atomic steps. The consequence is the property that matters: **the event
stream can never describe a step that was rolled back, and a committed step
can never lose its event.** A compensated saga publishes
`checkout.saga.compensated` — never a resurrected `checkout.saga.completed`.

`SubmitOrderUseCase` writes `inventory.reserved` **before** the order is
handed to the queue: if the outbox write fails, the reservation is released
and the order is *not* accepted (`SystemBusy`). An accepted order therefore
cannot be missing its event.

### 2. The event envelope

Every event travels in one canonical envelope (`FlashSale.Domain.Events.EventEnvelope`):

```json
{
  "event_id":        "b7c1…",   // UUID, STABLE: generated once when the outbox row
                                   // is written and reused for every retry
  "event_type":      "order.created",
  "aggregate_type":  "order",
  "aggregate_id":    "42",
  "occurred_at":     "2026-10-20T10:00:00+00:00",
  "correlation_id":  "9f2c…",   // request → event → audit trail
  "schema_version":  1,
  "payload":         "{ …typed event… }"
}
```

The envelope is **stored** with the outbox row, not re-derived at publish
time, so a retry republishes byte-identical content — including the stable
`event_id`. The outbox row keeps the envelope metadata as columns
(`AggregateType`, `AggregateId`, `CorrelationId`, `SchemaVersion`) so the
operator can see what a pending row is about without parsing JSON.

`event_id` is the consumer-side **dedup key**. It is written into
`OutboxMessages.MessageId` (unique index) and into the envelope, so the
consumer sees the same id on every delivery of the same business fact.

The typed event stays inside `payload`; the consumer unwraps the envelope and
deserialises the typed event from it. Legacy flat events are still accepted by
the consumer.

### 3. Dispatcher: retry, recovery, dead-letter

Reused, not reinvented: `OutboxDispatcherHostedService` → `OutboxDispatcherUseCase`
→ `IOutboxRepository`.

* **Claim/lease.** A batch is claimed with `FOR UPDATE SKIP LOCKED` and a
  lease. A dispatcher that dies mid-publish leaves a lease that **expires** —
  the row is claimed again by the next dispatcher. Nothing is stranded.
* **Processed only after the publish.** `ProcessedAt` is set after the
  transport acknowledged the message. A crash in that window republishes the
  event (at-least-once) — the consumer deduplicates it.
* **Retry with backoff.** A failed attempt increments `RetryCount` and
  schedules the next attempt at `2^n` seconds (capped at 60 s).
* **Dead-letter.** After `MaxRetryCount` (5) attempts the row is marked
  `DeadLetteredAt` — a terminal, visible state. It is never reported as
  "pending"; `GET /api/outbox/stuck` lists it and `POST /api/outbox/requeue`
  revives it.
* **Poison messages.** A row whose payload is not a canonical envelope is
  never published (a consumer could not deduplicate it). It burns its
  retries and ends up dead-lettered — loudly, see the metrics below.

### 4. Idempotent consumer

The consumer (`AutomationEventProcessor`, shared by the RabbitMQ and Kafka
paths) deduplicates on the envelope's `event_id` through the existing
`InboxMessages` table:

1. **Claim** the message id *before* applying side effects
   (`TryClaimAsync`): an `INSERT … ON CONFLICT DO NOTHING` guarded by the
   unique constraint on `(MessageId, ConsumerName)`. Exactly one delivery can
   win, so two concurrent deliveries of the same event cannot both apply side
   effects.
2. Apply the side effects (the `AutomationRuns` audit row and the workflow).
3. **Mark processed** once the audit row is persisted.

A claim left behind by a crashed consumer is taken over after the 5 minute
lease expires, so a crash mid-processing does not lose the event.

*Trade-off, stated honestly:* claim-first is **exactly-once for side
effects** and **at-most-once for the audit row** in the crash window between
the claim and the audit write. The previous order (process, then mark) was the
mirror image: at-least-once auditing, at-least-once side effects. ADR-019
chooses exactly-once side effects; the price is one audit row in a rare crash
window, and the recovery path is the stale-claim takeover.

### 5. Observability

Following the existing `flashsale.*` meter pattern (`WorkerTelemetry`):

| metric | meaning |
|---|---|
| `flashsale.worker.outbox.pending` | **outbox lag** — rows written, not yet published |
| `flashsale.worker.outbox.dispatched` | messages published and marked processed |
| `flashsale.worker.outbox.publish_failures` | publish attempts that failed |
| `flashsale.worker.outbox.retried` | failures scheduled for a retry |
| `flashsale.worker.outbox.dead_lettered` | rows that exhausted the retry ceiling |
| `flashsale.worker.outbox.stuck` | rows dead-lettered or waiting on a backoff |

A dead-lettered row is also logged at **error** level by the dispatcher and by
the hosted service — a poison message is never silent.

**Exported, not just declared.** The instruments above live on the
`FlashSale.Worker` meter. A `MeterProvider` only observes the meters it was
told about, so the meter is registered explicitly in the worker's
`WithMetrics()` block and in the API's, and the API's `MeterListener`
aggregates it — otherwise the six series would exist as C# fields and appear
in no scrape. `GET /metrics` (Prometheus) and `GET /internal/metrics` (JSON,
staff) both carry them; `pending` and `stuck` are exported as **gauges**
(they are levels), the rest as **counters** (they are totals).

### 6. Crash-window matrix (the acceptance criterion)

Verified by `tests/IntegrationTests/OutboxCrashWindowTests.cs` against a real
PostgreSQL, a real Redis and a real RabbitMQ (Testcontainers; the "broker
outage" is a stopped container, the "poison message" is a row PostgreSQL
really rejects).

| # | window | expectation | test |
|---|---|---|---|
| a | before commit | a failing outbox write rolls back the whole transaction (no order, no stock change, no event) | `A_OutboxWriteFails_TheWholeTransactionRollsBack` |
| b | after commit, before publish | the dispatcher dies; the event survives and is published after the restart | `B_DispatcherDiesAfterCommit_TheEventIsPublishedAfterTheRestart` |
| c | after publish, before `ProcessedAt` | the event is published again; the consumer deduplicates it (one audit row) | `C_PublishedButNotMarked_TheRepublishIsDeduplicatedByTheConsumer` |
| d | broker down | retry + backoff, nothing lost, published once the broker is back | `D_BrokerDown_TheEventIsRetriedAndPublishedAfterTheRecovery` |
| e | duplicate delivery | the consumer applies the side effect exactly once | `E_DuplicateDelivery_TheConsumerAppliesTheSideEffectOnce` |
| f | poison message | dead-lettered after the retry ceiling, visible in `/api/outbox/stuck`, moves the metrics | `F_PoisonMessage_IsDeadLetteredAndObservable` |
| g | API idempotency | two requests with one `Idempotency-Key` → one order, one event | `G_TwoRequestsWithOneIdempotencyKey_ProduceOneOrderAndOneEvent` |

### 7. Schema

Two EF Core migrations on `AppDbContext` (FlashSale database):

* `AddOutboxEventEnvelope` — `OutboxMessages.AggregateType`,
  `AggregateId`, `CorrelationId`, `SchemaVersion` (+ an index on
  `DeadLetteredAt`).
* `AddInboxClaim` — `InboxMessages.ProcessedAt` becomes nullable,
  `InboxMessages.ClaimedAt` (+ an index on it).

Following ADR-018's discipline: the migration is **verified, not stamped**.
`dotnet ef migrations has-pending-model-changes` reports no drift between the
model and the migrations, and the whole chain is applied from zero on a
disposable database in the integration fixture. No production database is
stamped or edited by hand.

### 8. Scope of this decision, and what is still open

#### Closed since the decision (2026-10-21, branch `recovery/g2-20261020`)

* **The two remaining automation scans are now on the outbox.** They were the
  last dual-write paths: `PaymentAutomationUseCase` published
  `payment.expired` / `order.cancelled` / `inventory.released` and
  `LowStockAlertUseCase` published `inventory.low_stock` straight to the
  broker in a swallowed `try/catch`. Both now write their events inside the
  transaction that changes the state
  (`PaymentAutomationUseCase.cs`, `LowStockAlertUseCase.cs`), so the swallowed
  publish is gone. `IDomainEventPublisher` is now used by the **dispatcher
  only**. The low-stock dedup insert runs under a SAVEPOINT, because a
  unique-index violation inside a PostgreSQL transaction aborts it.
* **The outbox metrics are exported.** The six `flashsale.worker.outbox.*`
  instruments were declared in `WorkerTelemetry` but no meter was ever
  registered with a `MeterProvider`, so nothing could observe them: the
  scrape showed `flashsale.http.*` only. The worker now registers
  `FlashSale.Worker` (`AddMeter`), the API registers it too and aggregates it
  in `/internal/metrics` and `/metrics`. A counter is reported as the sum of
  its observations and a gauge as its current level.
* **`POST /api/outbox/enqueue` answers 409 on a duplicate `MessageId`.** The
  unique index (`IX_OutboxMessages_MessageId`) protects the consumer's dedup
  key; the violation is translated into a domain condition at the repository
  boundary instead of escaping as an unhandled `23505` and a 500.

#### Still open (explicitly not covered by this decision)

* **A real payment provider.** `Payment.Service` is a local HTTP provider with
  a durable idempotency ledger, not a PSP. The saga talking to it proves the
  wiring, not a real money path.
* **Kafka is experimental.** The Kafka arm of the publisher/consumer exists
  and is not exercised by the production-fidelity stack, which runs RabbitMQ
  (`Messaging__Provider=RabbitMQ`). The Kafka path is a code path, not a
  verified one.
* **The Kubernetes overlay is not verified here.** This decision was verified
  on the branch's own `docker-compose.yml`. The k8s manifests (base + the
  pinned prod overlay) are not part of that evidence.
* **Load tests are not part of this decision.** The outbox is polled every
  2 s by design; its behaviour under a flash-sale burst (thousands of rows,
  one dispatcher vs. several) is unmeasured.
* **The production database shape is unproven.** The migrations are applied
  from zero on a disposable database and verified drift-free against the
  model; the shape of a real production database (volume, indexes, vacuum,
  partitioning of `OutboxMessages`) is not decided here — and retention /
  archival of `OutboxMessages` is still missing.


### 9. Operating the outbox

| situation | what the operator sees | what to do |
|---|---|---|
| the broker is down | `outbox.pending` grows, `publish_failures` rises, `outbox.stuck` grows | nothing — the rows drain when the broker returns |
| a poison message | `outbox.dead_lettered` rises, the row is in `GET /api/outbox/stuck` | inspect `Error`, fix the cause, `POST /api/outbox/requeue` |
| a dispatcher crashed | `outbox.pending` does not shrink | nothing — the claim lease expires and another replica picks the row up |
| the outbox is growing for good | `outbox.pending` never returns to 0 | check the dispatcher's throughput and the broker's health |

## Consequences

**Pros**

* No event can be lost between the database write and the publish — the
  original P1 gap of `docs/findings/outbox-gap.md` is closed.
* Consumers can be exactly-once for side effects (dedup on `event_id`).
* One envelope for every producer, so a consumer needs one parser.
* The outbox lag, the retry count and the dead-letter count are metrics, not
  log archaeology.

**Trade-offs**

* Delivery is no longer synchronous with the write: the dispatcher polls every
  2 seconds, so events land shortly after the commit.
* `OutboxMessages` grows — retention/archival is now needed (already listed as
  a trade-off in ADR-015).
* The write path now needs a transaction scope; a write path that forgets it
  would reintroduce the dual-write. The producers are covered by tests that
  assert the transaction boundaries.
* The saga is atomic per step, not as a whole — a crash between two steps
  leaves the saga in the state of the last committed step, and the
  compensation logic still owns the recovery.

## Rollback

The change is revertible per commit:

1. **Revert the producers** (`feat(outbox): transactional outbox …`): the
   business path publishes directly again. Rows already written to
   `OutboxMessages` keep being dispatched by the dispatcher — harmless, but
   they will be published as envelopes that an old consumer cannot read
   (they are dead-lettered, not lost).
2. **Revert the consumer** (`feat(outbox): idempotent consumer dedup …`): the
   consumer goes back to at-least-once side effects. The `ClaimedAt` column
   stays (nullable, harmless).
3. **Revert the migrations** (`Down`): drops the four outbox columns and the
   inbox claim column. `InboxMessages.ProcessedAt` goes back to `NOT NULL`,
   which requires that no row is left with a NULL `ProcessedAt` — i.e. that no
   claim is outstanding.

Rolling back the application but **not** the schema is safe: the columns are
additive and nullable (except `SchemaVersion`, which has a default).
