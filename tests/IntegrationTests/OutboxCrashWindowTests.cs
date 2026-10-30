using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using FlashSale.Application.Automation;
using FlashSale.Application.Events;
using FlashSale.Application.Messaging;
using FlashSale.Application.Outbox;
using FlashSale.Application.Orders;
using FlashSale.Application.Persistence;
using FlashSale.Domain;
using FlashSale.Domain.Automation;
using FlashSale.Domain.Events;
using FlashSale.Domain.Outbox;
using FlashSale.Infrastructure.Events;
using FlashSale.Infrastructure.Messaging;
using FlashSale.Infrastructure.Outbox;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace FlashSale.IntegrationTests;

/// <summary>
/// ADR-019 crash-window matrix — the acceptance criterion of the transactional
/// outbox.
///
/// Every scenario runs against a REAL PostgreSQL, a REAL Redis and a REAL
/// RabbitMQ (Testcontainers). Nothing about the database or the broker is
/// mocked: a "broker outage" is a stopped container, a "poison message" is a
/// row the database really rejects, and a "dispatcher crash" is a claim that
/// really expired.
///
/// | window | expectation |
/// |---|---|
/// | (a) before commit          | a failing outbox write rolls the whole transaction back |
/// | (b) after commit, before publish | the event is published after the dispatcher restarts |
/// | (c) after publish, before ProcessedAt | the event is published again and the consumer dedups it |
/// | (d) broker down            | retry + backoff, nothing lost, published once the broker is back |
/// | (e) duplicate delivery     | the consumer applies the side effect exactly once |
/// | (f) poison message         | dead-lettered, with a metric and a visible row state |
/// | (g) API idempotency        | two requests with one Idempotency-Key → one order, one event |
/// </summary>
[Collection("outbox-crash-window")]
public sealed class OutboxCrashWindowTests(OutboxCrashWindowFixture fx)
{
    private const string Exchange = "automation.events";

    private static OrderMessage Msg(int productId, string key, Guid? correlationId = null) =>
        new(productId, 1, key, DateTimeOffset.UtcNow, CorrelationId: correlationId ?? Guid.NewGuid());

    // ------------------------------------------------------------------ (a) ---
    [Fact]
    public async Task A_OutboxWriteFails_TheWholeTransactionRollsBack()
    {
        // The business row and the event row are written in ONE transaction. If
        // the event row cannot be written, the business change must not
        // survive either.
        var productId = await fx.ResetDatabaseAsync(stock: 10);

        await using var db = fx.CreateDbContext();
        var processor = new OrderProcessor(
            new OrderRepository(db),
            new EfWriteTransaction(db),
            // The REAL outbox repository, but the row is poisoned so that the
            // DATABASE rejects it (EventType is varchar(100)) — a genuine
            // database failure, not a stubbed one.
            new OutboxEventWriter(new PoisoningOutboxRepository(new OutboxRepository(db))),
            NullLogger<OrderProcessor>.Instance);

        await Assert.ThrowsAnyAsync<Exception>(
            () => processor.ProcessAsync(Msg(productId, "crash-a"), CancellationToken.None));

        await using var check = fx.CreateDbContext();
        Assert.Equal(0, await check.Orders.CountAsync());
        Assert.Equal(10, await check.Products.Where(p => p.Id == productId)
            .Select(p => p.AvailableStock).FirstAsync());
        Assert.Equal(0, await check.OutboxMessages.CountAsync());
    }

    // ------------------------------------------------------------------ (b) ---
    [Fact]
    public async Task B_DispatcherDiesAfterCommit_TheEventIsPublishedAfterTheRestart()
    {
        await fx.ResetDatabaseAsync();

        var collector = await EventCollector.CreateAsync(fx.RabbitMqConnectionString, "outbox-crash-b");
        await using var _ = collector;

        long rowId;
        await using (var db = fx.CreateDbContext())
        {
            var row = await InsertOrderCreatedAsync(db, new OutboxRepository(db));
            rowId = row.Id;

            // Simulate the crash: a dispatcher claimed the row and died before
            // publishing. The claim lease expires, so the row is claimable
            // again — and it was never lost.
            await new OutboxRepository(db).ClaimBatchAsync(
                Guid.NewGuid(), 10, leaseDuration: TimeSpan.Zero, CancellationToken.None);
        }

        await using (var db = fx.CreateDbContext())
        {
            var row = await db.OutboxMessages.AsNoTracking().SingleAsync(r => r.Id == rowId);
            Assert.Null(row.ProcessedAt);
            Assert.Null(row.DeadLetteredAt);
        }

        // The restart: a fresh dispatcher picks the row up and publishes it.
        await using (var db = fx.CreateDbContext())
        {
            await using var publisher = await RabbitMqDomainEventPublisher.CreateAsync(
                fx.RabbitMqConnectionString, NullLogger<RabbitMqDomainEventPublisher>.Instance);

            var dispatcher = new OutboxDispatcherUseCase(
                new OutboxRepository(db), publisher, NullLogger<OutboxDispatcherUseCase>.Instance);

            var result = await dispatcher.ExecuteBatchAsync(50);
            Assert.Equal(1, result.Dispatched);
        }

        var messages = await collector.WaitForAsync(1, TimeSpan.FromSeconds(15));
        var envelope = EventEnvelope.TryParse(Encoding.UTF8.GetString(messages[0].Body));
        Assert.NotNull(envelope);
        Assert.Equal("order.created", envelope.EventType);

        await using (var db = fx.CreateDbContext())
        {
            var row = await db.OutboxMessages.AsNoTracking().SingleAsync(r => r.Id == rowId);
            Assert.NotNull(row.ProcessedAt);
            Assert.Equal(0, row.RetryCount);
        }
    }

