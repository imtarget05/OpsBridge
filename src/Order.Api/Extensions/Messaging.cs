using FlashSale.Application.Messaging;
using FlashSale.Application.Inventory;
using FlashSale.Application.Orders;
using FlashSale.Infrastructure.Messaging;
using FlashSale.Infrastructure.Persistence;
using FlashSale.Infrastructure.Redis;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Threading.RateLimiting;

namespace Order.Api.Extensions;

/// <summary>
/// Redis, queue provider selection, and authoritative server-side rate limiting
/// (ADR-005 / ADR-025 / Phase 1).
/// </summary>
public static class MessagingExtensions
{
    public static void AddFlashSaleRedisAndQueue(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Redis: enabled when reachable, transparently skipped otherwise.
        var redisConnection = configuration.GetConnectionString("Redis") ?? "localhost:6379";
        services.AddSingleton<IConnectionMultiplexer>(sp =>
            ConnectionMultiplexer.Connect(redisConnection));
        services.AddSingleton<IStockReservationGateway>(sp =>
            new RedisStockGateway(sp.GetRequiredService<IConnectionMultiplexer>()));

        // Queue selection: exactly one provider from configuration (ADR-005).
        services.AddOrderQueue(configuration, "Production");

        services.AddScoped<OrderProcessor>();
        services.AddScoped<SubmitOrderUseCase>();
        services.AddScoped<OutboxRecoveryService>();
        services.AddScoped<StockResyncUseCase>();
        services.AddHostedService<OrderProcessorHost>();
    }

    public static void AddFlashSaleRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var rateLimitingEnabled = configuration.GetValue("RateLimiting:Enabled", true);
        if (!rateLimitingEnabled) return;

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                var clientIp = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var path = context.HttpContext.Request.Path;
                logger.LogWarning("Host rate limit exceeded for client {ClientIp} on {Path}", clientIp, path);

                context.HttpContext.Response.ContentType = "application/problem+json";
                var retrySeconds = 60;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    retrySeconds = (int)Math.Max(1, retryAfter.TotalSeconds);
                }
                context.HttpContext.Response.Headers.RetryAfter = retrySeconds.ToString();

                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    type = "https://tools.ietf.org/html/rfc7807",
                    title = "Too Many Requests",
                    status = StatusCodes.Status429TooManyRequests,
                    detail = "Authoritative server-side rate limit exceeded. Edge approximations reinforced by origin policy.",
                    retryAfterSeconds = retrySeconds
                }, cancellationToken: token);
            };

            // Sensitive endpoint limiter: burst protection for orders and login
            var orderPermitLimit = configuration.GetValue("RateLimiting:OrderTokenLimit", 30);
            var orderTokensPerPeriod = configuration.GetValue("RateLimiting:OrderTokensPerPeriod", 20);
            options.AddTokenBucketLimiter("order-submission", opt =>
            {
                opt.TokenLimit = orderPermitLimit;
                opt.TokensPerPeriod = orderTokensPerPeriod;
                opt.ReplenishmentPeriod = TimeSpan.FromMinutes(1);
                opt.QueueLimit = 0; // Fail-fast without queuing
            });

            // Global IP partitioned limiter
            var globalPermitLimit = configuration.GetValue("RateLimiting:GlobalPermitLimit", 120);
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                var clientKey = httpContext.User?.Identity?.IsAuthenticated == true
                    ? (httpContext.User.Identity.Name ?? "authenticated")
                    : (httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous");

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: clientKey,
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = globalPermitLimit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    });
            });
        });
    }
}
