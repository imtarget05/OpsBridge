using FlashSale.Application.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FlashSale.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IWriteTransaction"/> (ADR-019).
///
/// <para>
/// One physical transaction per scope. A nested scope joins the ambient
/// transaction instead of opening its own — EF would throw
/// "A transaction is already in progress" — and therefore never commits or
/// rolls back it. The owner (outermost) scope decides the fate of everything
/// written inside.
/// </para>
/// </summary>
public sealed class EfWriteTransaction(AppDbContext db) : IWriteTransaction
{
    private IDbContextTransaction? _owned;

    public async Task BeginAsync(CancellationToken ct = default)
    {
        if (_owned is not null || db.Database.CurrentTransaction is not null)
        {
            // Already inside a transaction (this scope owns it, or an outer
            // scope does) — join it instead of opening a second one.
            return;
        }

        _owned = await db.Database.BeginTransactionAsync(ct);
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_owned is null)
        {
            // Nested scope: the owner commits.
            return;
        }

        await _owned.CommitAsync(ct);
        await _owned.DisposeAsync();
        _owned = null;
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (_owned is null)
        {
            // Nested scope: the owner rolls back.
            return;
        }

        await _owned.RollbackAsync(ct);
        await _owned.DisposeAsync();
        _owned = null;
    }
}
