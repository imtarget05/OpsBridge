using FlashSale.Application.Events;
using FlashSale.Application.Outbox;
using FlashSale.Domain.Events;
using FlashSale.Domain.Outbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlashSale.UnitTests;

/// <summary>
/// Phase 10 outbox dispatch contract, driven through the REAL
/// <see cref="OutboxDispatcherUseCase"/> with recording doubles. These lock the
/// behaviour whose absence let a broker outage swallow messages silently:
/// a failed publish must be recorded as failed (never as processed) and must
/// not block the rest of the batch. Backoff/DLQ math lives in
/// <see cref="OutboxBackoffTests"/>; the SQL eligibility filter is verified
/// live against kind.
/// </summary>
public class OutboxDispatcherTests
{
    /// <summary>
    /// ADR-019: an outbox row is only publishable when it carries the
    /// canonical event envelope. A row without one is POISON.
    /// </summary>
    private static OutboxMessage Row(long id, string eventType, string? payload = null) =>
        new()
        {
            Id = id,
            MessageId = Guid.Parse($"11111111-2222-3333-4444-5555555555{id:D2}"),
            EventType = eventType,
            Payload = payload ?? new EventEnvelope
            {
                EventId = Guid.Parse($"11111111-2222-3333-4444-5555555555{id:D2}").ToString("D"),
                EventType = eventType,
                AggregateType = "order",
                AggregateId = id.ToString(),
                OccurredAt = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid(),
                Payload = "{\"orderId\":\"abc\"}"
            }.ToJson()
        };

    [Fact]
    public async Task ExecuteBatchAsync_SuccessfulPublish_MarksProcessedAndNeverFailed()
    {
        var repo = new RecordingOutboxRepository(Row(1, "order.created"));
        var publisher = new ControllablePublisher();
        var useCase = new OutboxDispatcherUseCase(repo, publisher, NullLogger<OutboxDispatcherUseCase>.Instance);

        var result = await useCase.ExecuteBatchAsync(50);

        Assert.Equal(1, result.Dispatched);
        Assert.Equal(0, result.Failed);
        Assert.Equal(new long[] { 1 }, repo.Processed);
        Assert.Empty(repo.Failed);
    }

    [Fact]
    public async Task ExecuteBatchAsync_FailedPublish_MarksFailedAndDoesNotProcess()
    {
        var repo = new RecordingOutboxRepository(Row(7, "order.created"));
        var publisher = new ControllablePublisher { FailFor = { "order.created" } };
        var useCase = new OutboxDispatcherUseCase(repo, publisher, NullLogger<OutboxDispatcherUseCase>.Instance);

        var result = await useCase.ExecuteBatchAsync(50);

        Assert.Equal(0, result.Dispatched);
        Assert.Equal(1, result.Failed);
        Assert.Empty(repo.Processed);
        Assert.Equal(new long[] { 7 }, repo.Failed);
        Assert.Contains("broker unavailable", repo.LastError);
    }

    [Fact]
    public async Task ExecuteBatchAsync_OnePoisonMessage_DoesNotBlockTheRestOfTheBatch()
    {
        // The production shape: #2 is undeliverable while #1 and #3 are fine.
        // Losing #3 because of #2 is exactly what this asserts against.
        var repo = new RecordingOutboxRepository(
            Row(1, "ok"),
            Row(2, "poison"),
            Row(3, "ok"));
        var publisher = new ControllablePublisher { FailFor = { "poison" } };
        var useCase = new OutboxDispatcherUseCase(repo, publisher, NullLogger<OutboxDispatcherUseCase>.Instance);

        var result = await useCase.ExecuteBatchAsync(50);

        Assert.Equal(2, result.Dispatched);
        Assert.Equal(new long[] { 1, 3 }, repo.Processed);
        Assert.Equal(new long[] { 2 }, repo.Failed);
    }

