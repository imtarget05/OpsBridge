# FlashSale Phase 5 Architecture (historical baseline)

This page records the Phase 5 design. The current repository also includes
payment/Saga, transactional business outbox and a separate OpsBridge bounded
context. Use [ADR-019](../adr/019-transactional-outbox.md),
[ADR-017](../adr/017-opsbridge-bounded-context-foundation.md) and the
[2026-10-21 portfolio audit](../evidence/portfolio-audit-2026-10-21.md) for
the current claim and evidence boundaries; the diagrams and measurements below
must not be read as a complete current topology or fresh benchmark.

## Overview
Tiered flash-sale order path: Redis absorbs hype traffic, the queue decouples
acceptance from fulfillment, PostgreSQL remains the source of truth.

```text
[ Client ]
    |  POST /api/orders (Idempotency-Key)
    v
[ Order API ]
    |-- T1: Redis Lua CAS (stock + idempotency ledger) --> sold out? 409 (29ms p95)
    |-- T2: enqueue OrderMessage --> [ Queue: InMemory (dev) | Azure Service Bus (prod) ]
    `-- fallback: direct DB path if Redis is down (Phase 3 logic)
    v  202 Accepted {status: processing}
[ Worker (OrderProcessorHost) ]
    |-- idempotency check (unique index Orders.IdempotencyKey)
    |-- authoritative atomic UPDATE stock (ADR-002) + INSERT order
    |-- retries w/ backoff -> DLQ; drift -> StockDriftException -> DLQ + resync runbook
    v
[ PostgreSQL ]  (source of truth)
```

## Layered structure (Clean Architecture)

```text
src/
├── FlashSale.Domain/          Entities (Product, Order), OrderMessage, StockDriftException
│                              → ZERO package references (BCL only)
├── FlashSale.Application/     Use case: OrderProcessor
│                              Ports:  IOrderRepository, IOrderReadModel,
│                                      IOrderQueueProducer/Consumer,
│                                      IStockReservationGateway
│                              → depends only on Domain
├── FlashSale.Infrastructure/  Adapters implementing the ports:
│                              Persistence/  AppDbContext, OrderRepository, OrderReadModel,
│                                            DatabaseInitializer
│                              Messaging/    InMemoryOrderQueue, ServiceBusOrderQueue,
│                                            OrderProcessorHost
│                              Redis/        RedisStockGateway
│                              → depends on Application
├── Order.Api/                 Presentation + composition root (thin Program.cs)
└── Order.Worker/              Composition root for the async worker
tests/
└── UnitTests/                 Use-case tests with a fake IOrderRepository +
                               architecture guards that fail the build on
                               dependency-rule violations
```

Dependency rule (enforced by tests in `tests/UnitTests/ArchitectureTests.cs`):

```text
Domain  ←  Application  ←  Infrastructure  ←  Order.Api / Order.Worker
```

Why it matters here:
- The **use case is testable without infrastructure** (7 tests run in ~13 ms, no DB/Redis).
- Redis, Service Bus and PostgreSQL are swappable adapters behind ports — the local
  in-memory topology and the Azure topology reuse the exact same `OrderProcessor`.
- `Program.cs` contains only wiring + HTTP mapping: no business rules, no raw queries.


1. **Order.Api** — tiered order endpoint, stock/product reads, status polling,
   `/internal/resync-stock/{id}` runbook, `/healthz`. Queue provider selected
   by `Messaging:Provider` (ADR-005): RabbitMQ locally, Service Bus on Azure.
2. **FlashSale.Infrastructure/Messaging** — queue adapters behind the same ports
   (`InMemoryOrderQueue` dev/test · `RabbitMQOrderQueue` local ·
   `ServiceBusOrderQueue` Azure), `RedisStockGateway` (Lua CAS),
   `OrderProcessor` + `OrderProcessorHost` (retry/DLQ).
3. **Order.Worker** — standalone worker host sharing the same provider
   selection (consumes RabbitMQ locally, Service Bus on Azure;
   KEDA scales on queue depth — Project 03).
4. **PostgreSQL** — source of truth. **Redis** — fast-fail filter, never authoritative.

## Delivery Semantics (ADR-004)
- At-least-once queue delivery; the consumer is idempotent (DB unique index).
- Retries: exponential backoff, 4 attempts, then DLQ (structured stdout log / Service Bus DLQ).
- HTTP: `202 Accepted` + status polling via `GET /api/orders/{idempotencyKey}`.

## Measured Evidence
| Phase | Design | Accepted | p95 | Correct |
|---|---|---|---|---|
| 2 | naive read-check-write | 50/50 (oversold) | 319 ms | FAIL |
| 3 | atomic conditional UPDATE | 10 | 245 ms | PASS |
| 4 | + high load (200 conc.) | 20 | 574 ms | PASS (but 90% wasted DB hits) |
| 5 | Redis fast-fail + async | 10 | **29 ms** | PASS |

## Known Limitations (deliberately deferred)
- Eventual consistency between 202-accepted and DB-persisted (seconds).
- Service Bus adapter is wired and unit-selected but has no live Azure
  round-trip evidence yet (no `terraform apply` in this task).
- OpenTelemetry wiring deferred to Phase 10; CI/CD and IaC phases 6–9 next.
