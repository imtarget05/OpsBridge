using FlashSale.Application.Automation;
using FlashSale.Application.Messaging;
using FlashSale.Application.Outbox;
using FlashSale.Application.Orders;
using FlashSale.Domain.Entities;
using FlashSale.Infrastructure.Messaging;
using FlashSale.Infrastructure.Persistence;
using FlashSale.Infrastructure.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace FlashSale.IntegrationTests;

/// <summary>
/// Shared Testcontainers environment: real Postgres + Redis + RabbitMQ.
/// One fixture per test collection; each test gets a fresh database.
/// Requires Docker on the dev machine. No Azure dependencies.
/// </summary>
public sealed class FlashSaleFixture : IAsyncLifetime
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

    public AppDbContext CreateDbContext(string? connectionString = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString ?? PostgresConnectionString)
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Fresh schema (migrations from zero) + one product with known stock.</summary>
    public async Task<int> ResetDatabaseAsync(int stock = 10, string? connectionString = null)
    {
        await using var db = CreateDbContext(connectionString);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
        db.Products.Add(new Product { Name = "Test Product", AvailableStock = stock });
        await db.SaveChangesAsync();
        return await db.Products.Select(p => p.Id).FirstAsync();
    }

    public RedisStockGateway CreateRedisGateway(string? connectionString = null) =>
        new(ConnectionMultiplexer.Connect(connectionString ?? RedisConnectionString));

    /// <summary>
    /// The order processor wired to the REAL outbox (ADR-019): the order row
    /// and its <c>order.created</c> event are written in one transaction.
    /// </summary>
    public OrderProcessor CreateOrderProcessor(AppDbContext db) =>
        new(
            new OrderRepository(db),
            new EfWriteTransaction(db),
            new OutboxEventWriter(new OutboxRepository(db)),
            NullLogger<OrderProcessor>.Instance);

    /// <summary>
    /// The payment-recording use case wired to the REAL outbox (ADR-019): the
    /// order status change and its payment/order events are written in one
    /// transaction.
    /// </summary>
    public RecordPaymentUseCase CreateRecordPaymentUseCase(AppDbContext db) =>
        new(
            new PaymentRepository(db),
            new OrderReadModel(db),
            new AutomationRunRepository(db),
            new EfWriteTransaction(db),
            new OutboxEventWriter(new OutboxRepository(db)),
            NullLogger<RecordPaymentUseCase>.Instance);

    public Task<RabbitMQOrderQueue> CreateRabbitQueueAsync(string queueName = "orders-test") =>
        RabbitMQOrderQueue.CreateAsync(RabbitMqConnectionString, queueName, NullLogger<RabbitMQOrderQueue>.Instance);

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }
}

[CollectionDefinition("flashsale")]
public sealed class FlashSaleCollection : ICollectionFixture<FlashSaleFixture>;