    [Fact]
    public async Task ExecuteBatchAsync_RowWithoutEnvelope_IsTreatedAsPoisonAndNeverPublished()
    {
        // A row whose payload is not an envelope must never reach the bus: the
        // consumer could not dedup it.
        var repo = new RecordingOutboxRepository(Row(5, "order.created", payload: "not-an-envelope"));
        var publisher = new ControllablePublisher();

        var result = await new OutboxDispatcherUseCase(repo, publisher, NullLogger<OutboxDispatcherUseCase>.Instance)
            .ExecuteBatchAsync(50);

        Assert.Equal(0, result.Dispatched);
        Assert.Equal(1, result.Failed);
        Assert.Empty(publisher.Published);
        Assert.Contains("envelope", repo.LastError);
    }

    [Fact]
    public async Task ExecuteBatchAsync_PublishesTheEnvelopeTheConsumerExpects()
    {
        // ADR-019: the dispatcher publishes the canonical envelope verbatim, so
        // the consumer always finds event_id / event_type / payload. The stable
        // event id must survive every retry.
        var message = Row(11, "order.created");
        var repo = new RecordingOutboxRepository(message);
        var publisher = new ControllablePublisher();

        await new OutboxDispatcherUseCase(repo, publisher, NullLogger<OutboxDispatcherUseCase>.Instance)
            .ExecuteBatchAsync(50);

        var published = Assert.Single(publisher.Published);
        Assert.Equal(message.MessageId.ToString("D"), published.EventId);
        Assert.Equal("order.created", published.EventType);
        Assert.Equal("outbox-dispatcher", published.Source);

        var envelope = EventEnvelope.TryParse(((ICarriesEventEnvelope)published).Envelope.ToJson());
        Assert.NotNull(envelope);
        Assert.Equal(message.MessageId.ToString("D"), envelope.EventId);
        Assert.Equal("{\"orderId\":\"abc\"}", envelope.Payload);
    }

