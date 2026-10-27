namespace OpsBridge.Application.Identity;

/// <summary>
/// Abstraction for production identity providers (Azure AD Entra ID, etc.).
/// Replaces demo persona login in production.
/// </summary>
public interface IIdentityProvider
{
    /// <summary>
    /// Validates an IdP-issued access token and returns the authenticated user's claims.
    /// Returns null if the token is invalid or expired.
    /// </summary>
    Task<IdentityUser?> ValidateTokenAsync(string accessToken, CancellationToken ct = default);

    /// <summary>
    /// Returns the OIDC authorization URL for initiating the login flow.
    /// </summary>
    string GetAuthorizationUrl(string state, string redirectUri);

    /// <summary>
    /// Exchanges an OIDC authorization code for tokens.
    /// </summary>
    Task<TokenResponse?> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct = default);
}

public sealed record IdentityUser(
    string UserId,
    string Username,
    string Email,
    string Role,
    IEnumerable<string> Claims);

public sealed record TokenResponse(
    string AccessToken,
    string? RefreshToken,
    int ExpiresInSeconds,
    string IdToken);