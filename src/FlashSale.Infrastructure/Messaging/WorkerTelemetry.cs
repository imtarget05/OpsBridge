using System.Diagnostics.Metrics;

namespace FlashSale.Infrastructure.Messaging;

public static class WorkerTelemetry
{
    public const string MeterName = "FlashSale.Worker";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static long _outboxStuckMessages;
    private static long _outboxPendingMessages;

    public static readonly Counter<long> QueueMessagesProcessed = Meter.CreateCounter<long>(
        "flashsale.worker.messages.processed",
        unit: "{message}",
        description: "Queue messages processed or dead-lettered by the worker.");

    public static readonly Counter<long> QueueMessageRetries = Meter.CreateCounter<long>(
        "flashsale.worker.messages.retried",
        unit: "{message}",
        description: "Queue messages re-enqueued after a transient processing failure.");

    public static readonly Counter<long> OutboxMessagesDispatched = Meter.CreateCounter<long>(
        "flashsale.worker.outbox.dispatched",
        unit: "{event}",
        description: "Outbox messages successfully dispatched.");

    public static readonly ObservableGauge<long> OutboxStuckMessages = Meter.CreateObservableGauge(
        "flashsale.worker.outbox.stuck",
        () => Interlocked.Read(ref _outboxStuckMessages),
        unit: "{event}",
        description: "Outbox messages dead-lettered or awaiting retry.");

    /// <summary>Outbox lag: rows written but not yet published (ADR-019).</summary>
    public static readonly ObservableGauge<long> OutboxPendingMessages = Meter.CreateObservableGauge(
        "flashsale.worker.outbox.pending",
        () => Interlocked.Read(ref _outboxPendingMessages),
        unit: "{event}",
        description: "Outbox messages written but not yet published (outbox lag).");

    /// <summary>Publish attempts that failed (ADR-019).</summary>
    public static readonly Counter<long> OutboxPublishFailures = Meter.CreateCounter<long>(
        "flashsale.worker.outbox.publish_failures",
        unit: "{event}",
        description: "Outbox publish attempts that failed and were scheduled for retry.");

    /// <summary>Failed attempts that will be retried after a backoff (ADR-019).</summary>
    public static readonly Counter<long> OutboxRetries = Meter.CreateCounter<long>(
        "flashsale.worker.outbox.retried",
        unit: "{event}",
        description: "Outbox messages scheduled for a retry after a failed publish.");

    /// <summary>Messages that exhausted the retry ceiling (ADR-019).</summary>
    public static readonly Counter<long> OutboxDeadLettered = Meter.CreateCounter<long>(
        "flashsale.worker.outbox.dead_lettered",
        unit: "{event}",
        description: "Outbox messages dead-lettered after exhausting their retries.");

    public static void SetOutboxStuckMessages(long count) =>
        Interlocked.Exchange(ref _outboxStuckMessages, count);

    public static void SetOutboxPendingMessages(long count) =>
        Interlocked.Exchange(ref _outboxPendingMessages, count);
}