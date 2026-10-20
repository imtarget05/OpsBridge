using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Payment.Service.Persistence;

/// <summary>
/// Design-time factory so `dotnet ef migrations add` works for
/// <see cref="PaymentDbContext"/> without booting the service host.
///
/// The connection string is only used for design-time tooling (scaffolding and
/// `dotnet ef database update`); it is never read at runtime — the runtime
/// wiring lives in Program.cs (<c>ConnectionStrings:PaymentDb</c>).
/// </summary>
public sealed class PaymentDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PaymentDb")
            ?? "Host=localhost;Port=5432;Database=FlashSaleDb;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new PaymentDbContext(options);
    }
}
