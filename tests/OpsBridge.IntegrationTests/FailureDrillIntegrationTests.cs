using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpsBridge.Application.InboxOutbox;
using OpsBridge.Application.Ingestion;
using OpsBridge.Application.Reconciliation;
using OpsBridge.Domain;
using OpsBridge.Domain.Connectors;
using OpsBridge.Domain.DLQ;
using OpsBridge.Domain.InboxOutbox;
using OpsBridge.Domain.Ingestion;
using OpsBridge.Infrastructure.Messaging;
using OpsBridge.Infrastructure.Persistence;
using OpsBridge.Infrastructure.Security;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OpsBridge.IntegrationTests;

[Collection("OpsBridgeIntegration")]
public class FailureDrillIntegrationTests(OpsBridgeFixture fixture)
{
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw=";

    private async Task SeedConnectorAsync()
    {
        await fixture.ResetAsync();

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();

        db.Connectors.Add(new ConnectorConfig
        {
            Id = Guid.NewGuid(),
            Name = "FactoryFlow Failure Drill",
            Type = ConnectorType.FactoryFlowWebhook,
            Secret = Secret,
            IsActive = true
        });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task FailureDrill1_RabbitMqDown_WebhookStillAcceptedAndStoredInOutbox()
    {
        // DRILL 1: Webhook arrives. Even if broker is disconnected, Postgres transaction succeeds.
        await SeedConnectorAsync();
        var client = fixture.Factory.CreateClient();

        var payload = File.ReadAllText(Path.Combine("..", "..", "..", "..", "fixtures", "valid-goods-receipt-v1.json"));
        var eventId = "d1111111-1111-1111-1111-111111111111";
        payload = payload.Replace("a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d", eventId);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = HmacWebhookVerifier.ComputeSvixSignatureHeader(payload, timestamp, eventId, Secret);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/factoryflow")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("svix-id", eventId);
        req.Headers.Add("svix-timestamp", timestamp);
        req.Headers.Add("svix-signature", signature);

        var response = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Verify committed in DB as Pending Outbox
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();

        var outbox = await db.OutboxMessages.FirstOrDefaultAsync(o => o.Payload.Contains(eventId));
        Assert.NotNull(outbox);
        Assert.Equal("Pending", outbox.Status);
        Assert.Null(outbox.ProcessedAt);
    }

    [Fact]
    public async Task FailureDrill2_ApiCrashesImmediatelyAfterDbCommit_OutboxPublisherRecovers()
    {
        // DRILL 2: Message was committed to Postgres Outbox table, then API process died.
        // Upon recovery, OutboxPublisher picks it up and publishes to RabbitMQ with Publisher Confirms.
        await fixture.ResetAsync();

        var eventId = Guid.NewGuid();
        var payload = $"{{\"eventId\":\"{eventId}\",\"eventType\":\"GoodsReceiptConfirmed\",\"data\":{{}}}}";

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                EventType = "GoodsReceiptConfirmed",
                Topic = RabbitMqTopology.InboundRoutingKey.Replace("#", "goods-receipt"),
                Payload = payload,
                Status = "Pending",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Run OutboxPublisher with real RabbitMQ connection
        await using var publisher = await RabbitMqEventPublisher.CreateAsync(
            fixture.RabbitMqConnectionString,
            NullLogger<RabbitMqEventPublisher>.Instance);

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDbContext<OpsBridgeDbContext>(opt => opt.UseNpgsql(fixture.ConnectionString));
        serviceCollection.AddScoped<IOutboxRepository, EfOutboxRepository>();
        serviceCollection.AddSingleton<OpsBridge.Application.Messaging.IEventPublisher>(publisher);

        var sp = serviceCollection.BuildServiceProvider();
        var outboxWorker = new OutboxPublisherHostedService(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<OutboxPublisherHostedService>.Instance);

        var drained = await outboxWorker.ProcessPendingBatchAsync();
        Assert.Equal(1, drained);

        // Verify marked Processed in DB
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
            var outbox = await db.OutboxMessages.FirstOrDefaultAsync(o => o.Payload.Contains(eventId.ToString()));
            Assert.NotNull(outbox);
            Assert.Equal("Published", outbox.Status);
            Assert.NotNull(outbox.ProcessedAt);
        }
    }

