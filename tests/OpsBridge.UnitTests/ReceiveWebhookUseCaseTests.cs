using Microsoft.Extensions.Logging.Abstractions;
using OpsBridge.Application.Common;
using OpsBridge.Application.Connectors;
using OpsBridge.Application.InboxOutbox;
using OpsBridge.Application.Ingestion;
using OpsBridge.Application.UseCases;
using OpsBridge.Domain.Connectors;
using OpsBridge.Domain.Exceptions;
using OpsBridge.Domain.InboxOutbox;
using OpsBridge.Domain.Ingestion;
using OpsBridge.Infrastructure.Contracts;
using OpsBridge.Infrastructure.Security;

namespace OpsBridge.UnitTests;

public class ReceiveWebhookUseCaseTests
{
    private readonly FakeConnectorRepository _connectorRepo = new();
    private readonly FakeInboxRepository _inboxRepo = new();
    private readonly FakeIngestedEventRepository _eventRepo = new();
    private readonly FakeOutboxRepository _outboxRepo = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly HmacWebhookVerifier _verifier = new();
    private readonly JsonContractValidator _contractValidator = new();
    private readonly ReceiveWebhookUseCase _useCase;

    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw=";
    private readonly string _validPayload;

    public ReceiveWebhookUseCaseTests()
    {
        _useCase = new ReceiveWebhookUseCase(
            _connectorRepo,
            _verifier,
            _contractValidator,
            _inboxRepo,
            _eventRepo,
            _outboxRepo,
            _unitOfWork,
            NullLogger<ReceiveWebhookUseCase>.Instance);

        _connectorRepo.SaveAsync(new ConnectorConfig
        {
            Id = Guid.NewGuid(),
            Name = "FactoryFlow Production",
            Type = ConnectorType.FactoryFlowWebhook,
            Secret = Secret,
            IsActive = true
        }).Wait();

        _validPayload = File.ReadAllText(Path.Combine("..", "..", "..", "..", "fixtures", "valid-goods-receipt-v1.json"));
    }

    [Fact]
    public async Task Execute_ValidWebhook_IngestsSuccessfullyWithInboxAndOutbox()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var messageId = "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d";
        var signature = HmacWebhookVerifier.ComputeSvixSignatureHeader(_validPayload, timestamp, messageId, Secret);

        var result = await _useCase.ExecuteAsync(
            _validPayload,
            signature,
            timestamp,
            messageId,
            ConnectorType.FactoryFlowWebhook);

        Assert.True(result.IsAccepted);
        Assert.False(result.IsDuplicate);
        Assert.NotNull(result.Contract);
        Assert.Equal("GoodsReceiptConfirmed", result.Contract.EventType);

        // Verify stored in IngestedEventRepository
        Assert.True(await _eventRepo.ExistsAsync(result.EventId));

        // Verify marked in Inbox
        Assert.True(await _inboxRepo.HasBeenProcessedAsync(result.EventId, "webhook-ingestion"));

