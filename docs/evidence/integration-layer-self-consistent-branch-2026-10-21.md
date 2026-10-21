# Integration-layer evidence — the self-consistent branch (2026-10-21)

- **Branch:** `recovery/g2-20261020`
- **BASE_SHA:** `47f5747` (test(payment): re-enable PaymentService tests now that implementation is present)
- **HEAD_SHA at the time of writing:** see `git log --oneline` below
- **Stack:** the branch's OWN `docker-compose.yml` (project `flashsale-backend`),
  no external override file. One documented variable was changed because the
  host port was already taken by another repository's stack:
  `POSTGRES_HOST_PORT=55433` (55432 was held by `opsbridge-e2e-pg`). The
  container port and every connection string are unchanged — the API and the
  worker still talk to `postgres:5432` by service name.

## What was wrong before

The branch as committed was not self-consistent:

| # | defect | symptom |
|---|---|---|
| 1 | the branch's compose had no `payment-service` and set no `Events__Provider` / `Messaging__Provider` / `Payment__ServiceUrl` | `ResolveProvider()` returned null → `InMemoryDomainEventPublisher` → **no `automation.events` exchange, no consumer**; `HttpPaymentClient` fell back to `http://localhost:5002` — the API's own loopback → **`sim_tx_…` and a fake `{"success":true,"status":3}`**; outbox rows were marked dispatched with nobody to consume them |
| 2 | `WorkerTelemetry` declared six `flashsale.worker.outbox.*` instruments but **no `AddMeter` existed anywhere in `src/`** | nothing was exported; `/internal/metrics` showed `flashsale.http.*` only |
| 3 | `POST /api/outbox/enqueue` with a duplicate `MessageId` | unhandled `23505` on `IX_OutboxMessages_MessageId` → **HTTP 500** instead of 409 |
| 4 | two automation scans published through `IDomainEventPublisher` outside any transaction, swallowing the exception | dual-write; a lost event left no trace |

## What was changed

1. **Payment stream merged** (`git merge codex/portfolio-opsbridge`: `19f4783`,
   `031f0aa`, `8ff3212`, `89d382d`, `d96fd38`) — compose, `.env.example`,
   override, k8s base/payment, `Payment.Service` persistence + tests.
2. **`fix(outbox): export worker outbox metrics`** — the worker registers
   `FlashSale.Worker`, the API registers it too and aggregates it in the
   scrape; a counter reports the sum of its observations, a gauge its level.
3. **`fix(outbox): return 409 on duplicate outbox message id`** — the unique
   violation is translated to a domain condition at the repository boundary.
4. **`feat(outbox): route automation scans through the outbox`** — both scans
   write their events in the transaction that changes the state.

### Conflicts resolved

Both were add/add conflicts; nothing was forced.

