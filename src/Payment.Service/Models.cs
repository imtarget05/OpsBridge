// Payment.Service types live in the Payment.Service namespace so they do NOT
// collide with Order.Api's top-level Program/dto classes when both assemblies
// are referenced from the shared integration test project (CS0433 ambiguity).
// File-scoped namespace: this file contains only these members, so it stays
// above all top-level-statement hosts (Program.cs).
using Microsoft.EntityFrameworkCore;

namespace Payment.Service;

/// <summary>
/// Test-only entry-point type so <see cref="WebApplicationFactory{TEntryPoint}"/>
/// resolves the payment host assembly without colliding with Order.Api's same-named
/// <c>Program</c> class. Mirrors the pattern used by Order.Api tests.
/// </summary>
public sealed class PaymentServiceMarker;

/// <summary>
/// Payment.Service's own persistence context. Table-level separation from the
/// order API's schema: this context maps ONLY payment_records and never reads
/// order tables (microservice decomposition rule: no cross-service table reads).
/// </summary>
public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<PaymentRecord> Records => Set<PaymentRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentRecord>(entity =>
        {
            entity.ToTable("payment_records");
            entity.HasKey(r => r.IdempotencyKey);
            entity.Property(r => r.IdempotencyKey).HasMaxLength(128).ValueGeneratedNever();
            entity.Property(r => r.Currency).HasMaxLength(8);
            entity.Property(r => r.Status).HasMaxLength(32);
            entity.Property(r => r.TransactionId).HasMaxLength(64);
            entity.Property(r => r.FailureReason).HasMaxLength(256);
            entity.Property(r => r.Amount).HasPrecision(12, 2);
        });
    }
}

/// <summary>
/// Durable row for one idempotent payment request. The idempotency key is the
/// primary key: the database, not process memory, is the deduplication ledger.
/// </summary>
public sealed class PaymentRecord
{
    public required string IdempotencyKey { get; init; }
    public Guid PaymentId { get; init; }
    public int OrderId { get; init; }
    public decimal Amount { get; init; }
    public required string Currency { get; init; }
    public bool Success { get; init; }
    public required string Status { get; init; }
    public string? TransactionId { get; init; }
    public string? FailureReason { get; init; }
    public bool IsTransientError { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }

    public static PaymentRecord From(
        ProcessPaymentRequest request,
        bool Success,
        string Status,
        string? TransactionId,
        string? FailureReason,
        bool IsTransientError) => new()
        {
            IdempotencyKey = request.IdempotencyKey,
            PaymentId = request.PaymentId,
            OrderId = request.OrderId,
            Amount = request.Amount,
            Currency = request.Currency,
            Success = Success,
            Status = Status,
            TransactionId = TransactionId,
            FailureReason = FailureReason,
            IsTransientError = IsTransientError,
            ProcessedAt = TruncateToMicroseconds(DateTimeOffset.UtcNow),
        };

    /// <summary>
    /// PostgreSQL <c>timestamptz</c> stores whole microseconds while .NET ticks
    /// are 100ns. Normalize before persistence so first delivery and replay use
    /// the same timestamp at the API boundary.
    /// </summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        const long ticksPerMicrosecond = 10;
        var truncatedTicks = value.Ticks - (value.Ticks % ticksPerMicrosecond);
        return new DateTimeOffset(truncatedTicks, value.Offset);
    }

    public PaymentRecordDto ToDto() => new(
        Success,
        Status,
        TransactionId,
        FailureReason,
        IsTransientError,
        ProcessedAt);
}

public record ProcessPaymentRequest(
    Guid PaymentId,
    int OrderId,
    string IdempotencyKey,
    decimal Amount,
    string Currency);

public record PaymentRecordDto(
    bool Success,
    string Status,
    string? TransactionId,
    string? FailureReason,
    bool IsTransientError,
    DateTimeOffset ProcessedAt);
