# Procurement lag detector draft

The `OpsBridge.Application.StuckOrder` and `OpsBridge.Domain.StuckOrder` files are an unfinished draft for detecting **purchase orders** that have not progressed to goods receipt or invoice. They are not the customer fulfillment detector for US-C-07 / FR-C04. FlashSale owns customer orders under ADR-017; its separate accepted-reservation detector is documented in [stuck-order-detection.md](stuck-order-detection.md).

The procurement draft is **not registered** in the API or dependency injection container, and its entity is not part of `OpsBridgeDbContext`. No migration exists for its proposed alert table. This prevents the draft from serving a GET that writes alerts, incrementing retries on each read, or depending on a table that has not been migrated. The source files remain available for a separate reviewed feature.

Before enabling it, define a procurement-specific requirement and SLA, then add a read-only list route and a separate authorized scan command. The scan needs a configurable threshold, cursor or pagination over all eligible POs, a unique database constraint and migration, concurrent/idempotent tests, explicit state transitions, safe error responses, and cloud evidence. The existing FlashSale `/api/outbox/stuck` and `/api/outbox/requeue` routes describe **message publication lag**, not procurement or customer-order fulfillment.
