namespace FlashSale.Application.AgentOps;

/// <summary>
/// Configuration for the FlashSale Order Operations Agent (local MVP).
/// All values are configuration-bound; nothing is hard-coded.
/// </summary>
public sealed class AgentOpsOptions
{
    public const string SectionName = "AgentOps";

    /// <summary>Feature flag: the investigation workflow is disabled by default.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>A dead-lettered event older than this window is STALE and requires a new run.</summary>
    public int StalenessMinutes { get; set; } = 60;

    /// <summary>Maximum bounded redrafts the Critic may request.</summary>
    public int MaxRedrafts { get; set; } = 1;

    /// <summary>Git/build SHA stamped on every investigation evidence record.</summary>
    public string SourceSha { get; set; } = string.Empty;
}
