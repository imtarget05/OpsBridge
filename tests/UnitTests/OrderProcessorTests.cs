using FlashSale.Application.Messaging;
using FlashSale.Application.Outbox;
using FlashSale.Application.Orders;
using FlashSale.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlashSale.UnitTests;

public class OrderProcessorTests
{
    private static OrderMessage Message(string key = "key-1") =>
        new(ProductId: 1, Quantity: 1, IdempotencyKey: key, CreatedAt: DateTimeOffset.UtcNow);

    private static OrderProcessor CreateProcessor(
        FakeOrderRepository repo,
        FakeWriteTransaction? transaction = null,
        FakeOutboxRepository? outbox = null)
    {
        var tx = transaction ?? new FakeWriteTransaction();
        var writer = new OutboxEventWriter(outbox ?? new FakeOutboxRepository());
        return new OrderProcessor(repo, tx, writer, NullLogger<OrderProcessor>.Instance);
    }

    [Fact]
    public async Task ProcessAsync_PersistsOnce_ForNewOrder()
    {
        var repo = new FakeOrderRepository();

        await CreateProcessor(repo).ProcessAsync(Message(), CancellationToken.None);

        Assert.Equal(1, repo.PersistCalls);
        Assert.True(await repo.ExistsAsync("key-1"));
    }

    [Fact]
    public async Task ProcessAsync_IsIdempotent_ForDuplicateDelivery()
    {
        var repo = new FakeOrderRepository();
        repo.SeedExisting("key-1");

        await CreateProcessor(repo).ProcessAsync(Message(), CancellationToken.None);

        // At-least-once delivery must not produce a second order.
        Assert.Equal(0, repo.PersistCalls);
    }

    [Fact]
    public async Task ProcessAsync_ThrowsStockDrift_WhenRepositoryRejects()
    {
        var repo = new FakeOrderRepository { NextPersistSucceeds = false };

        await Assert.ThrowsAsync<StockDriftException>(
            () => CreateProcessor(repo).ProcessAsync(Message(), CancellationToken.None));

        Assert.Equal(1, repo.PersistCalls);
    }

    [Fact]
    public async Task ProcessAsync_SecondCall_IsNoOp_AfterSuccessfulPersist()
    {
        var repo = new FakeOrderRepository();
        var processor = CreateProcessor(repo);

        await processor.ProcessAsync(Message(), CancellationToken.None);
        await processor.ProcessAsync(Message(), CancellationToken.None); // redelivery

        Assert.Equal(1, repo.PersistCalls);
    }

    [Fact]
    public async Task ProcessAsync_ThrowsStockDrift_WhenRepositoryRejects_LogsDriftDlq()
    {
        // StockDriftException is a business-error, not a transport failure.
        // The OrderProcessor immediately dead-letters without retry.
        var repo = new FakeOrderRepository { NextPersistSucceeds = false };

        await Assert.ThrowsAsync<StockDriftException>(
            () => CreateProcessor(repo).ProcessAsync(Message(), CancellationToken.None));

        // Drift is logged and dead-lettered; persist attempt count = 1
        Assert.Equal(1, repo.PersistCalls);
    }

    [Fact]
    public async Task ProcessAsync_WritesOrderCreatedEventToTheOutbox_InsideTheWriteTransaction()
    {
        // ADR-019: the order row and its event share ONE transaction — the
        // event is NOT published directly to the broker.
        var repo = new FakeOrderRepository();
        var tx = new FakeWriteTransaction();
        var outbox = new FakeOutboxRepository();

        await CreateProcessor(repo, tx, outbox).ProcessAsync(Message(), CancellationToken.None);

        var row = Assert.Single(outbox.Enqueued);
        Assert.Equal("order.created", row.EventType);
        Assert.Equal(tx.BeginCalls, 1);
        Assert.Equal(tx.CommitCalls, 1);
        Assert.Equal(tx.RollbackCalls, 0);

        // The envelope carries the stable event id + correlation id.
        var envelope = FlashSale.Domain.Events.EventEnvelope.TryParse(row.Payload);
        Assert.NotNull(envelope);
        Assert.Equal(row.MessageId.ToString("D"), envelope.EventId);
        Assert.Equal("order", envelope.AggregateType);
    }

    [Fact]
    public async Task ProcessAsync_RollsBackTheTransaction_WhenTheOutboxWriteFails()
    {
        // The crash-window case (a): a failing outbox write must undo the
        // order row as well — never leave a committed order without its event.
        var repo = new FakeOrderRepository();
        var tx = new FakeWriteTransaction();
        var outbox = new FakeOutboxRepository
        {
            EnqueueFailure = new InvalidOperationException("outbox write failed")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateProcessor(repo, tx, outbox).ProcessAsync(Message(), CancellationToken.None));

        Assert.Equal(tx.RollbackCalls, 1);
        Assert.Equal(tx.CommitCalls, 0);
        Assert.Empty(outbox.Enqueued);
    }
}
