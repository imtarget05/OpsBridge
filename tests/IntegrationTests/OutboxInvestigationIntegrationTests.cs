using FlashSale.Application.AgentOps;
using FlashSale.Application.Outbox;
using FlashSale.Domain.Outbox;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.IntegrationTests;

/// <summary>
/// Read-only investigation of dead-lettered outbox events (§3.1 §7).
/// The workflow reads the outbox row, runs Triage → Investigator → Critic,
/// persists only to the OutboxInvestigationAudits trail, and never mutates
/// outbox state (no requeue, no mark-processed, no mark-failed, no enqueue).
/// Requires Docker (Testcontainers PostgreSQL via FlashSaleFixture).
/// </summary>
[Collection("flashsale")]
public sealed class OutboxInvestigationIntegrationTests : IAsyncLifetime
{
    private readonly FlashSaleFixture _fx;
    private AppDbContext _db = null!;
    private OutboxInvestigationWorkflow _workflow = null!;

    public OutboxInvestigationIntegrationTests(FlashSaleFixture fx) => _fx = fx;

    public async Task InitializeAsync()
    {
        await _fx.ResetDatabaseAsync(stock: 10);
        _db = _fx.CreateDbContext();
        _workflow = new OutboxInvestigationWorkflow(
            new OutboxRepository(_db),
            new EfOutboxInvestigationAuditRepository(_db),
            new AgentOpsOptions
            {
                Enabled = true,
                StalenessMinutes = 60,
                MaxRedrafts = 1,
                SourceSha = "integration-test-sha"
            });
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<Guid> InsertOutboxRowAsync(AppDbContext db, Action<OutboxMessage>? mutate = null)
    {
        var message = new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            EventType = "order.created",
            Topic = "orders",
            AggregateType = "order",
            AggregateId = "1",
            CreatedAt = DateTimeOffset.UtcNow
        };
        mutate?.Invoke(message);
        await new OutboxRepository(db).EnqueueAsync(message);
        return message.MessageId;
    }

    /// <summary>
    /// Asserts the outbox row was NOT mutated by the workflow (read-only
    /// guarantee): DeadLetteredAt / ProcessedAt / RetryCount / Error still
    /// hold their arranged values. DeadLetteredAt is compared with tolerance
    /// because timestamptz stores microsecond precision.
    /// </summary>
    private static async Task AssertOutboxRowNotMutatedAsync(
        AppDbContext db,
        Guid messageId,
        DateTimeOffset? expectedDeadLetteredAt,
        DateTimeOffset? expectedProcessedAt,
        int expectedRetryCount,
        string? expectedError)
    {
        var row = await db.OutboxMessages.AsNoTracking().FirstAsync(m => m.MessageId == messageId);

        if (expectedDeadLetteredAt is not null)
        {
            Assert.True(row.DeadLetteredAt is not null, $"expected dead-lettered at {expectedDeadLetteredAt}, was null");
            Assert.True(
                Math.Abs((expectedDeadLetteredAt.Value - row.DeadLetteredAt!.Value).TotalMinutes) < 60,
                $"dead-lettered timestamp changed: {expectedDeadLetteredAt} -> {row.DeadLetteredAt}");
        }
        else
        {
            Assert.Null(row.DeadLetteredAt);
        }

        Assert.Equal(expectedProcessedAt, row.ProcessedAt);
        Assert.Equal(expectedRetryCount, row.RetryCount);
        Assert.Equal(expectedError, row.Error);
    }

