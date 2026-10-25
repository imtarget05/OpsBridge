using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsBridge.Application.InboxOutbox;
using OpsBridge.Domain.InboxOutbox;
using OpsBridge.Infrastructure.Persistence;

namespace OpsBridge.IntegrationTests;

[Collection("OpsBridgeIntegration")]
public sealed class OutboxRequeueIntegrationTests(OpsBridgeFixture fixture)
{
    [Fact]
    public async Task Requeue_OnlyTransitionsTerminalUnpublishedMessages()
    {
        await fixture.ResetAsync();

        var failedOne = Message("Failed", null);
        var failedTwo = Message("Failed", null);
        var published = Message("Published", DateTimeOffset.UtcNow);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
        db.OutboxMessages.AddRange(failedOne, failedTwo, published);
        await db.SaveChangesAsync();

        var repository = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        var stuck = await repository.GetStuckMessagesAsync();
        Assert.Equal(2, stuck.Count);
        Assert.DoesNotContain(stuck, message => message.Id == published.Id);

        Assert.False(await repository.RequeueMessageAsync(published.Id));
        Assert.True(await repository.RequeueMessageAsync(failedOne.Id));
        Assert.False(await repository.RequeueMessageAsync(failedOne.Id));
        Assert.Equal(1, await repository.RequeueStuckAsync());
        Assert.Empty(await repository.GetStuckMessagesAsync());
    }

    private static OutboxMessage Message(string status, DateTimeOffset? processedAt) => new()
    {
        Id = Guid.NewGuid(),
        EventType = "TestEvent",
        Topic = "test.outbox",
        Payload = "{}",
        Status = status,
        ProcessedAt = processedAt,
        RetryCount = status == "Failed" ? 5 : 0,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
