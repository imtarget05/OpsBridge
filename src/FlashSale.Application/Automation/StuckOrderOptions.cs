namespace FlashSale.Application.Automation;

/// <summary>Bounds the accepted-order fulfillment scan (ADR-004).</summary>
public sealed class StuckOrderOptions
{
    public const string SectionName = "Automation:StuckOrder";

    /// <summary>Maximum age of an accepted reservation without an order row.</summary>
    public int ThresholdMinutes { get; set; } = 5;

    /// <summary>How often the worker scans for an overdue accepted order.</summary>
    public int ScanIntervalSeconds { get; set; } = 60;
}
