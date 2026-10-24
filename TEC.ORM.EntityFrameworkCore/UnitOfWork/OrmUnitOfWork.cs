using Microsoft.EntityFrameworkCore;
using TEC.Cqrs.Persistence;

namespace TEC.ORM.EntityFrameworkCore.UnitOfWork;

/// <summary>
/// <see cref="IUnitOfWork"/> do TEC.Cqrs sobre o <c>DbContext</c> do TEC.ORM.
/// </summary>
public sealed class OrmUnitOfWork(DbContext context) : IUnitOfWork
{
    /// <inheritdoc />
    public bool HasActiveTransaction => context.Database.CurrentTransaction is not null;

    /// <inheritdoc />
    public async Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (HasActiveTransaction)
            throw new InvalidOperationException("Já existe uma transação ativa neste contexto.");
        await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        var transaction = context.Database.CurrentTransaction
                          ?? throw new InvalidOperationException("Não há transação ativa para confirmar.");
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        var transaction = context.Database.CurrentTransaction;
        if (transaction is null)
            return;
        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            context.ChangeTracker.Clear();
        }
    }
}
