using FlashSale.Application.Outbox;
using FlashSale.Application.Persistence;
using FlashSale.Domain.Automation;
using FlashSale.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace FlashSale.Infrastructure.Events;

/// <summary>What happened to one delivered automation event.</summary>
public enum AutomationEventOutcome
{
    /// <summary>Audited and applied.</summary>
    Processed,

    /// <summary>Already applied by this consumer — a redelivery, safely ignored.</summary>
    Duplicate,

    /// <summary>Not a recognised event document; the caller must dead-letter it.</summary>
    Unreadable
}

/// <summary>
/// Transport-independent automation event handling (Phase 10/V2.2): decode,
/// deduplicate through the Inbox, wrap in an <see cref="AutomationRun"/> audit
/// record (Running → Success/Failed) and dispatch the workflow.
///
/// Why this exists as a separate type: the original logic lived inside the
/// RabbitMQ host, so the Kafka path could not reuse it and a Kafka cutover
/// would have published events that nobody processed. Both consumers now share
/// exactly one implementation, including the delivery-header trap below.
/// </summary>
public sealed class AutomationEventProcessor(
    IServiceScopeFactory scopeFactory,
    ILogger<AutomationEventProcessor> logger)
{
    /// <summary>Consumer group recorded in the Inbox table.</summary>
    public const string ConsumerName = "automation-worker";

    public async Task<AutomationEventOutcome> ProcessAsync(
        byte[] body, string? eventTypeHeader, CancellationToken ct = default)
    {
        var json = Encoding.UTF8.GetString(body);

        // ADR-019: the canonical envelope carries event_type, event_id and the
        // typed event as its payload. Legacy flat events are still accepted.
        var envelope = EventEnvelope.TryParse(json);
        var eventType = !string.IsNullOrWhiteSpace(eventTypeHeader)
            ? eventTypeHeader
            : envelope?.EventType ?? EventTypeFrom(json);

        var evt = DeserializeSafe(envelope?.Payload ?? json, eventType);
        if (evt is null)
        {
            // Poison message: malformed body, or an event type this build does not
            // know. Never throw — one bad message must not stop the host, and an
            // unacked RabbitMQ delivery would be requeued forever (observed as a
            // container restart loop on the SAME message).
            logger.LogWarning(
                "Unreadable automation event (type={EventType}) — dead-lettering.", eventType);
            return AutomationEventOutcome.Unreadable;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<IAutomationRunRepository>();
        var inbox = scope.ServiceProvider.GetRequiredService<IInboxRepository>();

        // At-least-once transports redeliver; without this the same event would
        // create a second audit run and re-apply its side effects.
        //
        // ADR-019: the dedup key is the STABLE event_id of the envelope (the
        // same id for every retry of the same outbox row), and the claim is
        // durable — a unique-constraint insert in the inbox table, taken
        // BEFORE the side effects. A redelivery therefore cannot apply them
        // twice.
        var dedupeKey = TryParseMessageId(envelope?.EventId ?? evt.EventId);
        if (dedupeKey is { } messageId
            && !await inbox.TryClaimAsync(messageId, ConsumerName, ct))
        {
            logger.LogDebug(
                "Skipping duplicate {EventType} {EventId} for consumer {Consumer}.",
                evt.EventType, evt.EventId, ConsumerName);
            return AutomationEventOutcome.Duplicate;
        }

        var run = new AutomationRun
        {
            WorkflowName = WorkflowFor(evt),
            TriggerType = "domain.event",
            TriggerId = evt.EventId,
            Status = AutomationRunStatus.Running,
            StartedAt = DateTimeOffset.UtcNow,
            CorrelationId = evt.CorrelationId
        };
        await runs.CreateAsync(run, ct);

        try
        {
            await ProcessAsync(evt, ct);
            run.Status = AutomationRunStatus.Success;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ResultSummary = $"Processed {evt.EventType}.";
        }
        catch (Exception ex)
        {
            run.Status = AutomationRunStatus.Failed;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ErrorCode = ex.GetType().Name;
            run.ErrorMessage = ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000];
            logger.LogError(ex, "Automation run {RunId} failed for {EventType}.", run.Id, evt.EventType);
        }

        await runs.UpdateAsync(run, ct);

        // The claim is turned into a processed record once the audit row is
        // persisted: a redelivery then sees a completed inbox entry and skips
        // the event instead of applying its side effects a second time.
        if (dedupeKey is { } key)
        {
            await inbox.MarkProcessedAsync(key, ConsumerName, ct);
        }

        return AutomationEventOutcome.Processed;
    }

    /// <summary>
    /// Event ids are strings; the Inbox keys on Guid. A non-Guid id (or a missing
    /// one) simply skips deduplication rather than failing the delivery.
    /// </summary>
    private static Guid? TryParseMessageId(string? eventId)
        => Guid.TryParse(eventId, out var id) ? id : null;

    /// <summary>Event type from the JSON document, when no delivery header carries it.</summary>
    public static string? EventTypeFrom(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json).GetProperty("EventType").GetString();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Header value as text, tolerating the byte[] that AMQP longstr and Kafka
    /// headers decode to. byte[].ToString() is "System.Byte[]" (non-null!), so a
    /// naive reader silently mis-typed every event as unknown.
    /// </summary>
    public static string? ReadStringHeader(object? raw) => raw switch
    {
        null => null,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        string s => s,
        _ => raw.ToString()
    };

    private Task ProcessAsync(DomainEvent evt, CancellationToken ct)
    {
        // Workflow-specific side effects land in later phases; audit-first means
        // every known event type is persisted + logged even before they exist.
        logger.LogInformation(
            "Automation {Workflow}: handled {EventType} {EventId} (correlation {CorrelationId}).",
            WorkflowFor(evt), evt.EventType, evt.EventId, evt.CorrelationId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deserialize without ever letting a System.Text.Json parse error escape
    /// into the host — a malformed payload must dead-letter, not stop it.
    /// </summary>
    private static DomainEvent? DeserializeSafe(string json, string? eventType)
    {
        try
        {
            return Deserialize(json, eventType);
        }
        catch (JsonException)
        {
            // Intentionally no throw: caller logs eventType and dead-letters.
            return null;
        }
    }

    public static DomainEvent? Deserialize(string json, string? eventType) => eventType switch
    {
        "order.created" => JsonSerializer.Deserialize<OrderCreatedEvent>(json),
        "inventory.reserved" => JsonSerializer.Deserialize<InventoryReservedEvent>(json),
        "payment.completed" => JsonSerializer.Deserialize<PaymentCompletedEvent>(json),
        "payment.failed" => JsonSerializer.Deserialize<PaymentFailedEvent>(json),
        "payment.expired" => JsonSerializer.Deserialize<PaymentExpiredEvent>(json),
        "order.confirmed" => JsonSerializer.Deserialize<OrderConfirmedEvent>(json),
        "order.cancelled" => JsonSerializer.Deserialize<OrderCancelledEvent>(json),
        "inventory.released" => JsonSerializer.Deserialize<InventoryReleasedEvent>(json),
        "inventory.low_stock" => JsonSerializer.Deserialize<InventoryLowStockEvent>(json),
        // ADR-019: the saga events are now part of the event stream (they are
        // written to the outbox together with the saga state). They used to
        // arrive without a handler and were dead-lettered as unreadable.
        "checkout.saga.started" => JsonSerializer.Deserialize<CheckoutSagaStartedEvent>(json),
        "checkout.saga.completed" => JsonSerializer.Deserialize<CheckoutSagaCompletedEvent>(json),
        "checkout.saga.compensated" => JsonSerializer.Deserialize<CheckoutSagaCompensatedEvent>(json),
        _ => null
    };

    public static string WorkflowFor(DomainEvent evt) => evt switch
    {
        OrderCreatedEvent => AutomationWorkflow.OrderProcessing.ToString(),
        PaymentCompletedEvent or PaymentFailedEvent or PaymentExpiredEvent
            => AutomationWorkflow.PaymentTimeout.ToString(),
        OrderCancelledEvent or InventoryReleasedEvent or InventoryReservedEvent or InventoryLowStockEvent
            => AutomationWorkflow.InventoryAutomation.ToString(),
        OrderConfirmedEvent => AutomationWorkflow.OrderProcessing.ToString(),
        CheckoutSagaStartedEvent or CheckoutSagaCompletedEvent or CheckoutSagaCompensatedEvent
            => AutomationWorkflow.OrderProcessing.ToString(),
        _ => "unknown"
    };
}