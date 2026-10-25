# Explaining accepted-order stall detection

**Problem.** HTTP 202 only means the order was accepted into asynchronous work. A queue outage could leave its reservation committed while no Order row is ever created.

**Decision.** Use the existing durable `inventory.reserved` outbox fact as the starting point and the FlashSale Order row as the result. A timed scan reports overdue gaps; release/compensation events exclude rejected work. A deterministic alert ID plus the outbox unique index prevents duplicate alerts across retries or multiple workers.

**Trade-off.** A scan has up to one interval of detection delay, and a race can leave a historical alert just as an order finishes. The admin's current-candidate view rechecks authoritative state. This rule intentionally does not infer post-payment shipping progress because that transition is not modeled yet.

**Proof.** Fake-clock tests cover the exact deadline and repeated/concurrent scans. The release gate is a controlled queue interruption with cloud evidence tied to the deployed SHA, followed by recovery that removes the order from the current-candidate view.
