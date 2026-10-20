using FlashSale.Application.Events;
using FlashSale.Domain.Events;
using FlashSale.Domain.Outbox;
using Microsoft.Extensions.Logging;

namespace FlashSale.Application.Outbox;

/// <summary>
/// Dispatches pending Outbox messages to the event backbone (Kafka / RabbitMQ).
/// Marks each message processed only after the transport acknowledges receipt.
///
/// <para>
/// ADR-019: the payload is the canonical event envelope, published verbatim so
/// a retry republishes byte-identical content — including the stable
/// <c>event_id</c> the consumer dedups on.
/// </para>
/// </summary>
public sealed class OutboxDispatcherUseCase(
    IOutboxRepository outboxRepository,
    IDomainEventPublisher publisher,
    ILogger<OutboxDispatcherUseCase> logger)
{
    public async Task<int> ExecuteBatchAsync(int batchSize = 50, CancellationToken ct = default)
    {
        var claimToken = Guid.NewGuid();
        var messages = await outboxRepository.ClaimBatchAsync(claimToken, batchSize, ct: ct);
        if (messages.Count == 0)
        {
            return 0;
        }

        var dispatched = 0;
        foreach (var msg in messages)
        {
            try
            {
                // A row without a valid envelope is POISON: publishing it would
                // put a message on the bus that no consumer can dedup. It is
                // treated as a failed attempt, so it burns its retries and ends
                // up in the dead-letter state where an operator can see it.
                var envelope = EventEnvelope.TryParse(msg.Payload)
                    ?? throw new InvalidOperationException(
                        $"Outbox row {msg.Id} ({msg.EventType}) does not carry a valid event envelope.");

                await publisher.PublishAsync(new OutboxEnvelopeEvent(envelope), ct);

                // Processed is marked ONLY after the transport accepted the
                // message. A crash in between means the row is published again
                // — at-least-once — and the consumer dedups on event_id.
                await outboxRepository.MarkProcessedAsync(msg.Id, claimToken, ct);
                dispatched++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to dispatch outbox message {Id} ({Type}).", msg.Id, msg.EventType);
                await outboxRepository.MarkFailedAsync(msg.Id, claimToken, ex.Message, ct);
            }
        }

        return dispatched;
    }
}