    // ------------------------------------------------------------------ (c) ---
    [Fact]
    public async Task C_PublishedButNotMarked_TheRepublishIsDeduplicatedByTheConsumer()
    {
        await fx.ResetDatabaseAsync();

        var collector = await EventCollector.CreateAsync(fx.RabbitMqConnectionString, "outbox-crash-c");
        await using var _ = collector;

        OutboxMessage row;
        await using (var db = fx.CreateDbContext())
        {
            row = await InsertOrderCreatedAsync(db, new OutboxRepository(db));

            // "Published, then crashed before ProcessedAt": the message is on
            // the bus, the row is still unprocessed.
            var envelope = EventEnvelope.TryParse(row.Payload)!;
            await using var publisher = await RabbitMqDomainEventPublisher.CreateAsync(
                fx.RabbitMqConnectionString, NullLogger<RabbitMqDomainEventPublisher>.Instance);
            await publisher.PublishAsync(new OutboxEnvelopeEvent(envelope));
        }

        // The dispatcher republishes it (at-least-once).
        await using (var db = fx.CreateDbContext())
        {
            await using var publisher = await RabbitMqDomainEventPublisher.CreateAsync(
                fx.RabbitMqConnectionString, NullLogger<RabbitMqDomainEventPublisher>.Instance);

            var dispatcher = new OutboxDispatcherUseCase(
                new OutboxRepository(db), publisher, NullLogger<OutboxDispatcherUseCase>.Instance);

            var result = await dispatcher.ExecuteBatchAsync(50);
            Assert.Equal(1, result.Dispatched);
        }

        var messages = await collector.WaitForAsync(2, TimeSpan.FromSeconds(15));
        Assert.Equal(2, messages.Count);

        // The consumer sees the same event_id twice and applies it once.
        var processor = CreateConsumerProcessor();
        var outcomes = new List<AutomationEventOutcome>();
        foreach (var message in messages)
        {
            outcomes.Add(await processor.ProcessAsync(message.Body, ReadHeader(message, "event-type")));
        }

        Assert.Equal(AutomationEventOutcome.Processed, outcomes[0]);
        Assert.Equal(AutomationEventOutcome.Duplicate, outcomes[1]);

        await using var check = fx.CreateDbContext();
        Assert.Equal(1, await check.AutomationRuns.CountAsync(r => r.TriggerType == "domain.event"));
        Assert.Equal(1, await check.InboxMessages.CountAsync());
    }

