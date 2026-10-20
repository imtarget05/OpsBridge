namespace FlashSale.Application.Persistence;

/// <summary>
/// Ambient write-transaction scope (ADR-019).
///
/// <para>
/// The transactional outbox only works if the business state change and the
/// outbox row are written by the SAME database transaction. Before ADR-019 each
/// repository call committed on its own, so "write order, then write event"
/// was two independent commits — the dual-write this repo kept documenting as
/// a gap.
/// </para>
///
/// <para>
/// The scope is <b>nesting-aware</b>: the outermost scope owns the physical
/// transaction, an inner scope joins it and never commits it. A scope that
/// rolls back therefore rolls back EVERY state change made inside it.
/// </para>
/// </summary>
public interface IWriteTransaction
{
    /// <summary>
    /// Begin (or join) the write transaction. Idempotent: calling it twice on
    /// the same scope does not open a second transaction.
    /// </summary>
    Task BeginAsync(CancellationToken ct = default);

    /// <summary>
    /// Commit the transaction — but only when this scope owns it. A nested
    /// scope must never commit the outer transaction.
    /// </summary>
    Task CommitAsync(CancellationToken ct = default);

    /// <summary>
    /// Roll back everything written inside the transaction. A no-op for a
    /// nested scope (the owner rolls back).
    /// </summary>
    Task RollbackAsync(CancellationToken ct = default);
}
