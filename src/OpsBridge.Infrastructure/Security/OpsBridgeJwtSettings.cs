namespace OpsBridge.Infrastructure.Security;

/// <summary>
/// The JWT signing parameters, FROZEN at start-up.
/// </summary>
/// <remarks>
/// WHY THIS TYPE EXISTS
///
/// The API had two readers of the same three settings and they read them at
/// two different TIMES:
///
///   * <c>src/OpsBridge.Api/Program.cs</c> snapshotted the key into
///     <c>TokenValidationParameters</c> while the composition root was being
///     built - eagerly, once;
///   * <c>src/OpsBridge.Infrastructure/DependencyInjection.cs</c> resolved the
///     issuer lazily, on the FIRST resolution of
///     <c>IOpsBridgeTokenIssuer</c> - which happens after the host is built.
///
/// Both read the same <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// today, so they agree - but <c>IConfiguration</c>'s JSON provider RELOADS on
/// change. The moment an <c>appsettings.json</c> existed, a key rotation on
/// disk would make the ISSUER pick up the new key while the VALIDATOR kept
/// the old one - re-introducing exactly the defect this work fixed (every
/// token the API minted was rejected by the API itself), in the harder-to-see
/// form where the two sides were built from "the same" configuration.
///
/// This type is the answer: the three values are read ONCE, at start-up, into
/// an immutable snapshot. The issuer and the validation path are both handed
/// THAT object, so they cannot diverge - not by accident, not by a reload,
/// not by a different resolution order.
///
/// The snapshot is produced by <see cref="OpsBridgeJwtConfiguration"/>, which
/// remains the single READER of <c>Jwt:SigningKey</c> / <c>Jwt:Issuer</c> /
/// <c>Jwt:Audience</c> and applies the fallback rules.
/// </remarks>
public sealed record OpsBridgeJwtSettings
{
    /// <summary>The key the API both signs and validates with.</summary>
    public required string SigningKey { get; init; }

    /// <summary>The <c>iss</c> claim the API both signs and validates with.</summary>
    public required string Issuer { get; init; }

    /// <summary>The <c>aud</c> claim the API both signs and validates with.</summary>
    public required string Audience { get; init; }
}