    // ------------------------------------------------------------------ (d) ---
    [Fact]
    public async Task D_BrokerDown_TheEventIsRetriedAndPublishedAfterTheRecovery()
    {
        await fx.ResetDatabaseAsync();

        long rowId;
        await using (var db = fx.CreateDbContext())
        {
            var row = await InsertOrderCreatedAsync(db, new OutboxRepository(db));
            rowId = row.Id;
        }

        // The publisher is connected, then the broker goes down: the publish
        // really fails.
        await using var publisher = await RabbitMqDomainEventPublisher.CreateAsync(
            fx.RabbitMqConnectionString, NullLogger<RabbitMqDomainEventPublisher>.Instance);

        await fx.StopBrokerAsync();
        try
        {
            await using (var db = fx.CreateDbContext())
            {
                var dispatcher = new OutboxDispatcherUseCase(
                    new OutboxRepository(db), publisher, NullLogger<OutboxDispatcherUseCase>.Instance);

                var result = await dispatcher.ExecuteBatchAsync(50);
                Assert.Equal(0, result.Dispatched);
                Assert.Equal(1, result.Failed);
                Assert.Equal(1, result.Retried);
            }
        }
        finally
        {
            await fx.StartBrokerAsync();
        }

        // Nothing was lost: the row is still there, unprocessed, with a retry
        // scheduled.
        await using (var db = fx.CreateDbContext())
        {
            var row = await db.OutboxMessages.AsNoTracking().SingleAsync(r => r.Id == rowId);
            Assert.Null(row.ProcessedAt);
            Assert.Null(row.DeadLetteredAt);
            Assert.Equal(1, row.RetryCount);
            Assert.NotNull(row.NextAttemptAt);
        }

        // The broker is back: the queue is (re)declared and the retry
        // publishes the event.
        var collector = await EventCollector.CreateAsync(fx.RabbitMqConnectionString, "outbox-crash-d");
        await using var _ = collector;

        await using (var db = fx.CreateDbContext())
        {
            // Make the backoff due instead of sleeping through it.
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "OutboxMessages" SET "NextAttemptAt" = now() - interval '1 second' """);

            await using var recovered = await RabbitMqDomainEventPublisher.CreateAsync(
                fx.RabbitMqConnectionString, NullLogger<RabbitMqDomainEventPublisher>.Instance);

            var dispatcher = new OutboxDispatcherUseCase(
                new OutboxRepository(db), recovered, NullLogger<OutboxDispatcherUseCase>.Instance);

            var result = await dispatcher.ExecuteBatchAsync(50);
            Assert.Equal(1, result.Dispatched);
        }

        var messages = await collector.WaitForAsync(1, TimeSpan.FromSeconds(20));
        var envelope = EventEnvelope.TryParse(Encoding.UTF8.GetString(messages[0].Body));
        Assert.NotNull(envelope);
        Assert.Equal("order.created", envelope.EventType);

        await using (var db = fx.CreateDbContext())
        {
            var row = await db.OutboxMessages.AsNoTracking().SingleAsync(r => r.Id == rowId);
            Assert.NotNull(row.ProcessedAt);
        }
    }

    // ------------------------------------------------------------------ (e) ---
    [Fact]
    public async Task E_DuplicateDelivery_TheConsumerAppliesTheSideEffectOnce()
    {
        await fx.ResetDatabaseAsync();

        var collector = await EventCollector.CreateAsync(fx.RabbitMqConnectionString, "outbox-crash-e");
        await using var _ = collector;

        OutboxMessage row;
        await using (var db = fx.CreateDbContext())
        {
            row = await InsertOrderCreatedAsync(db, new OutboxRepository(db));
        }

        // The same event is delivered twice.
        await using (var publisher = await RabbitMqDomainEventPublisher.CreateAsync(
            fx.RabbitMqConnectionString, NullLogger<RabbitMqDomainEventPublisher>.Instance))
        {
            var envelope = EventEnvelope.TryParse(row.Payload)!;
            await publisher.PublishAsync(new OutboxEnvelopeEvent(envelope));
            await publisher.PublishAsync(new OutboxEnvelopeEvent(envelope));
        }

        var messages = await collector.WaitForAsync(2, TimeSpan.FromSeconds(15));
        Assert.Equal(2, messages.Count);

        var processor = CreateConsumerProcessor();
        var outcomes = new List<AutomationEventOutcome>();
        foreach (var message in messages)
        {
            outcomes.Add(await processor.ProcessAsync(message.Body, ReadHeader(message, "event-type")));
        }

        Assert.Equal(1, outcomes.Count(o => o == AutomationEventOutcome.Processed));
        Assert.Equal(1, outcomes.Count(o => o == AutomationEventOutcome.Duplicate));

        await using var check = fx.CreateDbContext();
        Assert.Equal(1, await check.AutomationRuns.CountAsync(r => r.TriggerType == "domain.event"));
        Assert.Equal(1, await check.InboxMessages.CountAsync(i => i.ProcessedAt != null));
    }

    // ------------------------------------------------------------------ (f) ---
    [Fact]
    public async Task F_PoisonMessage_IsDeadLetteredAndObservable()
    {
        await fx.ResetDatabaseAsync();

        // A row whose payload is not a canonical envelope can never be
        // deduplicated by a consumer — it must not reach the bus.
        long rowId;
        await using (var db = fx.CreateDbContext())
        {
            var poison = new OutboxMessage
            {
                MessageId = Guid.NewGuid(),
                EventType = "order.created",
                Topic = Exchange,
                Payload = "this-is-not-an-envelope"
            };
            await new OutboxRepository(db).EnqueueAsync(poison);
            rowId = poison.Id;
        }

        var deadLettered = 0L;
        var failures = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkerTelemetry.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "flashsale.worker.outbox.dead_lettered")
            {
                Interlocked.Add(ref deadLettered, measurement);
            }
            else if (instrument.Name == "flashsale.worker.outbox.publish_failures")
            {
                Interlocked.Add(ref failures, measurement);
            }
        });
        listener.Start();

        // The REAL dispatcher hosted service does the work: retries with
        // backoff, then the dead-letter state.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(fx.PostgresConnectionString));
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IInboxRepository, InboxRepository>();
        services.AddScoped<OutboxDispatcherUseCase>();
        var brokerPublisher = await RabbitMqDomainEventPublisher.CreateAsync(
            fx.RabbitMqConnectionString, NullLogger<RabbitMqDomainEventPublisher>.Instance);
        services.AddSingleton<IDomainEventPublisher>(brokerPublisher);

        await using var provider = services.BuildServiceProvider();
        var host = new OutboxDispatcherHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxDispatcherHostedService>.Instance);

        await host.StartAsync(CancellationToken.None);
        try
        {
            // Each tick the row burns one attempt; the test makes the backoff
            // due instead of sleeping through it.
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(500);
                await using var db = fx.CreateDbContext();
                var row = await db.OutboxMessages.AsNoTracking().SingleAsync(r => r.Id == rowId);
                if (row.DeadLetteredAt is not null)
                {
                    break;
                }

                await db.Database.ExecuteSqlRawAsync(
                    """UPDATE "OutboxMessages" SET "NextAttemptAt" = now() - interval '1 second' """);
            }
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        await using (var db = fx.CreateDbContext())
        {
            var row = await db.OutboxMessages.AsNoTracking().SingleAsync(r => r.Id == rowId);

            // Dead-lettered, visible, and NOT silently dropped.
            Assert.NotNull(row.DeadLetteredAt);
            Assert.Equal(OutboxMessage.MaxRetryCount, row.RetryCount);
            Assert.Null(row.ProcessedAt);
            Assert.NotNull(row.Error);

            // The operator can see it.
            var stuck = await new OutboxRepository(db).GetStuckAsync(50);
            Assert.Contains(stuck, r => r.Id == rowId);
        }

        // The signal is observable, not only logged.
        Assert.True(deadLettered >= 1, $"expected the dead-letter metric, saw {deadLettered}");
        Assert.True(failures >= 1, $"expected the publish-failure metric, saw {failures}");
    }

    // ------------------------------------------------------------------ (g) ---
    [Fact]
    public async Task G_TwoRequestsWithOneIdempotencyKey_ProduceOneOrderAndOneEvent()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        var gateway = fx.CreateRedisGateway();
        await gateway.SetStockAsync(productId, 10);

        await using var db = fx.CreateDbContext();
        var transaction = new EfWriteTransaction(db);
        var writer = new OutboxEventWriter(new OutboxRepository(db));
        var processor = new OrderProcessor(new OrderRepository(db), transaction, writer, NullLogger<OrderProcessor>.Instance);

        var useCase = new SubmitOrderUseCase(
            gateway,
            new InMemoryOrderQueue(),
            new OrderReadModel(db),
            processor,
            transaction,
            writer,
            new PaymentAutomationOptions(),
            NullLogger<SubmitOrderUseCase>.Instance);

        const string key = "crash-g-key";
        var first = await useCase.ExecuteAsync(productId, 1, key, null, CancellationToken.None);
        var second = await useCase.ExecuteAsync(productId, 1, key, null, CancellationToken.None);

        Assert.Equal(SubmitOrderOutcome.Accepted, first.Outcome);
        Assert.Equal(SubmitOrderOutcome.DuplicateRequest, second.Outcome);

        // The worker fulfils the order — and the queue redelivers it.
        var message = Msg(productId, key);
        await processor.ProcessAsync(message, CancellationToken.None);
        await processor.ProcessAsync(message, CancellationToken.None);

        await using var check = fx.CreateDbContext();
        Assert.Equal(1, await check.Orders.CountAsync(o => o.IdempotencyKey == key));
        Assert.Equal(1, await check.OutboxMessages.CountAsync(m => m.EventType == "order.created"));
        Assert.Equal(1, await check.OutboxMessages.CountAsync(m => m.EventType == "inventory.reserved"));
    }

    // ------------------------------------------------------------------ (h) ---
    [Fact]
    public async Task H_QueueRejectsThenRepairRepeats_OneReleaseInPostgresAndRabbitMq()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        var gateway = fx.CreateRedisGateway();
        await gateway.SetStockAsync(productId, 10);
        var collector = await EventCollector.CreateAsync(fx.RabbitMqConnectionString, "outbox-crash-h-" + Guid.NewGuid().ToString("N"));
        await using var _ = collector;

        const string key = "queue-reject-repair-once";
        Guid correlationId;
        await using (var db = fx.CreateDbContext())
        {
            var transaction = new EfWriteTransaction(db);
            var writer = new OutboxEventWriter(new OutboxRepository(db));
            var useCase = new SubmitOrderUseCase(
                gateway, new RejectingOrderQueue(), new OrderReadModel(db),
                new OrderProcessor(new OrderRepository(db), transaction, writer, NullLogger<OrderProcessor>.Instance),
                transaction, writer, new PaymentAutomationOptions(), NullLogger<SubmitOrderUseCase>.Instance);
            Assert.Equal(SubmitOrderOutcome.SystemBusy,
                (await useCase.ExecuteAsync(productId, 1, key, null, CancellationToken.None)).Outcome);
            correlationId = (await db.OutboxMessages.SingleAsync(m => m.EventType == "inventory.reserved" && m.AggregateId == key)).CorrelationId!.Value;
        }

        // Two repair workers each get a fresh DB context, as in a real retry.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = fx.CreateDbContext();
            var transaction = new EfWriteTransaction(db);
            var writer = new OutboxEventWriter(new OutboxRepository(db));
            var useCase = new SubmitOrderUseCase(
                gateway, new RejectingOrderQueue(), new OrderReadModel(db),
                new OrderProcessor(new OrderRepository(db), transaction, writer, NullLogger<OrderProcessor>.Instance),
                transaction, writer, new PaymentAutomationOptions(), NullLogger<SubmitOrderUseCase>.Instance);
            Assert.True(await useCase.ReconcileOrphanReservationAsync(productId, 1, key, correlationId, CancellationToken.None));
        }

        await using (var db = fx.CreateDbContext())
        {
            Assert.Equal(0, await db.Orders.CountAsync());
            Assert.Equal(10, await db.Products.Where(p => p.Id == productId).Select(p => p.AvailableStock).SingleAsync());
            Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.EventType == "inventory.reserved" && m.AggregateId == key));
            Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.EventType == "inventory.released" && m.AggregateId == key));

            await using var publisher = await RabbitMqDomainEventPublisher.CreateAsync(
                fx.RabbitMqConnectionString, NullLogger<RabbitMqDomainEventPublisher>.Instance);
            var dispatcher = new OutboxDispatcherUseCase(new OutboxRepository(db), publisher, NullLogger<OutboxDispatcherUseCase>.Instance);
            Assert.Equal(2, (await dispatcher.ExecuteBatchAsync(50)).Dispatched);
        }

        var delivered = await collector.WaitForAsync(2, TimeSpan.FromSeconds(15));
        var types = delivered.Select(m => EventEnvelope.TryParse(Encoding.UTF8.GetString(m.Body))?.EventType).ToList();
        Assert.Equal(1, types.Count(t => t == "inventory.reserved"));
        Assert.Equal(1, types.Count(t => t == "inventory.released"));
        Assert.False(await collector.HasReadyMessageAsync());
    }

    [Fact]
    public async Task I_PreUpgradeRandomReleaseId_RepairDoesNotEmitDuplicate()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        const string key = "legacy-release-once";
        var correlationId = Guid.NewGuid();
        var collector = await EventCollector.CreateAsync(fx.RabbitMqConnectionString, "outbox-crash-i-" + Guid.NewGuid().ToString("N"));
        await using var _ = collector;

        await using (var db = fx.CreateDbContext())
        {
            var transaction = new EfWriteTransaction(db);
            await transaction.BeginAsync();
            await new OutboxEventWriter(new OutboxRepository(db)).EnqueueAsync(
                new InventoryReleasedEvent(Guid.NewGuid().ToString("D"), correlationId, "pre-upgrade",
                    DateTimeOffset.UtcNow, 0, productId, 1), OutboxEventWriter.OrderAggregate, key);
            await transaction.CommitAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            var transaction = new EfWriteTransaction(db);
            var writer = new OutboxEventWriter(new OutboxRepository(db));
            var useCase = new SubmitOrderUseCase(
                fx.CreateRedisGateway(), new RejectingOrderQueue(), new OrderReadModel(db),
                new OrderProcessor(new OrderRepository(db), transaction, writer, NullLogger<OrderProcessor>.Instance),
                transaction, writer, new PaymentAutomationOptions(), NullLogger<SubmitOrderUseCase>.Instance);
            Assert.True(await useCase.ReconcileOrphanReservationAsync(productId, 1, key, correlationId, CancellationToken.None));
            Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.EventType == "inventory.released" && m.AggregateId == key));

            await using var publisher = await RabbitMqDomainEventPublisher.CreateAsync(
                fx.RabbitMqConnectionString, NullLogger<RabbitMqDomainEventPublisher>.Instance);
            var dispatcher = new OutboxDispatcherUseCase(new OutboxRepository(db), publisher, NullLogger<OutboxDispatcherUseCase>.Instance);
            Assert.Equal(1, (await dispatcher.ExecuteBatchAsync(50)).Dispatched);
        }

        var delivered = await collector.WaitForAsync(1, TimeSpan.FromSeconds(15));
        Assert.Equal("inventory.released", EventEnvelope.TryParse(Encoding.UTF8.GetString(delivered[0].Body))?.EventType);
        Assert.False(await collector.HasReadyMessageAsync());
    }

    [Fact]
    public async Task J_SameHttpKeyAfterQueueRejection_TwoAttemptsHaveTwoBalancedEventPairs()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        var gateway = fx.CreateRedisGateway();
        await gateway.SetStockAsync(productId, 10);
        const string key = "same-key-after-503";
        var collector = await EventCollector.CreateAsync(fx.RabbitMqConnectionString, "outbox-crash-j-" + Guid.NewGuid().ToString("N"));
        await using var _ = collector;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = fx.CreateDbContext();
            var transaction = new EfWriteTransaction(db);
            var writer = new OutboxEventWriter(new OutboxRepository(db));
            var useCase = new SubmitOrderUseCase(
                gateway, new RejectingOrderQueue(), new OrderReadModel(db),
                new OrderProcessor(new OrderRepository(db), transaction, writer, NullLogger<OrderProcessor>.Instance),
                transaction, writer, new PaymentAutomationOptions(), NullLogger<SubmitOrderUseCase>.Instance);
            Assert.Equal(SubmitOrderOutcome.SystemBusy,
                (await useCase.ExecuteAsync(productId, 1, key, null, CancellationToken.None)).Outcome);
        }

        Assert.Equal(10, await gateway.GetCachedStockAsync(productId));
        await using (var db = fx.CreateDbContext())
        {
            Assert.Equal(0, await db.Orders.CountAsync());
            Assert.Equal(10, await db.Products.Where(p => p.Id == productId).Select(p => p.AvailableStock).SingleAsync());
            var events = await db.OutboxMessages.Where(m => m.AggregateId == key).ToListAsync();
            Assert.Equal(2, events.Count(m => m.EventType == "inventory.reserved"));
            Assert.Equal(2, events.Count(m => m.EventType == "inventory.released"));
            Assert.Equal(2, events.Select(m => m.CorrelationId).Distinct().Count());
            Assert.All(events.GroupBy(m => m.CorrelationId), group =>
            {
                Assert.Equal(2, group.Count());
                Assert.Contains(group, m => m.EventType == "inventory.reserved");
                Assert.Contains(group, m => m.EventType == "inventory.released");
            });

            await using var publisher = await RabbitMqDomainEventPublisher.CreateAsync(
                fx.RabbitMqConnectionString, NullLogger<RabbitMqDomainEventPublisher>.Instance);
            var dispatcher = new OutboxDispatcherUseCase(new OutboxRepository(db), publisher, NullLogger<OutboxDispatcherUseCase>.Instance);
            Assert.Equal(4, (await dispatcher.ExecuteBatchAsync(50)).Dispatched);
        }

        var delivered = await collector.WaitForAsync(4, TimeSpan.FromSeconds(15));
        Assert.Equal(2, delivered.Count(m => EventEnvelope.TryParse(Encoding.UTF8.GetString(m.Body))?.EventType == "inventory.reserved"));
        Assert.Equal(2, delivered.Count(m => EventEnvelope.TryParse(Encoding.UTF8.GetString(m.Body))?.EventType == "inventory.released"));
        Assert.False(await collector.HasReadyMessageAsync());
    }

    [Fact]
    public async Task K_FirstReleaseSaveFails_TrackedFailedRowDoesNotPoisonRetry()
    {
        var productId = await fx.ResetDatabaseAsync(stock: 10);
        var gateway = fx.CreateRedisGateway();
        await gateway.SetStockAsync(productId, 10);
        await using var db = fx.CreateDbContext();
        var poisonFirstRelease = true;
        var repository = new PoisoningOutboxRepository(new OutboxRepository(db), message =>
        {
            if (message.EventType != "inventory.released" || !poisonFirstRelease) return false;
            poisonFirstRelease = false;
            return true;
        });
        var transaction = new EfWriteTransaction(db);
        var writer = new OutboxEventWriter(repository);
        var useCase = new SubmitOrderUseCase(
            gateway, new RejectingOrderQueue(), new OrderReadModel(db),
            new OrderProcessor(new OrderRepository(db), transaction, writer, NullLogger<OrderProcessor>.Instance),
            transaction, writer, new PaymentAutomationOptions(), NullLogger<SubmitOrderUseCase>.Instance);

        Assert.Equal(SubmitOrderOutcome.SystemBusy,
            (await useCase.ExecuteAsync(productId, 1, "poison-once-retry", null, CancellationToken.None)).Outcome);

        await using var verify = fx.CreateDbContext();
        Assert.Equal(0, await verify.Orders.CountAsync());
        Assert.Equal(10, await gateway.GetCachedStockAsync(productId));
        Assert.Equal(1, await verify.OutboxMessages.CountAsync(m => m.EventType == "inventory.reserved"));
        Assert.Equal(1, await verify.OutboxMessages.CountAsync(m => m.EventType == "inventory.released"));
        Assert.Equal(0, await verify.OutboxMessages.CountAsync(m => m.EventType == "inventory.compensation_failed"));
        Assert.Equal(1, await CompensationLookupIndexCountAsync(verify));
    }

    [Fact]
    public async Task L_UpgradeAddsCompensationLookupIndex_WithoutLosingLegacyOutboxRow()
    {
        await using var db = fx.CreateDbContext();
        await db.Database.EnsureDeletedAsync();
        await db.GetService<IMigrator>().MigrateAsync("20261020151133_AddInboxClaim");

        const string key = "upgrade-legacy-release";
        var correlationId = Guid.NewGuid();
        await new OutboxEventWriter(new OutboxRepository(db)).EnqueueAsync(
            new InventoryReleasedEvent(Guid.NewGuid().ToString("D"), correlationId, "pre-upgrade",
                DateTimeOffset.UtcNow, 0, 1, 1), OutboxEventWriter.OrderAggregate, key);

        Assert.Equal(0, await CompensationLookupIndexCountAsync(db));
        await db.Database.MigrateAsync();

        await using var verify = fx.CreateDbContext();
        Assert.Equal(1, await CompensationLookupIndexCountAsync(verify));
        Assert.True(await new OutboxRepository(verify).HasEventAsync(
            "inventory.released", OutboxEventWriter.OrderAggregate, key, correlationId));
        Assert.Equal(1, await verify.OutboxMessages.CountAsync(m => m.AggregateId == key));
    }

    private static Task<int> CompensationLookupIndexCountAsync(AppDbContext db) =>
        db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*)::integer AS "Value"
            FROM pg_indexes
            WHERE schemaname = current_schema()
              AND tablename = 'OutboxMessages'
              AND indexname = 'IX_OutboxMessages_CompensationLookup'
            """).SingleAsync();

    [Fact]
    public async Task M_OtherUniqueConstraint_IsNotMisreportedAsMessageIdDuplicate()
    {
        await fx.ResetDatabaseAsync();
        long existingId;
        await using (var db = fx.CreateDbContext())
        {
            var row = await InsertOrderCreatedAsync(db, new OutboxRepository(db));
            existingId = row.Id;
        }

        await using (var db = fx.CreateDbContext())
        {
            var repository = new OutboxRepository(db);
            var conflictingPrimaryKey = new OutboxMessage
            {
                Id = existingId,
                MessageId = Guid.NewGuid(),
                EventType = "inventory.released",
                Topic = Exchange,
                Payload = "{}",
                AggregateType = OutboxEventWriter.OrderAggregate,
                AggregateId = "pk-collision"
            };
            var exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => repository.EnqueueAsync(conflictingPrimaryKey));
            Assert.IsType<Npgsql.PostgresException>(exception.InnerException);
            Assert.Equal(Microsoft.EntityFrameworkCore.EntityState.Detached, db.Entry(conflictingPrimaryKey).State);
        }

        await using var verify = fx.CreateDbContext();
        Assert.Equal(1, await verify.OutboxMessages.CountAsync());
    }

