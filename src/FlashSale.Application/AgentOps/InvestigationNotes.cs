namespace FlashSale.Application.AgentOps;

/// <summary>
/// Output of the Investigator role: describes what metadata establishes,
/// identifies unknowns, and links read-only operator checks.
/// No root-cause claims from event metadata alone (§3.1 §4).
/// </summary>
public sealed class InvestigationNotes
{
    /// <summary>What the outbox metadata establishes about this event.</summary>
    public string MetadataEstablishes { get; init; } = string.Empty;

    /// <summary>What cannot be determined from metadata alone.</summary>
    public string Unknowns { get; init; } = string.Empty;

    /// <summary>Read-only operator checks linked for further investigation.</summary>
    public string ReadOnlyOperatorChecks { get; init; } = string.Empty;

    /// <summary>Always empty — the Investigator never claims broker/root cause.</summary>
    public string RootCauseClaim { get; init; } = string.Empty;
}
