using FlashSale.Application.Auth;
using FlashSale.Infrastructure.Auth;
using FlashSale.Infrastructure.Persistence;
using Order.Api.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Security.Claims;

namespace Order.Api.Extensions;

/// <summary>
/// Authentication &amp; authorization setup (ADR-013).
/// Registered AFTER the --migrate branch: JwtOptions.FromConfiguration() fails
/// fast when a signing key is missing, and the migration Job must depend on
/// PostgreSQL and nothing else.
/// </summary>
public static class AuthExtensions
{
    public static void AddFlashSaleAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isProduction)
    {
        var jwtOptions = JwtOptions.FromConfiguration(configuration, isProduction);
        services.AddSingleton(jwtOptions);

        var bootstrapOptions = BootstrapOptions.FromConfiguration(configuration);
        services.AddSingleton(bootstrapOptions);

        if (bootstrapOptions.AdminTokenConfigured)
        {
            Console.Error.WriteLine(
                "WARNING: Bootstrap:AdminToken is configured — POST /api/auth/bootstrap is live. " +
                "Unset it once the initial ADMIN account exists.");
        }
        else
        {
            Console.Error.WriteLine(
                "WARNING: Bootstrap:AdminToken is not configured — POST /api/auth/bootstrap is disabled (404) " +
                "and no ADMIN account can be created. Set it for the first run, then unset it.");
        }

        services.AddSingleton<ITokenIssuer>(new JwtTokenIssuer(jwtOptions));
        services.AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher());
        services.AddScoped<IUserStore, UserStore>();
        services.AddScoped<AuthService>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtOptions.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    RoleClaimType = ClaimTypes.Role,
                    NameClaimType = ClaimTypes.NameIdentifier,
                };
            });

        services.AddAuthorizationBuilder().AddFlashSalePolicies();
    }
}
