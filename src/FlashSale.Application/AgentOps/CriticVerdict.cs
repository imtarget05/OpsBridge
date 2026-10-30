namespace FlashSale.Application.AgentOps;

/// <summary>
/// Output of the Critic/Policy role: verifies every material claim against
/// evidence, checks scope and freshness, and blocks actions (§3.1 §5).
/// One bounded redraft maximum.
/// </summary>
public sealed class CriticVerdict
{
    public bool ClaimsVerified { get; init; }
    public bool ScopeValid { get; init; }
    public bool FreshnessValid { get; init; }
    public bool ActionsBlocked { get; init; }
    public int RedraftCount { get; init; }
    public string BlockingReasons { get; init; } = string.Empty;

    public bool Approved => ClaimsVerified && ScopeValid && FreshnessValid && ActionsBlocked && RedraftCount <= 1;
}
