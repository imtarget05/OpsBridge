using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpsBridge.Api.Auth;
using OpsBridge.Application.InboxOutbox;

namespace OpsBridge.Api.Endpoints;

public static class OutboxEndpoints
{
    public static IEndpointRouteBuilder MapOutboxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/outbox")
            .WithTags("Outbox Operations");

        group.MapGet("/stuck", async (
            IOutboxRepository outboxRepo,
            int? limit,
            CancellationToken ct) =>
        {
            var fetchLimit = limit.GetValueOrDefault(50);
            if (fetchLimit <= 0) fetchLimit = 50;
            if (fetchLimit > 200) fetchLimit = 200;

            var stuck = await outboxRepo.GetStuckMessagesAsync(fetchLimit, ct);
            return Results.Ok(new
            {
                count = stuck.Count,
                messages = stuck.Select(m => new
                {
                    m.Id,
                    m.EventType,
                    m.Topic,
                    m.CreatedAt,
                    m.RetryCount
                })
            });
        })
        .RequireAuthorization(OpsBridgeAuthPolicies.AuditorOrAbove)
        .WithName("GetStuckOutboxMessages")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/requeue", async (
            IOutboxRepository outboxRepo,
            CancellationToken ct) =>
        {
            var revived = await outboxRepo.RequeueStuckAsync(ct);
            return Results.Ok(new
            {
                status = "requeued",
                count = revived
            });
        })
        .RequireAuthorization(OpsBridgeAuthPolicies.OperatorOrAdmin)
        .WithName("RequeueAllStuckOutboxMessages")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/requeue/{id:guid}", async (
            Guid id,
            IOutboxRepository outboxRepo,
            CancellationToken ct) =>
        {
            var success = await outboxRepo.RequeueMessageAsync(id, ct);
            return success
                ? Results.Ok(new
                {
                    status = "requeued",
                    count = 1
                })
                : Results.NotFound(new
                {
                    error = "NotFound",
                    id
                });
        })
        .RequireAuthorization(OpsBridgeAuthPolicies.OperatorOrAdmin)
        .WithName("RequeueSingleOutboxMessage")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }
}