        // Verify staged in Outbox
        var pending = await _outboxRepo.GetPendingMessagesAsync();
        Assert.Contains(pending, o => o.EventType == "GoodsReceiptConfirmed");
    }

    [Fact]
    public async Task Execute_DuplicateWebhook_ReturnsDuplicateIdempotently()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var messageId = "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d";
        var signature = HmacWebhookVerifier.ComputeSvixSignatureHeader(_validPayload, timestamp, messageId, Secret);

        // First call
        var firstResult = await _useCase.ExecuteAsync(
            _validPayload,
            signature,
            timestamp,
            messageId,
            ConnectorType.FactoryFlowWebhook);

        Assert.True(firstResult.IsAccepted);
        Assert.False(firstResult.IsDuplicate);

        // Second call with same event
        var secondResult = await _useCase.ExecuteAsync(
            _validPayload,
            signature,
            timestamp,
            messageId,
            ConnectorType.FactoryFlowWebhook);

        Assert.True(secondResult.IsAccepted);
        Assert.True(secondResult.IsDuplicate);
        Assert.Equal(firstResult.EventId, secondResult.EventId);
    }

    [Fact]
    public async Task Execute_DuplicateEventId_WithConflictingPayload_ThrowsEventPayloadConflictException()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var messageId = "a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d";
        var signature1 = HmacWebhookVerifier.ComputeSvixSignatureHeader(_validPayload, timestamp, messageId, Secret);

        // First call
        await _useCase.ExecuteAsync(
            _validPayload,
            signature1,
            timestamp,
            messageId,
            ConnectorType.FactoryFlowWebhook);

        // Modify payload while keeping identical EventId
        var modifiedPayload = _validPayload.Replace("GR-2026-10042", "GR-TAMPERED-99999");
        var signature2 = HmacWebhookVerifier.ComputeSvixSignatureHeader(modifiedPayload, timestamp, messageId, Secret);

        var ex = await Assert.ThrowsAsync<EventPayloadConflictException>(() =>
            _useCase.ExecuteAsync(
                modifiedPayload,
                signature2,
                timestamp,
                messageId,
                ConnectorType.FactoryFlowWebhook));

        Assert.Equal(Guid.Parse(messageId), ex.EventId);
    }

    [Fact]
    public async Task Execute_InvalidSignature_ThrowsSignatureValidationException()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var badSignature = "v1,0000000000000000000000000000000000000000000=";

        await Assert.ThrowsAsync<SignatureValidationException>(() =>
            _useCase.ExecuteAsync(
                _validPayload,
                badSignature,
                timestamp,
                "msg_123",
                ConnectorType.FactoryFlowWebhook));
    }

    [Fact]
    public async Task Execute_InvalidContractPayload_ThrowsInvalidEventContractException()
    {
        var invalidPayload = File.ReadAllText(Path.Combine("..", "..", "..", "..", "fixtures", "invalid-goods-receipt-v1-missing-fields.json"));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = HmacWebhookVerifier.ComputeSvixSignatureHeader(invalidPayload, timestamp, "msg_123", Secret);

        await Assert.ThrowsAsync<InvalidEventContractException>(() =>
            _useCase.ExecuteAsync(
                invalidPayload,
                signature,
                timestamp,
                "msg_123",
                ConnectorType.FactoryFlowWebhook));
    }

    [Fact]
    public async Task Execute_InactiveConnector_ThrowsDomainException()
    {
        var connector = await _connectorRepo.GetByTypeAsync(ConnectorType.FactoryFlowWebhook);
        connector!.IsActive = false;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = HmacWebhookVerifier.ComputeSvixSignatureHeader(_validPayload, timestamp, "msg_123", Secret);

        await Assert.ThrowsAsync<DomainException>(() =>
            _useCase.ExecuteAsync(
                _validPayload,
                signature,
                timestamp,
                "msg_123",
                ConnectorType.FactoryFlowWebhook));
    }
}

// -------------------------------------------------------------
// In-Memory Fakes for Unit Testing
// -------------------------------------------------------------
internal sealed class FakeConnectorRepository : IConnectorRepository
{
    private readonly List<ConnectorConfig> _list = [];

    public Task<ConnectorConfig?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_list.FirstOrDefault(c => c.Id == id));

    public Task<ConnectorConfig?> GetByTypeAsync(ConnectorType type, CancellationToken ct = default) =>
        Task.FromResult(_list.FirstOrDefault(c => c.Type == type && c.IsActive));

    public Task<IReadOnlyList<ConnectorConfig>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConnectorConfig>>(_list.ToList());

    public Task SaveAsync(ConnectorConfig connector, CancellationToken ct = default)
    {
        _list.RemoveAll(c => c.Id == connector.Id || c.Type == connector.Type);
        _list.Add(connector);
        return Task.CompletedTask;
    }
}

internal sealed class FakeInboxRepository : IInboxRepository
{
    private readonly Dictionary<(Guid, string), InboxMessage> _messages = [];

    public Task<bool> HasBeenProcessedAsync(Guid messageId, string consumerGroup = "default", CancellationToken ct = default) =>
        Task.FromResult(_messages.ContainsKey((messageId, consumerGroup)));

