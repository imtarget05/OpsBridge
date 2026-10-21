using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpsBridge.Application.Connectors;
using OpsBridge.Application.Contracts;
using OpsBridge.Application.InboxOutbox;
using OpsBridge.Application.Ingestion;
using OpsBridge.Application.Security;
using OpsBridge.Application.UseCases;
using OpsBridge.Infrastructure.Connectors;
using OpsBridge.Infrastructure.Contracts;
using OpsBridge.Infrastructure.Persistence;
using OpsBridge.Infrastructure.Security;

namespace OpsBridge.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOpsBridgeInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        OpsBridgeJwtSettings? jwtSettings = null)
    {
        // The connection string is resolved in one place (OpsBridgeConnectionStringResolver)
        // so the application wiring and the deployment-time schema tools can never drift
        // apart. The application path keeps the historical localhost fallback; the
        // migration and baseline entrypoints deliberately do not (see ADR-018).
        var connectionString = OpsBridgeConnectionStringResolver.ResolveForApplication(configuration);

        services.AddDbContext<OpsBridgeDbContext>(options =>
            options.UseNpgsql(connectionString));

        // Repositories
        services.AddScoped<IConnectorRepository, EfConnectorRepository>();
        services.AddScoped<IIngestedEventRepository, EfIngestedEventRepository>();
        services.AddScoped<IInboxRepository, EfInboxRepository>();
        services.AddScoped<IConsumerProcessingRepository, EfConsumerProcessingRepository>();
        services.AddScoped<IOutboxRepository, EfOutboxRepository>();
        services.AddScoped<IDeadLetterEventRepository, EfDeadLetterEventRepository>();
        services.AddScoped<OpsBridge.Application.Common.IUnitOfWork, EfUnitOfWork>();

        // Reconciliation Repositories
        services.AddScoped<OpsBridge.Application.Reconciliation.IPurchaseOrderRepository, EfPurchaseOrderRepository>();
        services.AddScoped<OpsBridge.Application.Reconciliation.IGoodsReceiptRepository, EfGoodsReceiptRepository>();
        services.AddScoped<OpsBridge.Application.Reconciliation.ISupplierInvoiceRepository, EfSupplierInvoiceRepository>();
        services.AddScoped<OpsBridge.Application.Reconciliation.IMappingRuleRepository, EfMappingRuleRepository>();
        services.AddScoped<OpsBridge.Application.Reconciliation.IReconciliationRepository, EfReconciliationRepository>();
        services.AddScoped<OpsBridge.Application.Reconciliation.IAuditEventRepository, EfAuditEventRepository>();

        // Adapters
        services.AddSingleton<IWebhookSignatureVerifier, HmacWebhookVerifier>();
        services.AddSingleton<IContractValidator, JsonContractValidator>();

        // The token ISSUER is bound to the SAME configured key, issuer and
        // audience the API validates tokens with (OpsBridgeJwtConfiguration is
        // the single reader of Jwt:SigningKey / Jwt:Issuer / Jwt:Audience, and
        // src/OpsBridge.Api/Program.cs uses it too).
        //
        // It used to be registered with NO configuration, which meant the
        // issuer always signed with the built-in development key while
        // Program.cs validated with the configured one. In Production the two
        // cannot be the same (Program.cs refuses the built-in key there), so
        // every token the API issued was signed with a key the API itself
        // rejected: every login failed with IDX10517 and a 401.
        //
        // The values come from a FROZEN SNAPSHOT (OpsBridgeJwtSettings), not
        // from the configuration at resolution time. The old registration
        // resolved the configuration LAZILY - on the first resolution of the
        // issuer, which is after the host is built - while the validation
        // path snapshotted it EAGERLY while the composition root was built.
        // IConfiguration reloads on change, so a key rotation on disk would
        // have made the issuer sign with the new key while the validator kept
        // the old one: the same defect, in a harder-to-see form. Passing the
        // snapshot in makes the two sides provably identical.
        //
        // When no snapshot is supplied (a test that builds its own host), the
        // snapshot is taken HERE - once, at registration time - so the
        // property holds in that case too.
        var frozenJwt = jwtSettings ?? OpsBridgeJwtConfiguration.Resolve(configuration);
        services.AddSingleton(frozenJwt);
        services.AddSingleton<IOpsBridgeTokenIssuer>(_ => new OpsBridgeJwtTokenIssuer(
            frozenJwt.SigningKey,
            frozenJwt.Issuer,
            frozenJwt.Audience));
        services.AddHttpClient<IFactoryFlowClient, MockFactoryFlowClient>();

        // Application Use Cases
        services.AddScoped<ReceiveWebhookUseCase>();
        services.AddScoped<ReplayDeadLetterEventUseCase>();
        services.AddScoped<ReconcileInvoiceUseCase>();
        services.AddScoped<ReviewDiscrepancyUseCase>();

        return services;
    }
}
