# OpsBridge — Azure End-to-End Architecture Alignment & Defense

Refer to the primary architectural defense document:
👉 [Azure Architecture Coverage & Senior Interview Defense](../../../AKS-SRE-Platform/docs/architecture/azure-architecture-coverage-and-defense.md)

## Key Takeaways for OpsBridge:
1. **Concurrency & FlashSale**: Real Redis Lua reservation + conditional Postgres update ensures zero oversell under 50/10 concurrency drills.
2. **Dual-write Protection**: Transactional Outbox pattern with Publisher Confirms eliminates event loss windows between database and broker.
3. **PaaS Trade-off**: Replaced Azure Event Hubs / Functions with containerized Hosted Services on AKS, eliminating cold starts and enabling 100% reproducible local testing via Testcontainers.
4. **Ports & Adapters**: Pluggable `IOrderQueue` interface allows seamless binding to Azure Service Bus if required by enterprise cloud infrastructure.
