using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpsBridge.Application.Common;
using OpsBridge.Application.InboxOutbox;
using OpsBridge.Application.Ingestion;
using OpsBridge.Application.Reconciliation;
using OpsBridge.Domain.Contracts;
using OpsBridge.Domain.DLQ;
using OpsBridge.Domain.InboxOutbox;
using OpsBridge.Domain.Ingestion;
using OpsBridge.Infrastructure.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace OpsBridge.Infrastructure.Messaging;

public sealed class GoodsReceiptConsumerHost : BackgroundService
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;
    public IChannel Channel => _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GoodsReceiptConsumerHost> _logger;
    private const string ConsumerGroup = "goods-receipt-consumer";
    private const int MaxRetryAttempts = 3;

    public GoodsReceiptConsumerHost(
        IConnection connection,
        IChannel channel,
        IServiceScopeFactory scopeFactory,
        ILogger<GoodsReceiptConsumerHost> logger)
    {
        _connection = connection;
        _channel = channel;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public static async Task<GoodsReceiptConsumerHost> CreateAsync(
        string connectionString,
        IServiceScopeFactory scopeFactory,
        ILogger<GoodsReceiptConsumerHost> logger)
    {
        var factory = new ConnectionFactory { Uri = new Uri(connectionString) };
        var connection = await factory.CreateConnectionAsync();
        // The failure path republishes before acknowledging the original
        // delivery. Awaiting a broker confirm on this channel is required:
        // a successful socket write alone does not prove the retry is safe.
        var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true));

        await RabbitMqTopology.InitializeTopologyAsync(channel);
        return new GoodsReceiptConsumerHost(connection, channel, scopeFactory, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GoodsReceiptConsumerHost started — subscribing to {Queue} with Manual Ack.", RabbitMqTopology.InboundQueue);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            await HandleMessageAsync(ea, stoppingToken);
        };

        await _channel.BasicConsumeAsync(
            queue: RabbitMqTopology.InboundQueue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        // Keep service alive until shutdown
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown
        }

        _logger.LogInformation("GoodsReceiptConsumerHost stopping.");
    }

    public async Task HandleMessageAsync(BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var deliveryTag = ea.DeliveryTag;
        var rawPayload = Encoding.UTF8.GetString(ea.Body.ToArray());

        Guid eventId;
        string eventType = "GoodsReceiptConfirmed";
        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            eventId = doc.RootElement.GetProperty("eventId").GetGuid();
            if (doc.RootElement.TryGetProperty("eventType", out var et))
            {
                eventType = et.GetString() ?? eventType;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unparseable poison message. Rejecting to DLQ.");
            await RecordDeadLetterAsync(Guid.NewGuid(), "Unknown", RabbitMqTopology.InboundQueue, rawPayload, "Unparseable JSON: " + ex.Message, 0);
            await _channel.BasicRejectAsync(deliveryTag, requeue: false, ct);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var consumerRepo = scope.ServiceProvider.GetRequiredService<IConsumerProcessingRepository>();
        var eventRepo = scope.ServiceProvider.GetRequiredService<IIngestedEventRepository>();
        var grRepo = scope.ServiceProvider.GetRequiredService<IGoodsReceiptRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // 1. Consumer-side Deduplication check (Decoupled from webhook inbox)
        if (await consumerRepo.HasBeenProcessedAsync(eventId, ConsumerGroup, ct))
        {
            _logger.LogInformation("Consumer Processing: Event {EventId} already processed. Sending idempotent BasicAck.", eventId);
            await _channel.BasicAckAsync(deliveryTag, multiple: false, ct);
            return;
        }

        // 2. Business Execution in DB Transaction
        int retryCount = GetRetryCount(ea.BasicProperties?.Headers);

        try
        {
            // Simulate poison test: if payload contains "FAIL_FORCED_POISON", throw exception
            if (rawPayload.Contains("FAIL_FORCED_POISON", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Simulated poison payload execution failure.");
            }

            await using var tx = await unitOfWork.BeginTransactionAsync(ct);

            // A delivery is complete only when its domain effect is durable.
            // Let mapping/persistence failures reach the retry/DLQ branch below;
            // acknowledging them here would permanently lose the receipt.
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var contract = JsonSerializer.Deserialize<GoodsReceiptConfirmedV1>(rawPayload, options);
            if (contract?.Data == null || string.IsNullOrWhiteSpace(contract.Data.ReceiptNumber))
            {
                throw new InvalidDataException("GoodsReceiptConfirmed payload is missing receipt data or receiptNumber.");
            }

            var existingGr = await grRepo.GetByReceiptNumberAsync(contract.Data.ReceiptNumber, ct);
            if (existingGr == null)
            {
                var gr = new OpsBridge.Domain.Reconciliation.GoodsReceipt
                {
                    Id = contract.Data.ReceiptId != Guid.Empty ? contract.Data.ReceiptId : Guid.NewGuid(),
                    ReceiptNumber = contract.Data.ReceiptNumber,
                    PurchaseOrderNumber = contract.Data.PurchaseOrderNumber,
                    SupplierCode = contract.Data.SupplierCode,
                    ReceivedAt = contract.Data.ReceivedDate,
                    SourceEventId = contract.EventId,
                    SourceSystem = contract.Source,
                    Lines = contract.Data.Items.Select((item, idx) => new OpsBridge.Domain.Reconciliation.GoodsReceiptLine
                    {
                        Id = Guid.NewGuid(),
                        LineNumber = item.PurchaseOrderLineNumber > 0 ? item.PurchaseOrderLineNumber : idx + 1,
                        ItemSku = item.ItemCode,
                        ReceivedQuantity = item.QuantityReceived,
                        UnitOfMeasure = item.UnitOfMeasure,
                        AllocatedQuantity = 0m
                    }).ToList()
                };
                await grRepo.AddAsync(gr, ct);
            }

            // Update IngestedEvent status
            await eventRepo.UpdateStatusAsync(eventId, EventProcessingStatus.Completed, null, ct);

            // Record in Consumer Processing States
            await consumerRepo.RecordAsync(new ConsumerProcessingState
            {
                EventId = eventId,
                ConsumerGroup = ConsumerGroup,
                EventType = eventType,
                Status = ConsumerStatus.Completed,
                ProcessedAt = DateTimeOffset.UtcNow
            }, ct);

            await tx.CommitAsync(ct);

            // Manual ACK on success
            await _channel.BasicAckAsync(deliveryTag, multiple: false, ct);
            _logger.LogInformation("Event {EventId} successfully processed, domain model saved, and ACKed.", eventId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Processing failed for Event {EventId}. Current retry: {Retry}", eventId, retryCount);

            if (retryCount < MaxRetryAttempts)
            {
                // Exponential backoff with jitter
                var backoffMs = (int)(Math.Pow(2, retryCount) * 200 + Random.Shared.Next(50, 150));
                await Task.Delay(backoffMs, ct);

                // Republish with incremented retry header
                var newHeaders = ea.BasicProperties?.Headers != null
                    ? new Dictionary<string, object?>(ea.BasicProperties.Headers)
                    : [];
                newHeaders["x-retry-count"] = retryCount + 1;

                var props = new BasicProperties
                {
                    Persistent = true,
                    Headers = newHeaders
                };

                await _channel.BasicPublishAsync(
                    exchange: RabbitMqTopology.Exchange,
                    routingKey: ea.RoutingKey,
                    mandatory: true,
                    basicProperties: props,
                    body: ea.Body,
                    cancellationToken: ct);

                // BasicPublishAsync returns only after the broker confirms
                // this retry. If it throws, the original stays unacked and
                // can be redelivered instead of being silently lost.
                await _channel.BasicAckAsync(deliveryTag, multiple: false, ct);
            }
            else
            {
                // Max retries exceeded: Route to DLQ & record audit
                _logger.LogError("Max retries ({Max}) exceeded for Event {EventId}. Routing to Dead-Letter Queue.", MaxRetryAttempts, eventId);
                await RecordDeadLetterAsync(eventId, eventType, ea.RoutingKey, rawPayload, ex.Message, retryCount);

                // Reject to DLX (requeue = false routes message to Dead-Letter Exchange)
                await _channel.BasicRejectAsync(deliveryTag, requeue: false, ct);
            }
        }
    }

    private async Task RecordDeadLetterAsync(Guid eventId, string eventType, string topic, string payload, string reason, int retries)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
            db.DeadLetterEvents.Add(new DeadLetterEvent
            {
                EventId = eventId,
                EventType = eventType,
                Topic = topic,
                Payload = payload,
                Reason = reason,
                RetryCount = retries,
                FailedAt = DateTimeOffset.UtcNow,
                IsReplayed = false
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record DeadLetterEvent in database for Event {EventId}", eventId);
        }
    }

    private static int GetRetryCount(IDictionary<string, object?>? headers)
    {
        if (headers == null) return 0;
        if (headers.TryGetValue("x-retry-count", out var val))
        {
            if (val is int i) return i;
            if (val is long l) return (int)l;
            if (val is byte[] b && int.TryParse(Encoding.UTF8.GetString(b), out var parsed)) return parsed;
        }
        return 0;
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
