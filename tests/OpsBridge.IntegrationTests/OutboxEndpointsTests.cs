using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsBridge.Domain.InboxOutbox;
using OpsBridge.Infrastructure.Persistence;

namespace OpsBridge.IntegrationTests;

[Collection("OpsBridgeIntegration")]
public class OutboxEndpointsTests(OpsBridgeFixture fixture)
{
    private HttpClient CreateOperatorClient() => fixture.ClientForRole("Operator");
    private HttpClient CreateAnonymousClient() => fixture.AnonymousClient();

    [Fact]
    public async Task GetStuck_Anonymous_ReturnsUnauthorized()
    {
        var client = CreateAnonymousClient();
        var response = await client.GetAsync("/api/v1/outbox/stuck");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Requeue_Anonymous_ReturnsUnauthorized()
    {
        var client = CreateAnonymousClient();
        var response = await client.PostAsync("/api/v1/outbox/requeue", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Requeue_Auditor_ReturnsForbidden()
    {
        using var client = fixture.ClientForRole("Auditor");
        using var response = await client.PostAsync("/api/v1/outbox/requeue", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetStuck_And_RequeueAll_RestoresFailedOutboxMessages()
    {
        await fixture.ResetAsync();
        var client = CreateOperatorClient();

        var stuckId1 = Guid.NewGuid();
        var stuckId2 = Guid.NewGuid();

        // A published message must not be requeued by an operator.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
            db.OutboxMessages.AddRange(
                new OutboxMessage
                {
                    Id = stuckId1,
                    EventType = "goods_receipt.created",
                    Topic = "warehouse.receipts",
                    Payload = "{\"receiptNumber\": \"GR-100\"}",
                    Status = "Failed",
                    RetryCount = 5,
                    ErrorMessage = "Broker connection refused.",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
                },
                new OutboxMessage
                {
                    Id = stuckId2,
                    EventType = "invoice.matched",
                    Topic = "finance.invoices",
                    Payload = "{\"invoiceId\": \"INV-100\"}",
                    Status = "Published",
                    RetryCount = 2,
                    ErrorMessage = "Transient timeout.",
                    ProcessedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
                }
            );
            await db.SaveChangesAsync();
        }

        // 1. GET /api/v1/outbox/stuck
        var getResponse = await client.GetAsync("/api/v1/outbox/stuck");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var responseBody = await getResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Broker connection refused.", responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("receiptNumber", responseBody, StringComparison.Ordinal);

        var getJson = await getResponse.Content.ReadFromJsonAsync<StuckOutboxResponse>();
        Assert.NotNull(getJson);
        Assert.Equal(1, getJson!.Count);

        // 2. POST /api/v1/outbox/requeue
        var requeueResponse = await client.PostAsync("/api/v1/outbox/requeue", null);
        Assert.Equal(HttpStatusCode.OK, requeueResponse.StatusCode);

        var requeueJson = await requeueResponse.Content.ReadFromJsonAsync<RequeueResponse>();
        Assert.NotNull(requeueJson);
        Assert.Equal("requeued", requeueJson!.Status);
        Assert.Equal(1, requeueJson.Count);

        // 3. Verify in DB and via GET that they are no longer stuck
        var verifyResponse = await client.GetAsync("/api/v1/outbox/stuck");
        var verifyJson = await verifyResponse.Content.ReadFromJsonAsync<StuckOutboxResponse>();
        Assert.NotNull(verifyJson);
        Assert.Equal(0, verifyJson!.Count);

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
            var messages = await db.OutboxMessages.Where(m => m.Id == stuckId1 || m.Id == stuckId2).ToListAsync();
            var requeued = messages.Single(m => m.Id == stuckId1);
            Assert.Equal("Pending", requeued.Status);
            Assert.Equal(0, requeued.RetryCount);
            Assert.Null(requeued.ErrorMessage);

            var published = messages.Single(m => m.Id == stuckId2);
            Assert.Equal("Published", published.Status);
            Assert.Equal(2, published.RetryCount);
            Assert.Equal("Transient timeout.", published.ErrorMessage);
        }
    }

    [Fact]
    public async Task RequeueSingle_ValidId_RequeuesMessage_And_InvalidId_ReturnsNotFound()
    {
        await fixture.ResetAsync();
        var client = CreateOperatorClient();

        var stuckId = Guid.NewGuid();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = stuckId,
                EventType = "inventory.synced",
                Topic = "inventory.events",
                Payload = "{}",
                Status = "Failed",
                RetryCount = 5,
                ErrorMessage = "Fatal exception",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-15)
            });
            await db.SaveChangesAsync();
        }

        // Test not found for non-existent ID
        var notFoundResponse = await client.PostAsync($"/api/v1/outbox/requeue/{Guid.NewGuid()}", null);
        Assert.Equal(HttpStatusCode.NotFound, notFoundResponse.StatusCode);

        // Test success for existing stuck ID
        var response = await client.PostAsync($"/api/v1/outbox/requeue/{stuckId}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
            var msg = await db.OutboxMessages.SingleAsync(m => m.Id == stuckId);
            Assert.Equal("Pending", msg.Status);
            Assert.Equal(0, msg.RetryCount);
            Assert.Null(msg.ErrorMessage);
        }
    }

    private sealed record StuckOutboxResponse(int Count);
    private sealed record RequeueResponse(string Status, int Count);
}
