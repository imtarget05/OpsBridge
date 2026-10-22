using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpsBridge.Domain;
using OpsBridge.Infrastructure.Messaging;
using OpsBridge.Infrastructure.Persistence;
using OpsBridge.Infrastructure.Security;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace OpsBridge.IntegrationTests;

public sealed class OpsBridgeFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:15-alpine")
        .WithDatabase("OpsBridgeTestDb")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder()
        .WithImage("rabbitmq:3-management-alpine")
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();
    public string RabbitMqConnectionString => _rabbit.GetConnectionString();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public HttpClient AuthenticatedClient { get; private set; } = null!;

    public HttpClient ClientForRole(string role)
    {
        var issuer = new OpsBridgeJwtTokenIssuer();
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                JwtBearerDefaults.AuthenticationScheme,
                issuer.IssueToken($"int-{role}", $"int-{role}", role));
        return client;
    }

    public HttpClient AnonymousClient() => Factory.CreateClient();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:SigningKey"] = OpsBridgeJwtTokenIssuer.DefaultSigningKey,
                    ["Jwt:Issuer"] = OpsBridgeJwtTokenIssuer.DefaultIssuer,
                    ["Jwt:Audience"] = OpsBridgeJwtTokenIssuer.DefaultAudience,
                    ["Messaging:Provider"] = "InMemory"
                });
            });
            builder.ConfigureServices(services =>
            {
                // Remove existing DbContext registration
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<OpsBridgeDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }

                // Register with Testcontainers connection string
                services.AddDbContext<OpsBridgeDbContext>(options =>
                    options.UseNpgsql(ConnectionString));
            });
        });

        // Apply the EF Core migrations so the schema matches a deployed database
        // (migration history included) instead of being created ad hoc.
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
        await db.Database.MigrateAsync();

        AuthenticatedClient = ClientForRole(OpsBridgeRoles.Operator);
    }

    public async Task ResetAsync()
    {
        // The xUnit collection shares one RabbitMQ container across tests.
        // Resetting PostgreSQL without its queues can make BasicGet consume a
        // delivery from a previous drill, producing a false pass or failure.
        var connectionFactory = new ConnectionFactory { Uri = new Uri(RabbitMqConnectionString) };
        await using (var connection = await connectionFactory.CreateConnectionAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            await RabbitMqTopology.InitializeTopologyAsync(channel);
            await channel.QueuePurgeAsync(RabbitMqTopology.InboundQueue);
            await channel.QueuePurgeAsync(RabbitMqTopology.DlqQueue);
        }

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpsBridgeDbContext>();
        db.AuditEvents.RemoveRange(db.AuditEvents);
        db.ReviewDecisions.RemoveRange(db.ReviewDecisions);
        db.Discrepancies.RemoveRange(db.Discrepancies);
        db.ReconciliationAllocations.RemoveRange(db.ReconciliationAllocations);
        db.ReconciliationResults.RemoveRange(db.ReconciliationResults);
        db.ReconciliationBatches.RemoveRange(db.ReconciliationBatches);
        db.SupplierInvoiceLines.RemoveRange(db.SupplierInvoiceLines);
        db.SupplierInvoices.RemoveRange(db.SupplierInvoices);
        db.GoodsReceiptLines.RemoveRange(db.GoodsReceiptLines);
        db.GoodsReceipts.RemoveRange(db.GoodsReceipts);
        db.PurchaseOrderLines.RemoveRange(db.PurchaseOrderLines);
        db.PurchaseOrders.RemoveRange(db.PurchaseOrders);
        db.MappingRules.RemoveRange(db.MappingRules);
        db.ConsumerProcessingStates.RemoveRange(db.ConsumerProcessingStates);
        db.IngestedEvents.RemoveRange(db.IngestedEvents);
        db.InboxMessages.RemoveRange(db.InboxMessages);
        db.OutboxMessages.RemoveRange(db.OutboxMessages);
        db.DeadLetterEvents.RemoveRange(db.DeadLetterEvents);
        db.Connectors.RemoveRange(db.Connectors);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (Factory != null)
        {
            await Factory.DisposeAsync();
        }
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }
}
