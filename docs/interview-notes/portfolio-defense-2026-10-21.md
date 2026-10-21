# OpsBridge interview defense — current portfolio checkpoint

Use the [commit-pinned audit](../evidence/portfolio-audit-2026-10-21.md) before quoting a test result. Demo only the environment available on the day; identify local fixtures and simulated payment by name.

The local container results ran on starting base `1fd2822649d673f037372c2b34b4de38a9c31fab` plus uncommitted fixes. An independent web-only commit later moved HEAD to `d54eb7e150e5d0f74b54f69e122dc5c57c164ff1`; the Python verdict tests were rerun afterward. State those versions when presenting the demo.

## Five-minute walkthrough

1. **Business risk (45 s):** limited stock in a flash sale and supplier receipts that must be reconciled without changing the order system's authoritative data. Show the two bounded contexts in [ADR-017](../adr/017-opsbridge-bounded-context-foundation.md).
2. **Happy path (90 s):** explain `POST /api/orders` → Redis reservation → queue `202` → worker PostgreSQL conditional update + outbox. Show the current [sanitized 50/10 local run](../evidence/portfolio-opsbridge-local-gates-2026-10-21.txt): 10 accepted, 40 × 409, 10 orders, stock 0. For OpsBridge, show signed `GoodsReceiptConfirmed v1` → inbox/outbox → consumer → receipt → PO/GR/invoice match.
3. **Failure path (90 s):** broker down after durable webhook receipt leaves outbox pending; restart drains it. A duplicate event is deduped by event ID; malformed receipt goes through bounded retry and DLQ without `Completed`. The full PostgreSQL/RabbitMQ suite passed 39/39 locally; explain that its retry test covers a successful broker confirm, not an injected nack.
4. **Human decision (45 s):** a FinanceReviewer/Admin resolves a discrepancy. The route policy is the actual authority; Operator cannot approve via HTTP. Review writes the decision and audit, and commits/releases allocation in a DB transaction.
5. **Limits and trade-offs (30 s):** payment is simulated; `202` is not completed fulfillment. The 50/10 p95 is one local run, remote CI has not rerun, and the reservation compensation window remains a P1 finding in the audit.

## Difficult questions and evidence-backed answers

| Role | Challenge | Answer and evidence |
|---|---|---|
| Solution Architect | Why two bounded contexts in one repository? | Separate data ownership matters more than repository count. FlashSale owns orders/payment/inventory; OpsBridge owns supplier ingestion and reconciliation through `OpsBridgeDbContext`. A versioned contract crosses the boundary. ADR-017 lists rejected shared-table writes and premature service extraction. |
| Solution Architect | Why RabbitMQ and an outbox instead of direct publish? | A DB commit followed by broker publish has a loss window. ADR-019 stores the event in the same DB transaction as the state change; dispatcher retries at least once and consumer deduplicates. It adds lag, a table and operational replay. Show `OutboxCrashWindowTests.cs`; do not claim exactly-once transport. |
| Backend | Does 202 mean the order or receipt exists? | No. Order API returns a polling key; webhook 202 means inbox/event/outbox committed. The worker may still fail. The client must supply a stable idempotency key when it wants safe retries; without the header the API generates a fresh key. |
| Backend | What if two buyers win the Redis race? | Redis is a fast filter. PostgreSQL's conditional stock update is the final invariant; failure returns 409 drift/out-of-stock and cannot oversell. Today's disposable 50/10 harness proved exact fill once; the old harness falsely passed 3/47 and 0/50 because resync was dry-run. Show the fixed verdict tests and sanitized output. |
| Backend | Can a duplicate broker message repeat a business effect? | Worker and consumer deduplicate with DB constraints/inbox state. Delivery remains at least once. Explain the claim/lease and crash windows in ADR-019 rather than saying “exactly once” for the whole system. |
| Platform | How do you prove recovery instead of trusting a manifest? | Run the broker-down, crash-window, poison and replay Testcontainers drills, then inspect persisted rows and ACK/DLQ outcomes. The current 39/39 local integration run covers the consumer fix; broker nack and lost-confirm injection remain open. |
| Platform | Why is retry republish safe before ACK? | The consumer creates a RabbitMQ channel with publisher confirmations and tracking enabled. It awaits the retry `BasicPublishAsync` before ACKing the original. If publish throws, the original is still unacked and can redeliver. The integration test checks a successful retry and its header; a broker nack was not injected, so that failure behavior is a reasoned design claim, not a completed drill. |
| Platform | Is this running on AKS with production SLO history? | No current AKS deployment or 30-day SLO history is established by this repo's local tests. Kubernetes overlays are configuration until applied and observed. |
| AI Engineer | Why is there no agent in the order/reconciliation write path? | Inventory conservation and reconciliation are deterministic transactional rules. An agent could summarize an incident or suggest a review, but it must not own stock decrement, payment, allocation or approval. The existing payment adapter is a simulation, not an AI decision. |

## Reproduction commands

From the repository root: `dotnet test tests/UnitTests/FlashSale.UnitTests.csproj -c Release`; `dotnet test tests/OpsBridge.UnitTests/OpsBridge.UnitTests.csproj -c Release`; `dotnet test tests/OpsBridge.ContractTests/OpsBridge.ContractTests.csproj -c Release`; `dotnet test tests/OpsBridge.IntegrationTests/OpsBridge.IntegrationTests.csproj -c Release`; `python3 -m unittest discover -s load-tests/concurrency -p 'test_oversell_demo.py'`. The disposable 50/10 command and environment are recorded in the sanitized local gate evidence; rerun after committing or on another host before treating the claim as repeatable across environments.