    [Fact]
    public async Task FailureDrill3_And_4_PublisherRedelivery_ConsumerIdempotentViaInbox()
    {
        // DRILL 3 & 4: Consumer receives duplicate message due to publisher or worker crash before ACK.
        // Consumer checks Inbox, updates state only on first delivery, and safely basicAcks on duplicate.
        await fixture.ResetAsync();

        var eventId = Guid.NewGuid();
        var payload = File.ReadAllText(Path.Combine("..", "..", "..", "..", "fixtures", "valid-goods-receipt-v1.json"))
            .Replace("a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d", eventId.ToString(), StringComparison.Ordinal)
            .Replace("b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e", Guid.NewGuid().ToString(), StringComparison.Ordinal);

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
            db.IngestedEvents.Add(new IngestedEvent
            {
                EventId = eventId,
                EventType = "GoodsReceiptConfirmed",
                SchemaVersion = "1.0",
                Source = "factoryflow",
                RawPayload = payload,
                Status = EventProcessingStatus.Received,
                OccurredAt = DateTimeOffset.UtcNow,
                ReceivedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDbContext<OpsBridgeDbContext>(opt => opt.UseNpgsql(fixture.ConnectionString));
        serviceCollection.AddScoped<IInboxRepository, EfInboxRepository>();
        serviceCollection.AddScoped<IConsumerProcessingRepository, EfConsumerProcessingRepository>();
        serviceCollection.AddScoped<IGoodsReceiptRepository, EfGoodsReceiptRepository>();
        serviceCollection.AddScoped<IIngestedEventRepository, EfIngestedEventRepository>();
        serviceCollection.AddScoped<OpsBridge.Application.Common.IUnitOfWork, EfUnitOfWork>();
        var sp = serviceCollection.BuildServiceProvider();

        using var consumerHost = await GoodsReceiptConsumerHost.CreateAsync(
            fixture.RabbitMqConnectionString,
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GoodsReceiptConsumerHost>.Instance);

        await using var publisher = await RabbitMqEventPublisher.CreateAsync(
            fixture.RabbitMqConnectionString,
            NullLogger<RabbitMqEventPublisher>.Instance);

        // Publish 1st message to queue
        await publisher.PublishAsync("opsbridge.events.inbound.goods-receipt", payload);
        var getResult1 = await consumerHost.Channel.BasicGetAsync(RabbitMqTopology.InboundQueue, autoAck: false);
        Assert.NotNull(getResult1);

        var deliveryArgs = new BasicDeliverEventArgs(
            consumerTag: "test-tag",
            deliveryTag: getResult1.DeliveryTag,
            redelivered: false,
            exchange: RabbitMqTopology.Exchange,
            routingKey: "opsbridge.events.inbound.goods-receipt",
            properties: getResult1.BasicProperties,
            body: getResult1.Body);

        // First delivery: processes successfully
        await consumerHost.HandleMessageAsync(deliveryArgs, CancellationToken.None);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
            var ev = await db.IngestedEvents.FirstAsync(e => e.EventId == eventId);
            Assert.Equal(EventProcessingStatus.Completed, ev.Status);
            var state = await db.ConsumerProcessingStates.FirstOrDefaultAsync(i => i.EventId == eventId && i.ConsumerGroup == "goods-receipt-consumer");
            Assert.NotNull(state);
            Assert.Equal(ConsumerStatus.Completed, state.Status);
            Assert.Equal(1, await db.GoodsReceipts.CountAsync(g => g.SourceEventId == eventId));
        }

        // Publish 2nd message (simulating at-least-once duplicate): consumer detects state entry and acks idempotently
        await publisher.PublishAsync("opsbridge.events.inbound.goods-receipt", payload);
        var getResult2 = await consumerHost.Channel.BasicGetAsync(RabbitMqTopology.InboundQueue, autoAck: false);
        Assert.NotNull(getResult2);

        var redeliveryArgs = new BasicDeliverEventArgs(
            consumerTag: "test-tag",
            deliveryTag: getResult2.DeliveryTag,
            redelivered: true,
            exchange: RabbitMqTopology.Exchange,
            routingKey: "opsbridge.events.inbound.goods-receipt",
            properties: getResult2.BasicProperties,
            body: getResult2.Body);

        await consumerHost.HandleMessageAsync(redeliveryArgs, CancellationToken.None);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
            var stateCount = await db.ConsumerProcessingStates.CountAsync(i => i.EventId == eventId && i.ConsumerGroup == "goods-receipt-consumer");
            Assert.Equal(1, stateCount);
            Assert.Equal(1, await db.GoodsReceipts.CountAsync(g => g.SourceEventId == eventId));
        }
    }

