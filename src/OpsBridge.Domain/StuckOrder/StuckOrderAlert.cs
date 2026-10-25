namespace OpsBridge.Domain.StuckOrder;

public enum StuckOrderStatus
{
    Open,
    UnderInvestigation,
    Resolved,
    FalsePositive
}

public enum StuckOrderSeverity
{
    Warning,
    Critical
}

public sealed class StuckOrderAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PurchaseOrderId { get; set; }
    public string PurchaseOrderNumber { get; set; } = string.Empty;
    public string SupplierCode { get; set; } = string.Empty;
    public StuckOrderStatus Status { get; set; } = StuckOrderStatus.Open;
    public StuckOrderSeverity Severity { get; set; } = StuckOrderSeverity.Warning;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? AssignedToUserId { get; set; }
    public string? ResolutionNotes { get; set; }
    public int RetryCount { get; set; } = 0;
    public DateTimeOffset? LastRetryAt { get; set; }
    public bool IsDeadLettered { get; set; } = false;
    public string? DeadLetterReason { get; set; }
}

public sealed record StuckOrderDetectionResult(
    bool IsStuck,
    string PurchaseOrderNumber,
    string SupplierCode,
    string Reason,
    StuckOrderSeverity Severity,
    TimeSpan TimeSinceLastProgress,
    TimeSpan SlaThreshold);

public sealed record StuckOrderDetectionOptions(
    TimeSpan SlaThreshold,
    int MaxRetries = 3,
    TimeSpan RetryInterval = default)
{
    public TimeSpan RetryInterval { get; init; } = RetryInterval == default ? TimeSpan.FromHours(1) : RetryInterval;
}