    [Fact]
    public async Task DeadLetteredWithinStalenessWindow_ReturnsDeadLetterRecoveryRecommended_AndIsReadOnly()
    {
        // Arrange: dead-lettered 10 minutes ago, inside the 60-minute staleness window.
        var deadLetteredAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var messageId = await InsertOutboxRowAsync(_db, m =>
        {
            m.DeadLetteredAt = deadLetteredAt;
            m.RetryCount = OutboxMessage.MaxRetryCount;
            m.Error = "broker unavailable";
        });
        var outboxRowsBefore = await _db.OutboxMessages.CountAsync();

        // Act
        var result = await _workflow.InvestigateAsync(messageId, "ops-integration-test", CancellationToken.None);

        // Assert: correct verdict, read-only flag set, recovery recommended.
        Assert.Equal(InvestigationVerdict.DeadLetterRecoveryRecommended, result.Verdict);
        Assert.True(result.IsReadOnly);
        Assert.True(result.Triage.CanReceiveRecoveryRecommendation);
        Assert.False(string.IsNullOrWhiteSpace(result.Recommendation));
        Assert.Equal(messageId, result.MessageId);

        // Assert: the workflow wrote only to its own audit trail.
        var audit = await _db.OutboxInvestigationAudits
            .FirstOrDefaultAsync(a => a.MessageId == messageId);
        Assert.NotNull(audit);
        Assert.Equal(InvestigationVerdict.DeadLetterRecoveryRecommended.ToString(), audit.Verdict);
        Assert.Equal("ops-integration-test", audit.AssessedBy);

        // Assert: no outbound write calls — outbox row untouched, no new outbox rows.
        await AssertOutboxRowNotMutatedAsync(
            _db, messageId, deadLetteredAt, null, OutboxMessage.MaxRetryCount, "broker unavailable");
        Assert.Equal(outboxRowsBefore, await _db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task DeadLetteredOlderThanStalenessWindow_ReturnsStaleRequiresNewRun()
    {
        // Arrange: dead-lettered 120 minutes ago, beyond the 60-minute staleness window.
        var deadLetteredAt = DateTimeOffset.UtcNow.AddMinutes(-120);
        var messageId = await InsertOutboxRowAsync(_db, m =>
        {
            m.DeadLetteredAt = deadLetteredAt;
            m.RetryCount = OutboxMessage.MaxRetryCount;
        });

        // Act
        var result = await _workflow.InvestigateAsync(messageId, "ops-integration-test", CancellationToken.None);

        // Assert
        Assert.Equal(InvestigationVerdict.StaleRequiresNewRun, result.Verdict);
        Assert.True(result.IsReadOnly);
        Assert.False(result.Triage.CanReceiveRecoveryRecommendation);

        var audit = await _db.OutboxInvestigationAudits.FirstOrDefaultAsync(a => a.MessageId == messageId);
        Assert.NotNull(audit);
        Assert.Equal(InvestigationVerdict.StaleRequiresNewRun.ToString(), audit.Verdict);

        // Read-only: outbox row not mutated.
        await AssertOutboxRowNotMutatedAsync(_db, messageId, deadLetteredAt, null, OutboxMessage.MaxRetryCount, null);
    }

    [Fact]
    public async Task MessageNotFound_ReturnsNotFoundNoAction()
    {
        // Act: a MessageId with no matching outbox row.
        var missing = Guid.Empty;
        var result = await _workflow.InvestigateAsync(missing, "ops-integration-test", CancellationToken.None);

        // Assert
        Assert.Equal(InvestigationVerdict.NotFoundNoAction, result.Verdict);
        Assert.True(result.IsReadOnly);
        Assert.False(result.Triage.CanReceiveRecoveryRecommendation);
    }

    [Fact]
    public async Task AlreadyProcessed_ReturnsAlreadyProcessedNoAction()
    {
        // Arrange: row already processed.
        var processedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var messageId = await InsertOutboxRowAsync(_db, m =>
        {
            m.ProcessedAt = processedAt;
            m.RetryCount = 0;
        });

        // Act
        var result = await _workflow.InvestigateAsync(messageId, "ops-integration-test", CancellationToken.None);

        // Assert
        Assert.Equal(InvestigationVerdict.AlreadyProcessedNoAction, result.Verdict);
        Assert.True(result.IsReadOnly);
        Assert.False(result.Triage.CanReceiveRecoveryRecommendation);

        // Read-only: outbox row not mutated (still processed, no new rows).
        await AssertOutboxRowNotMutatedAsync(_db, messageId, null, processedAt, 0, null);
    }
}