    [Fact]
    public async Task MissingReceiptData_IsDeadLettered_WithoutMarkingEventCompleted()
    {
        await fixture.ResetAsync();
        var eventId = Guid.NewGuid();
        var payload = $"{{\"eventId\":\"{eventId}\",\"eventType\":\"GoodsReceiptConfirmed\",\"data\":{{}}}}";

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
            db.IngestedEvents.Add(new IngestedEvent
            {
                EventId = eventId,
                EventType = "GoodsReceiptConfirmed",
                SchemaVersion = "1.0",
                Source = "factoryflow",
                RawPayload = payload,
                Status = EventProcessingStatus.Received,
                OccurredAt = DateTimeOffset.UtcNow,
                ReceivedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDbContext<OpsBridgeDbContext>(opt => opt.UseNpgsql(fixture.ConnectionString));
        serviceCollection.AddScoped<IConsumerProcessingRepository, EfConsumerProcessingRepository>();
        serviceCollection.AddScoped<IGoodsReceiptRepository, EfGoodsReceiptRepository>();
        serviceCollection.AddScoped<IIngestedEventRepository, EfIngestedEventRepository>();
        serviceCollection.AddScoped<OpsBridge.Application.Common.IUnitOfWork, EfUnitOfWork>();
        using var sp = serviceCollection.BuildServiceProvider();

        using var consumerHost = await GoodsReceiptConsumerHost.CreateAsync(
            fixture.RabbitMqConnectionString,
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GoodsReceiptConsumerHost>.Instance);
        await using var publisher = await RabbitMqEventPublisher.CreateAsync(
            fixture.RabbitMqConnectionString,
            NullLogger<RabbitMqEventPublisher>.Instance);
        await publisher.PublishAsync(
            "opsbridge.events.inbound.goods-receipt",
            payload,
            new Dictionary<string, object?> { ["x-retry-count"] = 3 });

        var delivery = await consumerHost.Channel.BasicGetAsync(RabbitMqTopology.InboundQueue, autoAck: false);
        Assert.NotNull(delivery);
        Assert.Contains(eventId.ToString(), Encoding.UTF8.GetString(delivery.Body.ToArray()), StringComparison.OrdinalIgnoreCase);
        await consumerHost.HandleMessageAsync(new BasicDeliverEventArgs(
            consumerTag: "test-tag",
            deliveryTag: delivery.DeliveryTag,
            redelivered: false,
            exchange: RabbitMqTopology.Exchange,
            routingKey: "opsbridge.events.inbound.goods-receipt",
            properties: delivery.BasicProperties,
            body: delivery.Body), CancellationToken.None);

        using var verifyScope = sp.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
        Assert.Equal(EventProcessingStatus.Received,
            (await verifyDb.IngestedEvents.SingleAsync(e => e.EventId == eventId)).Status);
        Assert.False(await verifyDb.ConsumerProcessingStates.AnyAsync(s => s.EventId == eventId));
        Assert.False(await verifyDb.GoodsReceipts.AnyAsync(g => g.SourceEventId == eventId));
        Assert.Contains(await verifyDb.DeadLetterEvents.ToListAsync(),
            e => e.EventId == eventId && e.Reason.Contains("missing receipt data", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Retry_IsBrokerConfirmedBeforeOriginalDeliveryIsAcked()
    {
        await fixture.ResetAsync();
        var eventId = Guid.NewGuid();
        var payload = $"{{\"eventId\":\"{eventId}\",\"eventType\":\"GoodsReceiptConfirmed\",\"error\":\"FAIL_FORCED_POISON\"}}";

        var services = new ServiceCollection();
        services.AddDbContext<OpsBridgeDbContext>(opt => opt.UseNpgsql(fixture.ConnectionString));
        services.AddScoped<IConsumerProcessingRepository, EfConsumerProcessingRepository>();
        services.AddScoped<IGoodsReceiptRepository, EfGoodsReceiptRepository>();
        services.AddScoped<IIngestedEventRepository, EfIngestedEventRepository>();
        services.AddScoped<OpsBridge.Application.Common.IUnitOfWork, EfUnitOfWork>();
        using var sp = services.BuildServiceProvider();

        using var consumerHost = await GoodsReceiptConsumerHost.CreateAsync(
            fixture.RabbitMqConnectionString,
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GoodsReceiptConsumerHost>.Instance);
        await using var publisher = await RabbitMqEventPublisher.CreateAsync(
            fixture.RabbitMqConnectionString,
            NullLogger<RabbitMqEventPublisher>.Instance);

        Assert.True(await publisher.PublishAsync("opsbridge.events.inbound.goods-receipt", payload));
        var original = await consumerHost.Channel.BasicGetAsync(RabbitMqTopology.InboundQueue, autoAck: false);
        Assert.NotNull(original);
        Assert.Contains(eventId.ToString(), Encoding.UTF8.GetString(original.Body.ToArray()), StringComparison.OrdinalIgnoreCase);

        await consumerHost.HandleMessageAsync(new BasicDeliverEventArgs(
            consumerTag: "test-tag",
            deliveryTag: original.DeliveryTag,
            redelivered: false,
            exchange: RabbitMqTopology.Exchange,
            routingKey: "opsbridge.events.inbound.goods-receipt",
            properties: original.BasicProperties,
            body: original.Body), CancellationToken.None);

        var retry = await consumerHost.Channel.BasicGetAsync(RabbitMqTopology.InboundQueue, autoAck: false);
        Assert.NotNull(retry);
        Assert.Equal(payload, Encoding.UTF8.GetString(retry.Body.ToArray()));
        Assert.NotNull(retry.BasicProperties.Headers);
        Assert.True(retry.BasicProperties.Headers.TryGetValue("x-retry-count", out var retryHeader));
        Assert.Equal(1, retryHeader switch
        {
            int i => i,
            long l => checked((int)l),
            byte[] bytes => int.Parse(Encoding.UTF8.GetString(bytes)),
            _ => throw new InvalidOperationException("Unexpected RabbitMQ retry header type")
        });
        await consumerHost.Channel.BasicAckAsync(retry.DeliveryTag, multiple: false);
    }

    [Fact]
    public async Task Retry_WhenPublishThrows_OriginalDeliveryIsNotAcked()
    {
        await fixture.ResetAsync();
        var eventId = Guid.NewGuid();
        var payload = $"{{\"eventId\":\"{eventId}\",\"eventType\":\"GoodsReceiptConfirmed\",\"error\":\"FAIL_FORCED_POISON\"}}";

        var services = new ServiceCollection();
        services.AddDbContext<OpsBridgeDbContext>(opt => opt.UseNpgsql(fixture.ConnectionString));
        services.AddScoped<IConsumerProcessingRepository, EfConsumerProcessingRepository>();
        services.AddScoped<IGoodsReceiptRepository, EfGoodsReceiptRepository>();
        services.AddScoped<IIngestedEventRepository, EfIngestedEventRepository>();
        services.AddScoped<OpsBridge.Application.Common.IUnitOfWork, EfUnitOfWork>();
        using var sp = services.BuildServiceProvider();

        using var consumerHost = await GoodsReceiptConsumerHost.CreateAsync(
            fixture.RabbitMqConnectionString,
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GoodsReceiptConsumerHost>.Instance);
        await using var publisher = await RabbitMqEventPublisher.CreateAsync(
            fixture.RabbitMqConnectionString,
            NullLogger<RabbitMqEventPublisher>.Instance);

        Assert.True(await publisher.PublishAsync("opsbridge.events.inbound.goods-receipt", payload));
        var original = await consumerHost.Channel.BasicGetAsync(RabbitMqTopology.InboundQueue, autoAck: false);
        Assert.NotNull(original);

        // Close channel before retry publish to simulate broker disconnect / publisher reject
        await consumerHost.Channel.CloseAsync();

        // HandleMessageAsync must fail on BasicPublishAsync and never invoke BasicAckAsync
        await Assert.ThrowsAnyAsync<Exception>(() => consumerHost.HandleMessageAsync(new BasicDeliverEventArgs(
            consumerTag: "test-tag",
            deliveryTag: original.DeliveryTag,
            redelivered: false,
            exchange: RabbitMqTopology.Exchange,
            routingKey: "opsbridge.events.inbound.goods-receipt",
            properties: original.BasicProperties,
            body: original.Body), CancellationToken.None));
    }

    [Fact]
    public async Task FailureDrill5_PoisonMessage_RetriesAndDeadLettersWithAudit()
    {
        // DRILL 5: Message with unrecoverable failure retries and lands in DeadLetterEvents
        await fixture.ResetAsync();

        var eventId = Guid.NewGuid();
        var poisonPayload = $"{{\"eventId\":\"{eventId}\",\"eventType\":\"GoodsReceiptConfirmed\",\"error\":\"FAIL_FORCED_POISON\"}}";

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddDbContext<OpsBridgeDbContext>(opt => opt.UseNpgsql(fixture.ConnectionString));
        serviceCollection.AddScoped<IInboxRepository, EfInboxRepository>();
        serviceCollection.AddScoped<IConsumerProcessingRepository, EfConsumerProcessingRepository>();
        serviceCollection.AddScoped<IGoodsReceiptRepository, EfGoodsReceiptRepository>();
        serviceCollection.AddScoped<IIngestedEventRepository, EfIngestedEventRepository>();
        serviceCollection.AddScoped<OpsBridge.Application.Common.IUnitOfWork, EfUnitOfWork>();
        var sp = serviceCollection.BuildServiceProvider();

        using var consumerHost = await GoodsReceiptConsumerHost.CreateAsync(
            fixture.RabbitMqConnectionString,
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GoodsReceiptConsumerHost>.Instance);

        await using var publisher = await RabbitMqEventPublisher.CreateAsync(
            fixture.RabbitMqConnectionString,
            NullLogger<RabbitMqEventPublisher>.Instance);

        // Publish poison message with max retries header to queue
        var poisonHeaders = new Dictionary<string, object?> { ["x-retry-count"] = 3 };
        await publisher.PublishAsync("opsbridge.events.inbound.goods-receipt", poisonPayload, poisonHeaders);

        var getResult = await consumerHost.Channel.BasicGetAsync(RabbitMqTopology.InboundQueue, autoAck: false);
        Assert.NotNull(getResult);

        var deliveryArgs = new BasicDeliverEventArgs(
            consumerTag: "test-tag",
            deliveryTag: getResult.DeliveryTag,
            redelivered: true,
            exchange: RabbitMqTopology.Exchange,
            routingKey: "opsbridge.events.inbound.goods-receipt",
            properties: getResult.BasicProperties,
            body: getResult.Body);

        await consumerHost.HandleMessageAsync(deliveryArgs, CancellationToken.None);

        // Verify recorded in DeadLetterEvents table in Postgres
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
        var dlqEntry = await db.DeadLetterEvents.FirstOrDefaultAsync(d => d.EventId == eventId);

        Assert.NotNull(dlqEntry);
        Assert.Contains("Simulated poison payload", dlqEntry.Reason);
        Assert.False(dlqEntry.IsReplayed);
    }

    [Fact]
    public async Task FailureDrill6_ManualReplay_RequestedTwice_HandledIdempotently()
    {
        // DRILL 6: Operator triggers manual replay on a DLQ event twice.
        await fixture.ResetAsync();
        var client = fixture.Factory.CreateClient();

        var eventId = Guid.NewGuid();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
            db.DeadLetterEvents.Add(new DeadLetterEvent
            {
                Id = Guid.NewGuid(),
                EventId = eventId,
                EventType = "GoodsReceiptConfirmed",
                Topic = "opsbridge.events.inbound.goods-receipt",
                Payload = $"{{\"eventId\":\"{eventId}\"}}",
                Reason = "Simulated business error",
                FailedAt = DateTimeOffset.UtcNow,
                IsReplayed = false
            });
            await db.SaveChangesAsync();
        }

        // 1st Replay Request (Operator JWT; backdoor header removed in F2)
        var req1 = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/replay/{eventId}");
        req1.Headers.Authorization = fixture.AuthenticatedClient.DefaultRequestHeaders.Authorization;
        var res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        var json1 = await res1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ReplayQueued", json1.GetProperty("status").GetString());

        // 2nd Replay Request (Duplicate)
        var req2 = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/replay/{eventId}");
        req2.Headers.Authorization = fixture.AuthenticatedClient.DefaultRequestHeaders.Authorization;
        var res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var json2 = await res2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AlreadyReplayed", json2.GetProperty("status").GetString());

        // Verify only 1 OutboxMessage was queued
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
            var outboxCount = await db.OutboxMessages.CountAsync(o => o.Payload.Contains(eventId.ToString()));
            Assert.Equal(1, outboxCount);
        }
    }

    [Fact]
    public async Task FailureDrill6_ConcurrentManualReplay_QueuesExactlyOnce()
    {
        await fixture.ResetAsync();

        var eventId = Guid.NewGuid();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
            db.DeadLetterEvents.Add(new DeadLetterEvent
            {
                Id = Guid.NewGuid(),
                EventId = eventId,
                EventType = "GoodsReceiptConfirmed",
                Topic = "opsbridge.events.inbound.goods-receipt",
                Payload = $"{{\"eventId\":\"{eventId}\"}}",
                Reason = "Concurrent replay test",
                FailedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var requests = Enumerable.Range(0, 12).Select(async _ =>
        {
            using var client = fixture.ClientForRole(OpsBridgeRoles.Operator);
            using var response = await client.PostAsync($"/api/v1/events/replay/{eventId}", content: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.GetProperty("status").GetString();
        });

        var statuses = await Task.WhenAll(requests);
        Assert.Single(statuses, status => status == "ReplayQueued");
        Assert.Equal(11, statuses.Count(status => status == "AlreadyReplayed"));

        using var verifyScope = fixture.Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
        Assert.Equal(1, await verifyDb.OutboxMessages.CountAsync(message => message.Payload.Contains(eventId.ToString())));
    }
}
