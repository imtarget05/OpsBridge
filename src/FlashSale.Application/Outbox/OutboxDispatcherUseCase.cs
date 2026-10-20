using FlashSale.Application.Events;
using FlashSale.Domain.Outbox;
using FlashSale.Domain.Events;
using Microsoft.Extensions.Logging;

namespace FlashSale.Application.Outbox;

/// <summary>
/// What one dispatcher batch achieved (ADR-019). The caller (the hosted
/// service) turns this into metrics — a failure or a dead-lettered row must
/// never be invisible.
/// </summary>
/// <param name="Dispatched">Messages published and marked processed.</param>
/// <param name="Failed">Publish attempts that failed.</param>
/// <param name="Retried">Failures that will be retried after a backoff.</param>
/// <param name="DeadLettered">Failures that exhausted the retry ceiling.</param>
public sealed record OutboxDispatchResult(
    int Dispatched,
    int Failed,
    int Retried,
    int DeadLettered);

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
    public async Task<OutboxDispatchResult> ExecuteBatchAsync(int batchSize = 50, CancellationToken ct = default)
    {
        var claimToken = Guid.NewGuid();
        var messages = await outboxRepository.ClaimBatchAsync(claimToken, batchSize, ct: ct);
        if (messages.Count == 0)
        {
            return new OutboxDispatchResult(0, 0, 0, 0);
        }

        var dispatched = 0;
        var failed = 0;
        var retried = 0;
        var deadLettered = 0;

        foreach (var msg in messages)
        {
            try
            {
                // A row without a valid envelope is POISON: publishing it
                // would put a message on the bus that no consumer can dedup.
                // It is treated as a failed attempt, so it burns its retries
                // and ends up in the dead-letter state where an operator can
                // see it.
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
                failed++;
                var outcome = await outboxRepository.MarkFailedAsync(msg.Id, claimToken, ex.Message, ct);
                if (outcome.DeadLettered)
                {
                    deadLettered++;
                    logger.LogError(
                        ex,
                        "Outbox message {Id} ({Type}) exhausted its retries and was DEAD-LETTERED " +
                        "after {RetryCount} attempts. Inspect GET /api/outbox/stuck; recover with POST /api/outbox/requeue.",
                        msg.Id, msg.EventType, outcome.RetryCount);
                }
                else
                {
                    retried++;
                    logger.LogWarning(
                        ex,
                        "Failed to dispatch outbox message {Id} ({Type}); retry {RetryCount} scheduled for {NextAttemptAt}.",
                        msg.Id, msg.EventType, outcome.RetryCount, outcome.NextAttemptAt);
                }
            }
        }

        return new OutboxDispatchResult(dispatched, failed, retried, deadLettered);
    }
}
