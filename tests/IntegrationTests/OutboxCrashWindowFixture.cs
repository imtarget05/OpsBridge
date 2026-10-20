using System.Text;
using FlashSale.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace FlashSale.IntegrationTests;

/// <summary>
/// Dedicated fixture for the outbox crash-window matrix (ADR-019).
///
/// Its own containers — not the shared FlashSale fixture — because one of the
/// scenarios has to take the BROKER down and bring it back, and that must not
/// disturb the other tests in the suite.
/// </summary>
public sealed class OutboxCrashWindowFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:15-alpine")
        .WithDatabase("FlashSaleDb")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder()
        .WithImage("rabbitmq:3-management-alpine")
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

    public string PostgresConnectionString { get; private set; } = string.Empty;
    public string RedisConnectionString { get; private set; } = string.Empty;
    public string RabbitMqConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _rabbit.StartAsync());
        PostgresConnectionString = _postgres.GetConnectionString();
        RedisConnectionString = _redis.GetConnectionString();
        RabbitMqConnectionString = _rabbit.GetConnectionString();
    }

    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(PostgresConnectionString)
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Fresh schema (migrations from zero) + one product with known stock.</summary>
    public async Task<int> ResetDatabaseAsync(int stock = 10)
    {
        await using var db = CreateDbContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        db.Products.Add(new FlashSale.Domain.Entities.Product { Name = "Outbox Test Product", AvailableStock = stock });
        await db.SaveChangesAsync();
        return await db.Products.Select(p => p.Id).FirstAsync();
    }

    public FlashSale.Infrastructure.Redis.RedisStockGateway CreateRedisGateway() =>
        new(StackExchange.Redis.ConnectionMultiplexer.Connect(RedisConnectionString));

    /// <summary>Take the event broker down (the "broker outage" scenario).</summary>
    public Task StopBrokerAsync() => _rabbit.StopAsync();

    /// <summary>
    /// Bring the event broker back. The connection string is refreshed (the
    /// mapped port can change across a restart) and the fixture waits until the
    /// broker accepts connections again.
    /// </summary>
    public async Task StartBrokerAsync()
    {
        await _rabbit.StartAsync();
        RabbitMqConnectionString = _rabbit.GetConnectionString();

        var uri = new Uri(RabbitMqConnectionString);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var probe = new System.Net.Sockets.TcpClient();
                await probe.ConnectAsync(uri.Host, uri.Port);
                return;
            }
            catch (System.Net.Sockets.SocketException)
            {
                await Task.Delay(500);
            }
        }

        throw new InvalidOperationException("The RabbitMQ container did not come back up.");
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }
}

[CollectionDefinition("outbox-crash-window")]
public sealed class OutboxCrashWindowCollection : ICollectionFixture<OutboxCrashWindowFixture>;
