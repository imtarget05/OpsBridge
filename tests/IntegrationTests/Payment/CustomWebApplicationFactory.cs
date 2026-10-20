extern alias payment;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

using PaymentServiceMarker = payment.Payment.Service.PaymentServiceMarker;

namespace FlashSale.IntegrationTests;

/// <summary>
/// Boots Payment.Service's real Program.cs without colliding with Order.Api's
/// same-named <c>Program</c> class. The connection string is injected through
/// the environment before the host builds (matching the constraint documented
/// in AuthFlowTests for the order API).
/// </summary>
internal sealed class CustomWebApplicationFactory : WebApplicationFactory<PaymentServiceMarker>
{
    private readonly string _connectionString;

    public CustomWebApplicationFactory(string connectionString) =>
        _connectionString = connectionString;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__PaymentDb", _connectionString);
        builder.UseEnvironment("Development");
        return base.CreateHost(builder);
    }
}
