using FlashSale.Application.Outbox;
using FlashSale.Domain.Automation;
using FlashSale.Domain.Events;
using FlashSale.Infrastructure.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.Json;

namespace FlashSale.UnitTests;

/// <summary>
/// V2.2 automation event processing, transport-independent. Locks the behaviours
/// that a Kafka cutover depends on: the shared processor must deduplicate
/// redeliveries through the Inbox, must read the delivery header as the byte[]
/// that both AMQP and Kafka produce, and must dead-letter rather than throw.
/// </summary>
public class AutomationEventProcessorTests
{
    private static byte[] Body(DomainEvent evt) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt, evt.GetType()));

    private static OrderCancelledEvent SampleEvent(string? eventId = null) =>
        new(eventId ?? Guid.NewGuid().ToString("D"),
            Guid.NewGuid(),
            "test",
            DateTimeOffset.UtcNow,
            OrderId: 42,
            Reason: "unit-test");

    private (AutomationEventProcessor Processor, FakeRuns Runs, FakeInbox Inbox) Create()
    {
        var runs = new FakeRuns();
        var inbox = new FakeInbox();
        var processor = new AutomationEventProcessor(
            new FakeScopeFactory(runs, inbox),
            NullLogger<AutomationEventProcessor>.Instance);
        return (processor, runs, inbox);
    }

    [Fact]
    public async Task ProcessAsync_KnownEvent_AuditsARunAndRecordsInbox()
    {
        var (processor, runs, inbox) = Create();
        var evt = SampleEvent();

        var outcome = await processor.ProcessAsync(Body(evt), evt.EventType);

        Assert.Equal(AutomationEventOutcome.Processed, outcome);
        var run = Assert.Single(runs.Runs);
        Assert.Equal(AutomationRunStatus.Success, run.Status);
        Assert.Equal(AutomationWorkflow.InventoryAutomation.ToString(), run.WorkflowName);
        Assert.Equal($"Processed {evt.EventType}.", run.ResultSummary);
        Assert.Contains((Guid.Parse(evt.EventId), AutomationEventProcessor.ConsumerName), inbox.Seen);
    }

    [Fact]
    public async Task ProcessAsync_RedeliveredEvent_IsDeduplicatedAndAuditedOnce()
    {
        var (processor, runs, _) = Create();
        var evt = SampleEvent();

        var first = await processor.ProcessAsync(Body(evt), evt.EventType);
        var second = await processor.ProcessAsync(Body(evt), evt.EventType);

        Assert.Equal(AutomationEventOutcome.Processed, first);
        Assert.Equal(AutomationEventOutcome.Duplicate, second);
        Assert.Single(runs.Runs); // the whole point: one transition, not two
    }

    [Fact]
    public async Task ProcessAsync_ReplayFiveTimes_AppliesExactlyOnce()
    {
        // The V2.2 acceptance criterion, at unit level: replay 5x -> 1 transition.
        var (processor, runs, _) = Create();
        var evt = SampleEvent();

        var outcomes = new List<AutomationEventOutcome>();
        for (var i = 0; i < 5; i++)
        {
            outcomes.Add(await processor.ProcessAsync(Body(evt), evt.EventType));
        }

        Assert.Equal(1, outcomes.Count(o => o == AutomationEventOutcome.Processed));
        Assert.Equal(4, outcomes.Count(o => o == AutomationEventOutcome.Duplicate));
        Assert.Single(runs.Runs);
    }

    [Fact]
    public async Task ProcessAsync_EventIdIsNotAGuid_SkipsDedupInsteadOfFailing()
    {
        var (processor, runs, _) = Create();
        var evt = SampleEvent(eventId: "not-a-guid");

        var outcome = await processor.ProcessAsync(Body(evt), evt.EventType);

        Assert.Equal(AutomationEventOutcome.Processed, outcome);
        Assert.Single(runs.Runs);
    }

    [Fact]
    public async Task ProcessAsync_ByteArrayHeader_IsDecodedNotStringified()
    {
        // Both transports deliver the header as byte[]; byte[].ToString() yields
        // "System.Byte[]" (non-null), which used to mis-type every event.
        var (processor, runs, _) = Create();
        var evt = SampleEvent();
        var header = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(evt.EventType));

        var asBytes = AutomationEventProcessor.ReadStringHeader(Encoding.UTF8.GetBytes(header));

        Assert.Equal(evt.EventType, asBytes);
        Assert.Equal(AutomationEventOutcome.Processed, await processor.ProcessAsync(Body(evt), asBytes));
        Assert.Single(runs.Runs);
    }

    [Fact]
    public async Task ProcessAsync_NoHeader_FallsBackToTheJsonEventType()
    {
        var (processor, runs, _) = Create();

        var outcome = await processor.ProcessAsync(Body(SampleEvent()), eventTypeHeader: null);

        Assert.Equal(AutomationEventOutcome.Processed, outcome);
        Assert.Single(runs.Runs);
    }

    [Fact]
    public async Task ProcessAsync_MalformedBody_IsUnreadableAndNeverThrows()
    {
        var (processor, runs, inbox) = Create();

        var outcome = await processor.ProcessAsync(Encoding.UTF8.GetBytes("{ this is not json"), "order.created");

        Assert.Equal(AutomationEventOutcome.Unreadable, outcome);
        Assert.Empty(runs.Runs);
        Assert.Empty(inbox.Seen);
    }

    [Fact]
    public async Task ProcessAsync_UnknownEventType_IsUnreadable()
    {
        var (processor, runs, _) = Create();
        var evt = SampleEvent();

        var outcome = await processor.ProcessAsync(Body(evt), "something.unknown");

        Assert.Equal(AutomationEventOutcome.Unreadable, outcome);
        Assert.Empty(runs.Runs);
    }

    [Theory]
    [InlineData("Kafka")]
    [InlineData("RabbitMQ")]
    public void ResolveProvider_EventsProviderWinsOverTheOrderQueueProvider(string eventsProvider)
    {
        // The decoupling that makes a Kafka cutover possible at all: the order
        // queue stays RabbitMQ (MessagingProviderSelector has no Kafka arm) while
        // the event backbone moves to Kafka.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Events:Provider"] = eventsProvider,
            ["Messaging:Provider"] = "RabbitMQ"
        }).Build();

        Assert.Equal(eventsProvider, DomainEventServiceCollectionExtensions.ResolveProvider(configuration));
    }

    [Fact]
    public void ResolveProvider_FallsBackToMessagingProviderForUnchangedDeployments()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:Provider"] = "RabbitMQ"
        }).Build();

        Assert.Equal("RabbitMQ", DomainEventServiceCollectionExtensions.ResolveProvider(configuration));
    }

    [Fact]
    public void ResolveKafkaTopic_DefaultMatchesTheProducerTopic()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        // Producer and consumer MUST agree, or a cutover publishes into a topic
        // nobody reads while every health check still passes.
        Assert.Equal("orders.events", DomainEventServiceCollectionExtensions.ResolveKafkaTopic(configuration));
    }

    private sealed class FakeRuns : IAutomationRunRepository
    {
        public List<AutomationRun> Runs { get; } = new();

        public Task<AutomationRun> CreateAsync(AutomationRun run, CancellationToken ct = default)
        {
            run.Id = Runs.Count + 1;
            Runs.Add(run);
            return Task.FromResult(run);
        }

        public Task UpdateAsync(AutomationRun run, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AutomationRun?> GetByIdAsync(int id, CancellationToken ct = default)
            => Task.FromResult(Runs.FirstOrDefault(r => r.Id == id));

        public Task<IReadOnlyList<AutomationRun>> GetRecentAsync(int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AutomationRun>>(Runs.Take(count).ToList());

        public Task<AutomationSummary> GetSummaryAsync(CancellationToken ct = default)
            => Task.FromResult(new AutomationSummary(Runs.Count, Runs.Count, 0, 0, 0, 0, null));
    }

    private sealed class FakeInbox : IInboxRepository
    {
        public HashSet<(Guid MessageId, string Consumer)> Seen { get; } = new();
        private readonly HashSet<(Guid MessageId, string Consumer)> _claimed = new();

        public Task<bool> HasBeenProcessedAsync(Guid messageId, string consumerName, CancellationToken ct = default)
            => Task.FromResult(Seen.Contains((messageId, consumerName)));

        /// <summary>
        /// ADR-019: the claim is taken BEFORE the side effects, so a
        /// redelivery cannot apply them twice.
        /// </summary>
        public Task<bool> TryClaimAsync(Guid messageId, string consumerName, CancellationToken ct = default)
        {
            var key = (messageId, consumerName);
            if (Seen.Contains(key) || _claimed.Contains(key))
            {
                return Task.FromResult(false);
            }

            _claimed.Add(key);
            return Task.FromResult(true);
        }

        public Task MarkProcessedAsync(Guid messageId, string consumerName, CancellationToken ct = default)
        {
            _claimed.Remove((messageId, consumerName));
            Seen.Add((messageId, consumerName));
            return Task.CompletedTask;
        }
    }

    /// <summary>Real DI container: the processor resolves its repositories per scope.</summary>
    private sealed class FakeScopeFactory : IServiceScopeFactory
    {
        private readonly ServiceProvider _provider;

        public FakeScopeFactory(IAutomationRunRepository runs, IInboxRepository inbox)
        {
            var services = new ServiceCollection();
            services.AddSingleton(runs);
            services.AddSingleton(inbox);
            _provider = services.BuildServiceProvider();
        }

        public IServiceScope CreateScope() => _provider.CreateScope();
    }
}