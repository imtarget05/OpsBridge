using FlashSale.Application.Events;
using FlashSale.Domain.Automation;
using FlashSale.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlashSale.Infrastructure.Events;

/// <summary>
/// Composition helpers (spec §1/§11): exactly one event publisher — RabbitMQ
/// when Messaging:Provider=RabbitMQ, in-memory otherwise. Follows the same
/// explicit-provider rule as the order queue (ADR-005): the automation worker
/// only starts for RabbitMQ, so API and worker resolve the same topology.
/// </summary>
public static class DomainEventServiceCollectionExtensions
{
    /// <summary>
    /// Configuration key that selects the DOMAIN EVENT transport
    /// (<c>Kafka</c>, <c>RabbitMQ</c>, <c>InMemory</c>).
    ///
    /// Deliberately separate from <c>Messaging:Provider</c>, which selects the
    /// ORDER QUEUE transport: <see cref="Messaging.MessagingProviderSelector"/>
    /// has no Kafka arm, so a Kafka value there throws
    /// "Unsupported messaging provider". The two transports are independent —
    /// the command queue stays RabbitMQ while the event backbone can be Kafka —
    /// and the fallback below keeps existing deployments (compose, kind) working
    /// unchanged because they only set Messaging:Provider.
    /// </summary>
    public const string ProviderKey = "Events:Provider";

    /// <summary>Resolved event transport provider name for this configuration.</summary>
    public static string? ResolveProvider(IConfiguration configuration)
        => configuration[ProviderKey] ?? configuration["Messaging:Provider"];

    /// <summary>Topic the Kafka producer/consumer agree on for domain events.</summary>
    public static string ResolveKafkaTopic(IConfiguration configuration)
        => configuration["Kafka:Topics:Events"] ?? "orders.events";

    public static IServiceCollection AddDomainEventPublisher(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var kafka = configuration.GetConnectionString("Kafka") ?? configuration["Kafka:BootstrapServers"];
        var rabbit = configuration.GetConnectionString("RabbitMQ");
        var provider = ResolveProvider(configuration);

        if (string.Equals(provider, "Kafka", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(kafka))
        {
            services.AddSingleton<IDomainEventPublisher>(sp =>
                new KafkaDomainEventPublisher(
                    kafka!,
                    sp.GetRequiredService<ILogger<KafkaDomainEventPublisher>>(),
                    ResolveKafkaTopic(configuration)));
        }
        else if (string.Equals(provider, "RabbitMQ", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(rabbit))
        {
            services.AddSingleton<IDomainEventPublisher>(sp =>
                RabbitMqDomainEventPublisher.CreateAsync(
                        rabbit!,
                        sp.GetRequiredService<ILogger<RabbitMqDomainEventPublisher>>())
                    .GetAwaiter().GetResult());
        }
        else
        {
            var inMemory = new InMemoryDomainEventPublisher();
            services.AddSingleton<IDomainEventPublisher>(inMemory);
            services.AddSingleton<IAutomationEventPublisher>(inMemory);
            services.AddSingleton(inMemory);
        }

        return services;
    }

    public static IServiceCollection AddTransactionalOutbox(
        this IServiceCollection services)
    {
        services.AddScoped<FlashSale.Application.Outbox.IOutboxRepository, OutboxRepository>();
        services.AddScoped<FlashSale.Application.Outbox.IInboxRepository, InboxRepository>();
        services.AddScoped<FlashSale.Application.Outbox.OutboxDispatcherUseCase>();
        // ADR-019: the write transaction scope is what lets a business write
        // path commit its state change and its outbox row together.
        services.AddScoped<FlashSale.Application.Persistence.IWriteTransaction, EfWriteTransaction>();
        services.AddScoped<FlashSale.Application.Outbox.OutboxEventWriter>();
        services.AddHostedService<FlashSale.Infrastructure.Outbox.OutboxDispatcherHostedService>();
        return services;
    }

    public static IServiceCollection AddAutomationWorkerHost(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Audit repository is provider-independent (DB-backed): the payment
        // timeout scan runs on ANY messaging provider, so this registration
        // must stay OUTSIDE the transport gate below.
        services.AddScoped<IAutomationRunRepository, AutomationRunRepository>();

        var rabbit = configuration.GetConnectionString("RabbitMQ");
        var kafka = configuration.GetConnectionString("Kafka") ?? configuration["Kafka:BootstrapServers"];
        var provider = ResolveProvider(configuration);

        if (string.Equals(provider, "Kafka", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(kafka))
        {
            // Both transports share ONE processor, so a Kafka cutover cannot
            // silently change what an event does.
            services.AddSingleton<AutomationEventProcessor>();
            services.AddHostedService(sp => new KafkaAutomationEventConsumer(
                kafka!,
                ResolveKafkaTopic(configuration),
                configuration["Kafka:ConsumerGroup"] ?? "flashsale-automation",
                sp.GetRequiredService<AutomationEventProcessor>(),
                sp.GetRequiredService<ILogger<KafkaAutomationEventConsumer>>()));
            return services;
        }

        services.AddSingleton<AutomationEventProcessor>();

        if (string.Equals(provider, "RabbitMQ", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(rabbit))
        {
            services.AddHostedService(sp =>
                AutomationWorkerHost.CreateAsync(
                        rabbit!,
                        sp.GetRequiredService<AutomationEventProcessor>(),
                        sp.GetRequiredService<ILogger<AutomationWorkerHost>>())
                    .GetAwaiter().GetResult());
        }

        return services;
    }
}