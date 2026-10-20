using FlashSale.Application.Events;
using FlashSale.Domain.Events;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace FlashSale.Infrastructure.Events;

/// <summary>
/// RabbitMQ publisher for domain/automation events (spec §1): topic exchange
/// "automation.events", routing key = event type, headers carry event-type and
/// correlation-id. JSON is the flat typed event (every envelope field is a
/// record ctor param), so consumers round-trip with one typed deserialize.
/// Async factory mirrors RabbitMQOrderQueue.CreateAsync — no sync-over-async.
/// </summary>
public sealed class RabbitMqDomainEventPublisher : IDomainEventPublisher, IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;
    private readonly ILogger<RabbitMqDomainEventPublisher> _logger;
    private readonly string _exchangeName;
    // Serializes publishes on this singleton channel (see PublishAsync) — same
    // contract as RabbitMQOrderQueue._publishGate.
    private readonly SemaphoreSlim _publishGate = new(1, 1);

    private RabbitMqDomainEventPublisher(IConnection connection, IChannel channel,
        ILogger<RabbitMqDomainEventPublisher> logger, string exchangeName)
    {
        _connection = connection;
        _channel = channel;
        _logger = logger;
        _exchangeName = exchangeName;
    }

    public static async Task<RabbitMqDomainEventPublisher> CreateAsync(
        string connectionString,
        ILogger<RabbitMqDomainEventPublisher> logger,
        string exchangeName = "automation.events")
    {
        var factory = new ConnectionFactory { Uri = new Uri(connectionString) };
        var connection = await factory.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(exchangeName, ExchangeType.Topic, durable: true);
        return new RabbitMqDomainEventPublisher(connection, channel, logger, exchangeName);
    }

    public async Task PublishAsync(DomainEvent domainEvent, CancellationToken ct = default)
    {
        try
        {
            // ADR-019: an event that carries the canonical envelope is
            // published AS the envelope, so every producer ends up with the
            // same wire format and the consumer always finds event_id.
            var json = domainEvent is ICarriesEventEnvelope carrier
                ? carrier.Envelope.ToJson()
                : JsonSerializer.Serialize(domainEvent, domainEvent.GetType());
            var properties = new BasicProperties
            {
                Persistent = true,
                MessageId = domainEvent.EventId,
                ContentType = "application/json",
                Headers = new Dictionary<string, object>
                {
                    ["event-type"] = domainEvent.EventType,
                    ["correlation-id"] = domainEvent.CorrelationId.ToString()
                }
            };
            // Same publish gate as RabbitMQOrderQueue: singleton channel, concurrent
            // publishers (e.g. SubmitOrderUseCase + RecordPaymentUseCase) must not
            // interleave frames on one socket write. Symptom without it: broker
            // stores the event body truncated mid-string and AutomationWorkerHost
            // dead-letters it as "Unreadable automation event".
            await _publishGate.WaitAsync(ct);
            try
            {
                await _channel.BasicPublishAsync(
                    exchange: _exchangeName,
                    routingKey: domainEvent.EventType,
                    basicProperties: properties,
                    body: Encoding.UTF8.GetBytes(json),
                    mandatory: true,
                    cancellationToken: ct);
            }
            finally
            {
                _publishGate.Release();
            }
            _logger.LogDebug("Published {EventType} {EventId} to {Exchange}.",
                domainEvent.EventType, domainEvent.EventId, _exchangeName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish {EventType} {EventId}.",
                domainEvent.EventType, domainEvent.EventId);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _channel.DisposeAsync(); } catch { /* shutdown race is non-fatal */ }
        try { await _connection.DisposeAsync(); } catch { }
    }
}