    private sealed class RejectingOrderQueue : IOrderQueueProducer
    {
        public ValueTask<bool> EnqueueAsync(OrderMessage message, CancellationToken ct = default) => ValueTask.FromResult(false);
    }

    // ------------------------------------------------------------- helpers ---
    private static async Task<OutboxMessage> InsertOrderCreatedAsync(AppDbContext db, IOutboxRepository outbox)
    {
        var message = new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            EventType = "order.created",
            Topic = Exchange,
            AggregateType = "order",
            AggregateId = "1",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var envelope = new EventEnvelope
        {
            EventId = message.MessageId.ToString("D"),
            EventType = message.EventType,
            AggregateType = message.AggregateType,
            AggregateId = message.AggregateId,
            OccurredAt = message.CreatedAt,
            CorrelationId = Guid.NewGuid(),
            Payload = JsonSerializer.Serialize(new OrderCreatedEvent(
                message.MessageId.ToString("D"),
                Guid.NewGuid(),
                "crash-window-test",
                DateTimeOffset.UtcNow,
                1,
                1,
                1,
                null,
                "crash-window-key",
                OrderStatus.PendingPayment))
        };

        message.Payload = envelope.ToJson();
        await outbox.EnqueueAsync(message);
        return message;
    }

    private AutomationEventProcessor CreateConsumerProcessor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(fx.PostgresConnectionString));
        services.AddScoped<IAutomationRunRepository, AutomationRunRepository>();
        services.AddScoped<IInboxRepository, InboxRepository>();

        var provider = services.BuildServiceProvider();
        return new AutomationEventProcessor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AutomationEventProcessor>.Instance);
    }

    private static string? ReadHeader(CollectedMessage message, string name)
    {
        if (message.Headers is null || !message.Headers.TryGetValue(name, out var raw))
        {
            return null;
        }

        return raw switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string s => s,
            _ => raw?.ToString()
        };
    }

    /// <summary>
    /// Writes the outbox row with a payload the DATABASE rejects (the event
    /// type column is varchar(100)). The failure is real: it comes from
    /// PostgreSQL, not from a stubbed repository.
    /// </summary>
    private sealed class PoisoningOutboxRepository(IOutboxRepository inner, Func<OutboxMessage, bool>? shouldPoison = null) : IOutboxRepository
    {
        public Task<bool> HasEventAsync(string eventType, string aggregateType, string aggregateId, Guid correlationId, CancellationToken ct = default) =>
            inner.HasEventAsync(eventType, aggregateType, aggregateId, correlationId, ct);
        public Task<OutboxMessage?> GetByMessageIdAsync(Guid messageId, CancellationToken ct = default) =>
            inner.GetByMessageIdAsync(messageId, ct);


        public Task EnqueueAsync(OutboxMessage message, CancellationToken ct = default)
        {
            if (shouldPoison?.Invoke(message) ?? true)
                message.EventType = new string('x', 200);
            return inner.EnqueueAsync(message, ct);
        }

        public Task<IReadOnlyList<OutboxMessage>> GetUnprocessedAsync(int batchSize = 50, CancellationToken ct = default)
            => inner.GetUnprocessedAsync(batchSize, ct);

        public Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
            Guid claimToken, int batchSize = 50, TimeSpan? leaseDuration = null, CancellationToken ct = default)
            => inner.ClaimBatchAsync(claimToken, batchSize, leaseDuration, ct);

        public Task MarkProcessedAsync(long id, Guid claimToken, CancellationToken ct = default)
            => inner.MarkProcessedAsync(id, claimToken, ct);

        public Task<OutboxFailureOutcome> MarkFailedAsync(long id, Guid claimToken, string error, CancellationToken ct = default)
            => inner.MarkFailedAsync(id, claimToken, error, ct);

        public Task<IReadOnlyList<OutboxMessage>> GetStuckAsync(int limit = 50, CancellationToken ct = default)
            => inner.GetStuckAsync(limit, ct);

        public Task<int> CountStuckAsync(CancellationToken ct = default) => inner.CountStuckAsync(ct);

        public Task<int> CountPendingAsync(CancellationToken ct = default) => inner.CountPendingAsync(ct);

        public Task<int> RequeueStuckAsync(CancellationToken ct = default) => inner.RequeueStuckAsync(ct);
    }

    /// <summary>One message taken off the real broker, with its headers.</summary>
    private sealed record CollectedMessage(byte[] Body, Dictionary<string, object?>? Headers);

    /// <summary>
    /// Pulls messages off a REAL queue bound to the event exchange. The queue
    /// is declared BEFORE the publish, otherwise a topic exchange with no
    /// bound queue drops the message.
    /// </summary>
    private sealed class EventCollector : IAsyncDisposable
    {
        private readonly IConnection _connection;
        private readonly IChannel _channel;
        private readonly string _queueName;

        private EventCollector(IConnection connection, IChannel channel, string queueName)
        {
            _connection = connection;
            _channel = channel;
            _queueName = queueName;
        }

        public static async Task<EventCollector> CreateAsync(string connectionString, string queueName)
        {
            var factory = new ConnectionFactory { Uri = new Uri(connectionString) };
            var connection = await factory.CreateConnectionAsync();
            var channel = await connection.CreateChannelAsync();
            await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true);
            await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false, arguments: null);
            await channel.QueueBindAsync(queueName, Exchange, "#");
            return new EventCollector(connection, channel, queueName);
        }

        public async Task<List<CollectedMessage>> WaitForAsync(int count, TimeSpan timeout)
        {
            var collected = new List<CollectedMessage>();
            var deadline = DateTime.UtcNow + timeout;

            while (collected.Count < count && DateTime.UtcNow < deadline)
            {
                var result = await _channel.BasicGetAsync(_queueName, autoAck: true);
                if (result is null)
                {
                    await Task.Delay(100);
                    continue;
                }

                var headers = result.BasicProperties.Headers is null
                    ? null
                    : new Dictionary<string, object?>(
                        result.BasicProperties.Headers.ToDictionary(h => h.Key, h => (object?)h.Value));

                collected.Add(new CollectedMessage(result.Body.ToArray(), headers));
            }

            return collected;
        }

        public async Task<bool> HasReadyMessageAsync() =>
            await _channel.BasicGetAsync(_queueName, autoAck: true) is not null;

        public async ValueTask DisposeAsync()
        {
            try { await _channel.CloseAsync(); } catch { /* shutdown race */ }
            try { await _channel.DisposeAsync(); } catch { }
            try { await _connection.DisposeAsync(); } catch { }
        }
    }
}
