using Microsoft.Extensions.Configuration;

namespace OpsBridge.Infrastructure.Security;

/// <summary>
/// The ONE place where the JWT signing parameters are read from configuration.
/// </summary>
/// <remarks>
/// WHY THIS CLASS EXISTS
///
/// The API had two readers of the same three settings and they disagreed:
///
///   * <c>src/OpsBridge.Api/Program.cs</c> VALIDATED tokens with
///     <c>Jwt:SigningKey</c> — the configured key;
///   * <c>src/OpsBridge.Infrastructure/DependencyInjection.cs</c> registered
///     the token ISSUER with no configuration at all, so it always signed with
///     <see cref="OpsBridgeJwtTokenIssuer.DefaultSigningKey"/> — the built-in
///     development key.
///
/// In Development the two agree by accident (there is usually no configured
/// key, so both fall back to the built-in one). In Production they CANNOT
/// agree: <c>Program.cs</c> refuses to start when the configured key is the
/// built-in one, so every deployed API validated tokens with a real key and
/// minted them with the built-in one — every login produced a token the API
/// itself rejected (IDX10517 -> 401). The CI job hid the defect by reading
/// the built-in key out of the source at run time.
///
/// The fix is not "remember to pass the key in both places" — it is to have
/// ONE reader. This class is that reader: the issuer and the validation path
/// both call it, so they cannot disagree about the key, the issuer or the
/// audience again.
///
/// The fallback rules are deliberately the ones <c>Program.cs</c> already
/// applied to the VALIDATION path, so binding the issuer to the configured
/// values changes nothing else:
///   * an absent or blank signing key falls back to the built-in key (the API's
///     Production gate — which is enforced in <c>Program.cs</c>, where the
///     environment is known — refuses to start in Production instead);
///   * an absent issuer or audience falls back to the built-in defaults.
///
/// The Production gate stays where it is, in <c>Program.cs</c>: it needs the
/// environment, and it must keep throwing BEFORE the host is built. Because
/// both sides now read the same value, that gate is meaningful for the issuer
/// as well as for the validation path: a Production deployment cannot mint a
/// token with a key it would refuse to validate.
/// </remarks>
public static class OpsBridgeJwtConfiguration
{
    /// <summary>The configuration section the JWT settings live in.</summary>
    public const string SectionName = "Jwt";

    /// <summary>
    /// Reads the three JWT settings ONCE and returns them as a frozen snapshot.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <remarks>
    /// This is the ONLY place the settings are read. The snapshot it returns
    /// is handed to BOTH the token issuer and the token validation path, so
    /// the two sides cannot drift apart: they hold the same immutable values
    /// instead of each reading the (reloadable) configuration at their own
    /// time.
    /// </remarks>
    public static OpsBridgeJwtSettings Resolve(IConfiguration? configuration) => new()
    {
        SigningKey = ResolveSigningKey(configuration),
        Issuer = ResolveIssuer(configuration),
        Audience = ResolveAudience(configuration),
    };

    /// <summary>
    /// The signing key the API both signs and validates with.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>
    /// The configured key, or <see cref="OpsBridgeJwtTokenIssuer.DefaultSigningKey"/>
    /// when none is configured.
    /// </returns>
    public static string ResolveSigningKey(IConfiguration? configuration)
    {
        var configured = configuration?[$"{SectionName}:SigningKey"];
        return string.IsNullOrWhiteSpace(configured)
            ? OpsBridgeJwtTokenIssuer.DefaultSigningKey
            : configured;
    }

    /// <summary>
    /// The <c>iss</c> claim the API both signs and validates with.
    /// </summary>
    public static string ResolveIssuer(IConfiguration? configuration)
    {
        var configured = configuration?[$"{SectionName}:Issuer"];
        return string.IsNullOrWhiteSpace(configured)
            ? OpsBridgeJwtTokenIssuer.DefaultIssuer
            : configured;
    }

    /// <summary>
    /// The <c>aud</c> claim the API both signs and validates with.
    /// </summary>
    public static string ResolveAudience(IConfiguration? configuration)
    {
        var configured = configuration?[$"{SectionName}:Audience"];
        return string.IsNullOrWhiteSpace(configured)
            ? OpsBridgeJwtTokenIssuer.DefaultAudience
            : configured;
    }
}