    public Task<InboxMessage?> GetMessageAsync(Guid messageId, string consumerGroup = "default", CancellationToken ct = default) =>
        Task.FromResult(_messages.GetValueOrDefault((messageId, consumerGroup)));

    public Task MarkProcessedAsync(Guid messageId, string consumerGroup, string eventType, string source, string payloadHash = "", CancellationToken ct = default)
    {
        _messages[(messageId, consumerGroup)] = new InboxMessage
        {
            MessageId = messageId,
            ConsumerGroup = consumerGroup,
            EventType = eventType,
            Source = source,
            PayloadHash = payloadHash,
            ProcessedAt = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task AddAsync(InboxMessage inboxMessage, CancellationToken ct = default)
    {
        _messages[(inboxMessage.MessageId, inboxMessage.ConsumerGroup)] = inboxMessage;
        return Task.CompletedTask;
    }
}

internal sealed class FakeIngestedEventRepository : IIngestedEventRepository
{
    private readonly Dictionary<Guid, IngestedEvent> _events = [];

    public Task<bool> ExistsAsync(Guid eventId, CancellationToken ct = default) =>
        Task.FromResult(_events.ContainsKey(eventId));

    public Task AddAsync(IngestedEvent ev, CancellationToken ct = default)
    {
        _events[ev.EventId] = ev;
        return Task.CompletedTask;
    }

    public Task UpdateStatusAsync(Guid eventId, EventProcessingStatus status, string? errorMessage = null, CancellationToken ct = default)
    {
        if (_events.TryGetValue(eventId, out var ev))
        {
            ev.Status = status;
            ev.ErrorMessage = errorMessage;
        }
        return Task.CompletedTask;
    }

    public Task<IngestedEvent?> GetByIdAsync(Guid eventId, CancellationToken ct = default) =>
        Task.FromResult(_events.GetValueOrDefault(eventId));

    public Task<IReadOnlyList<IngestedEvent>> GetRecentAsync(int limit = 50, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IngestedEvent>>(_events.Values.OrderByDescending(e => e.ReceivedAt).Take(limit).ToList());
}

internal sealed class FakeOutboxRepository : IOutboxRepository
{
    private readonly List<OutboxMessage> _messages = [];

    public Task AddAsync(OutboxMessage message, CancellationToken ct = default)
    {
        _messages.Add(message);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize = 20, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OutboxMessage>>(_messages.Where(m => m.Status == "Pending").Take(batchSize).ToList());

    public Task MarkProcessedAsync(Guid messageId, CancellationToken ct = default)
    {
        var msg = _messages.FirstOrDefault(m => m.Id == messageId);
        if (msg != null)
        {
            msg.Status = "Published";
            msg.ProcessedAt = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(Guid messageId, string error, CancellationToken ct = default)
    {
        var msg = _messages.FirstOrDefault(m => m.Id == messageId);
        if (msg != null)
        {
            msg.RetryCount++;
            msg.ErrorMessage = error;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> GetStuckMessagesAsync(int limit = 50, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OutboxMessage>>(_messages
            .Where(m => m.ProcessedAt == null && m.Status == "Failed")
            .OrderBy(m => m.CreatedAt)
            .Take(limit)
            .ToList());

    public Task<int> RequeueStuckAsync(CancellationToken ct = default)
    {
        var stuck = _messages.Where(m => m.ProcessedAt == null && m.Status == "Failed").ToList();
        foreach (var message in stuck)
        {
            message.Status = "Pending";
            message.RetryCount = 0;
            message.ErrorMessage = null;
        }
        return Task.FromResult(stuck.Count);
    }

    public Task<bool> RequeueMessageAsync(Guid id, CancellationToken ct = default)
    {
        var message = _messages.FirstOrDefault(m => m.Id == id && m.ProcessedAt == null && m.Status == "Failed");
        if (message == null) return Task.FromResult(false);
        message.Status = "Pending";
        message.RetryCount = 0;
        message.ErrorMessage = null;
        return Task.FromResult(true);
    }
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
        Task.FromResult<IUnitOfWorkTransaction>(new FakeTransaction());

    private sealed class FakeTransaction : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