    [Fact]
    public async Task ExecuteBatchAsync_RowWithoutEnvelope_IsTreatedAsPoisonAndDeadLettered()
    {
        // A row whose payload is not an envelope must never reach the bus: the
        // consumer could not dedup it. It burns its retries and ends up dead —
        // and the dispatcher REPORTS that, so a poison row is never silent.
        var repo = new RecordingOutboxRepository(Row(5, "order.created", payload: "not-an-envelope"))
        {
            RetryCeiling = 1
        };
        var publisher = new ControllablePublisher();

        var result = await new OutboxDispatcherUseCase(repo, publisher, NullLogger<OutboxDispatcherUseCase>.Instance)
            .ExecuteBatchAsync(50);

        Assert.Equal(0, result.Dispatched);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.DeadLettered);
        Assert.Empty(publisher.Published);
        Assert.Contains("envelope", repo.LastError);
    }

    [Fact]
    public async Task ExecuteBatchAsync_FailedPublish_ReportsTheRetrySchedule()
    {
        // The dispatcher must be able to REPORT a failure, not only log it:
        // the retry count and the next attempt are what the operator sees.
        var repo = new RecordingOutboxRepository(Row(9, "order.created"));
        var publisher = new ControllablePublisher { FailFor = { "order.created" } };

        var result = await new OutboxDispatcherUseCase(repo, publisher, NullLogger<OutboxDispatcherUseCase>.Instance)
            .ExecuteBatchAsync(50);

        Assert.Equal(1, result.Retried);
        Assert.Equal(0, result.DeadLettered);
    }

    private sealed class RecordingOutboxRepository(params OutboxMessage[] messages) : IOutboxRepository
    {
        public List<long> Processed { get; } = new();
        public List<long> Failed { get; } = new();
        public string? LastError { get; private set; }

        /// <summary>Attempts left before the row is dead-lettered (test knob).</summary>
        public int RetryCeiling { get; init; } = int.MaxValue;

        public Task EnqueueAsync(OutboxMessage message, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<OutboxMessage>> GetUnprocessedAsync(int batchSize = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OutboxMessage>>(messages);

        public Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
            Guid claimToken,
            int batchSize = 50,
            TimeSpan? leaseDuration = null,
            CancellationToken ct = default)
        {
            foreach (var message in messages)
            {
                message.ClaimToken = claimToken;
                message.ClaimedUntil = DateTimeOffset.UtcNow.Add(leaseDuration ?? TimeSpan.FromSeconds(30));
            }

            return Task.FromResult<IReadOnlyList<OutboxMessage>>(messages);
        }

        public Task MarkProcessedAsync(long id, Guid claimToken, CancellationToken ct = default)
        {
            Processed.Add(id);
            return Task.CompletedTask;
        }

        public Task<OutboxFailureOutcome> MarkFailedAsync(long id, Guid claimToken, string error, CancellationToken ct = default)
        {
            Failed.Add(id);
            LastError = error;
            var deadLettered = Failed.Count >= RetryCeiling;
            return Task.FromResult(new OutboxFailureOutcome(
                Failed.Count,
                deadLettered ? null : DateTimeOffset.UtcNow.AddSeconds(2),
                deadLettered));
        }

        public Task<IReadOnlyList<OutboxMessage>> GetStuckAsync(int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());

        public Task<int> CountStuckAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> CountPendingAsync(CancellationToken ct = default) => Task.FromResult(messages.Length);

        public Task<int> RequeueStuckAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class ControllablePublisher : IDomainEventPublisher
    {
        public HashSet<string> FailFor { get; } = new();
        public List<DomainEvent> Published { get; } = new();

        public Task PublishAsync(DomainEvent @event, CancellationToken ct = default)
            => FailFor.Contains(@event.EventType)
                ? Task.FromException(new InvalidOperationException("broker unavailable"))
                : Task.FromResult(Record(@event));

        private Task Record(DomainEvent @event)
        {
            Published.Add(@event);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Backoff/DLQ arithmetic for the transactional outbox. Locks the property whose
/// absence let a broker outage permanently swallow messages: a failed attempt
/// must schedule its next try instead of burning every retry instantly.
/// </summary>
public class OutboxBackoffTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 32)]
    public void BackoffFor_DoublesPerAttempt(int retryCount, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), OutboxMessage.BackoffFor(retryCount));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(100)]
    public void BackoffFor_IsCappedSoALongOutageDoesNotPostponeRetriesForHours(int retryCount)
    {
        Assert.Equal(OutboxMessage.MaxBackoff, OutboxMessage.BackoffFor(retryCount));
    }

    [Fact]
    public void BackoffFor_IsMonotonicAndNeverZero()
    {
        var previous = TimeSpan.Zero;
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var backoff = OutboxMessage.BackoffFor(attempt);
            Assert.True(backoff >= previous, $"attempt {attempt} went backwards: {backoff} < {previous}");
            Assert.True(backoff > TimeSpan.Zero, $"attempt {attempt} had a zero backoff");
            previous = backoff;
        }
    }

    [Fact]
    public void BackoffFor_TreatsNonPositiveAttemptAsFirstAttempt()
    {
        // Guards the retry path against a 2^0 = 1 s (or 2^negative) surprise if a
        // row is ever observed with RetryCount unset.
        Assert.Equal(TimeSpan.FromSeconds(2), OutboxMessage.BackoffFor(0));
        Assert.Equal(TimeSpan.FromSeconds(2), OutboxMessage.BackoffFor(-3));
    }

    [Fact]
    public void DeadLetterState_IsExplicitRatherThanInferredFromRetryCount()
    {
        var live = new OutboxMessage();
        Assert.False(live.IsDeadLettered);

        live.DeadLetteredAt = DateTimeOffset.UtcNow;
        Assert.True(live.IsDeadLettered);
    }
}
