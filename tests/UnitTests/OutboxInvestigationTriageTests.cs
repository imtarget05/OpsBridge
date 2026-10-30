using FlashSale.Application.AgentOps;

namespace FlashSale.UnitTests;

public class OutboxInvestigationTriageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly OutboxInvestigationTriage _triage = new();

    [Fact]
    public void Classify_DeadLetteredWithinStalenessWindow_ReturnsDeadLetterRecoveryRecommended()
    {
        // Arrange: dead-lettered 60 minutes ago, staleness window is 1440 minutes
        var evidence = new InvestigationEvidence
        {
            MessageId = Guid.Parse("11111111-2222-3333-4444-555555555501"),
            EventType = "order.created",
            Topic = "orders",
            DeadLetteredAt = Now.AddMinutes(-60),
        };

        // Act
        var result = _triage.Classify(evidence, stalenessMinutes: 1440, now: Now);

        // Assert
        Assert.Equal(InvestigationVerdict.DeadLetterRecoveryRecommended, result.Classification);
        Assert.True(result.CanReceiveRecoveryRecommendation);
        Assert.False(string.IsNullOrWhiteSpace(result.Reasoning));
    }

    [Fact]
    public void Classify_NotProcessedWithFutureNextAttempt_ReturnsBackingOffNoAction()
    {
        // Arrange: not processed, not dead-lettered, next attempt in the future
        var evidence = new InvestigationEvidence
        {
            MessageId = Guid.Parse("11111111-2222-3333-4444-555555555502"),
            EventType = "order.created",
            Topic = "orders",
            NextAttemptAt = Now.AddMinutes(30),
        };

        // Act
        var result = _triage.Classify(evidence, stalenessMinutes: 1440, now: Now);

        // Assert
        Assert.Equal(InvestigationVerdict.BackingOffNoAction, result.Classification);
        Assert.False(result.CanReceiveRecoveryRecommendation);
        Assert.False(string.IsNullOrWhiteSpace(result.Reasoning));
    }

    [Fact]
    public void Classify_AlreadyProcessed_ReturnsAlreadyProcessedNoAction()
    {
        // Arrange: row was already processed
        var evidence = new InvestigationEvidence
        {
            MessageId = Guid.Parse("11111111-2222-3333-4444-555555555503"),
            EventType = "order.created",
            Topic = "orders",
            ProcessedAt = Now.AddMinutes(-30),
        };

        // Act
        var result = _triage.Classify(evidence, stalenessMinutes: 1440, now: Now);

        // Assert
        Assert.Equal(InvestigationVerdict.AlreadyProcessedNoAction, result.Classification);
        Assert.False(result.CanReceiveRecoveryRecommendation);
        Assert.False(string.IsNullOrWhiteSpace(result.Reasoning));
    }

    [Fact]
    public void Classify_EmptyMessageId_ReturnsNotFoundNoAction()
    {
        // Arrange: no outbox row exists for the requested MessageId
        var evidence = new InvestigationEvidence
        {
            MessageId = Guid.Empty,
            EventType = "order.created",
            Topic = "orders",
        };

        // Act
        var result = _triage.Classify(evidence, stalenessMinutes: 1440, now: Now);

        // Assert
        Assert.Equal(InvestigationVerdict.NotFoundNoAction, result.Classification);
        Assert.False(result.CanReceiveRecoveryRecommendation);
        Assert.False(string.IsNullOrWhiteSpace(result.Reasoning));
    }

    [Fact]
    public void Classify_DeadLetteredOlderThanStalenessWindow_ReturnsStaleRequiresNewRun()
    {
        // Arrange: dead-lettered 60 minutes ago, staleness window is 30 minutes
        var evidence = new InvestigationEvidence
        {
            MessageId = Guid.Parse("11111111-2222-3333-4444-555555555505"),
            EventType = "order.created",
            Topic = "orders",
            DeadLetteredAt = Now.AddMinutes(-60),
        };

        // Act
        var result = _triage.Classify(evidence, stalenessMinutes: 30, now: Now);

        // Assert
        Assert.Equal(InvestigationVerdict.StaleRequiresNewRun, result.Classification);
        Assert.False(result.CanReceiveRecoveryRecommendation);
        Assert.False(string.IsNullOrWhiteSpace(result.Reasoning));
    }

    [Fact]
    public void Classify_MissingEventType_ReturnsInsufficientEvidenceAbstain()
    {
        // Arrange: row exists but lacks EventType metadata
        var evidence = new InvestigationEvidence
        {
            MessageId = Guid.Parse("11111111-2222-3333-4444-555555555506"),
            EventType = string.Empty,
            Topic = "orders",
        };

        // Act
        var result = _triage.Classify(evidence, stalenessMinutes: 1440, now: Now);

        // Assert
        Assert.Equal(InvestigationVerdict.InsufficientEvidenceAbstain, result.Classification);
        Assert.False(result.CanReceiveRecoveryRecommendation);
        Assert.False(string.IsNullOrWhiteSpace(result.Reasoning));
    }

    [Fact]
    public void Classify_CanReceiveRecoveryRecommendation_TrueOnlyForDeadLetterRecovery()
    {
        // Act / Assert: only the DEAD_LETTERED-within-window path may recommend recovery
        foreach (var verdict in Enum.GetValues<InvestigationVerdict>())
        {
            var evidence = VerdictEvidence(verdict);
            var result = _triage.Classify(evidence, stalenessMinutes: 1440, now: Now);

            Assert.Equal(verdict, result.Classification);
            Assert.Equal(verdict == InvestigationVerdict.DeadLetterRecoveryRecommended, result.CanReceiveRecoveryRecommendation);
            Assert.False(string.IsNullOrWhiteSpace(result.Reasoning), $"{verdict} returned empty reasoning");
        }
    }

    private static InvestigationEvidence VerdictEvidence(InvestigationVerdict verdict)
    {
        return verdict switch
        {
            InvestigationVerdict.DeadLetterRecoveryRecommended => new()
            {
                MessageId = Guid.NewGuid(),
                EventType = "order.created",
                Topic = "orders",
                DeadLetteredAt = Now.AddMinutes(-10),
            },
            InvestigationVerdict.BackingOffNoAction => new()
            {
                MessageId = Guid.NewGuid(),
                EventType = "order.created",
                Topic = "orders",
                NextAttemptAt = Now.AddMinutes(30),
            },
            InvestigationVerdict.AlreadyProcessedNoAction => new()
            {
                MessageId = Guid.NewGuid(),
                EventType = "order.created",
                Topic = "orders",
                ProcessedAt = Now.AddMinutes(-10),
            },
            InvestigationVerdict.NotFoundNoAction => new()
            {
                MessageId = Guid.Empty,
                EventType = "order.created",
                Topic = "orders",
            },
            InvestigationVerdict.StaleRequiresNewRun => new()
            {
                MessageId = Guid.NewGuid(),
                EventType = "order.created",
                Topic = "orders",
                DeadLetteredAt = Now.AddMinutes(-2000),
            },
            InvestigationVerdict.InsufficientEvidenceAbstain => new()
            {
                MessageId = Guid.NewGuid(),
                EventType = string.Empty,
                Topic = "orders",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null)
        };
    }
}