* `docs/findings/outbox-gap.md` — the branch copy already contained the
  historical analysis (§1–§5) **and** the implemented status (§6 "What
  changed"). The payment-stream copy was the same analysis with the older
  "OPEN — analysis only" header. Resolution: keep the UNION (the analysis is
  verbatim, the status is the current one) and record the merge in the header.
* `tests/IntegrationTests/PaymentServiceTests.cs` — the payment-stream copy is
  a strict superset of the branch copy (it adds the `89d382d`
  microsecond-timestamp assertions), so the payment-stream version was taken.
* `src/Payment.Service/Models.cs` was listed as a known conflict but resolved
  automatically: both sides were byte-identical.

## Build / test / format

```
dotnet build AzureFlashSale.slnx -c Release   → 0 error
dotnet build OpsBridge.slnx     -c Release     → 0 error
dotnet format AzureFlashSale.slnx --verify-no-changes → exit 0
dotnet format OpsBridge.slnx     --verify-no-changes → exit 0
dotnet test  AzureFlashSale.slnx -c Release    → 5 projects, 0 fail (run twice)
dotnet ef migrations has-pending-model-changes → clean for all 3 contexts
```

## Runtime evidence (the branch's own compose)

### Containers

```
flashsale-order-api         Up (healthy)   0.0.0.0:5001->8080/tcp
flashsale-order-worker      Up
flashsale-payment-service   Up (healthy)   0.0.0.0:5002->8080/tcp
flashsale-postgres          Up (healthy)   0.0.0.0:55433->5432/tcp
flashsale-rabbitmq          Up (healthy)   0.0.0.0:5672->5672/tcp, 15672->15672
flashsale-redis             Up (healthy)   0.0.0.0:56379->6379/tcp
```

> The worker service has **no healthcheck** in the branch's compose, so it
> reports `Up` rather than `Up (healthy)`. Its liveness is proven by its logs
> (the outbox dispatcher claims rows every 2 s, the `automation.events`
> consumer is connected).

### A real transaction id, not a simulated one

```
POST /api/saga/checkout   → 200 {"success":true,"sagaId":"92dde0ee-…","orderId":1,"status":3}
API log: Payment succeeded (tx=tx_503499a8f7e442f4b0d28da62b390597). Confirming order 1.
count of "sim_tx" in the API log: 0
payment-service log: Request starting POST http://payment-service:8080/api/payments
                      Request finished POST http://payment-service:8080/api/payments - 200
```

### The event backbone is real

```
$ curl -su guest:guest http://localhost:15672/api/exchanges
automation.events   topic

$ curl -su guest:guest http://localhost:15672/api/queues
automation.events   consumers=1  messages=0
orders              consumers=2  messages=0
```

### Order flow

```
POST /api/orders (Idempotency-Key: order-1792550039-21115) → 202 {"status":"processing"}
worker consumes 'orders'                                   → Orders.Status = PendingPayment
POST /api/orders/{key}/pay {"outcome":"completed"}         → 200 {"status":"confirmed"}
                                                              Orders.Status = Confirmed
```

### Outbox → broker → inbox → audit

```
 Id |        EventType        | AggregateType | AggregateId | processed | RetryCount
----+-------------------------+---------------+-------------+-----------+------------
  1 | inventory.low_stock     | inventory     | 1           | t         |          0
  2 | inventory.low_stock     | inventory     | 4           | t         |          0
  3 | checkout.saga.started   | checkout-saga | 92dde0ee-…  | t         |          0
  4 | order.created           | order         | 1           | t         |          0
  5 | checkout.saga.completed | checkout-saga | 92dde0ee-…  | t         |          0
  6 | order.confirmed         | order         | 1           | t         |          0
  7 | inventory.reserved      | order         | order-…21115| t         |          0
  8 | order.created           | order         | 2           | t         |          0
  9 | payment.completed       | order         | 2           | t         |          0
 10 | order.confirmed         | order         | 2           | t         |          0
 21 | payment.expired         | order         | 5           | t         |          0
 22 | order.cancelled         | order         | 5           | t         |          0
 23 | inventory.released      | order         | 5           | t         |          0

final: 23 outbox rows, 23 processed, 0 dead-lettered
       24 inbox rows, 24 AutomationRuns with TriggerType='domain.event'
```

Rows 1–2 and 21–23 are the **automation scans** (the low-stock scan and the
payment-timeout scan) — they are in the outbox because of the fix, and the
consumer recorded them:

```
PaymentTimeout    | timer       | Success | scanned=1 reminded=0 cancelled=1
InventoryAutomation| timer      | Success | scanned=2 alerts_created=2 products=[1,4]
InventoryAutomation| domain.event| Success | Processed inventory.low_stock.
PaymentTimeout    | domain.event| Success | Processed payment.expired.
```

### Duplicate delivery applies the side effect once

The same envelope (`event_id=3a44c2c8-669c-4ae9-a89b-1a9d456af9e7`) was
published to `automation.events` **twice**:

```
{"routed":true} <- publish #1
{"routed":true} <- publish #2

InboxMessages:      10 → 11   (+1, not +2)
AutomationRuns for that event_id: 1
```

### The metrics are exported

```
$ curl -s http://localhost:5001/metrics
# TYPE flashsale_worker_outbox_dispatched counter
flashsale_worker_outbox_dispatched 12
# TYPE flashsale_worker_outbox_pending gauge
flashsale_worker_outbox_pending 0
# TYPE flashsale_worker_outbox_stuck gauge
flashsale_worker_outbox_stuck 0
flashsale_worker_messages_processed{outcome="succeeded"} 1
```

The counter was observed rising while the dispatcher published
(6 → 10 → 12 as new outbox rows were written and dispatched).

## NOT VERIFIED here

* A real payment provider (PSP) — `Payment.Service` is a local provider.
* The Kafka path (experimental; the stack runs RabbitMQ).
* The Kubernetes overlay (the evidence is the compose stack).
* Load tests / flash-sale burst behaviour of the 2 s polling dispatcher.
* The production database shape and the retention/archival job for
  `OutboxMessages`.
