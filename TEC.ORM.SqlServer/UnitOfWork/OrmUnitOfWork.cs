using Microsoft.EntityFrameworkCore;
using TEC.Cqrs.Persistence;

namespace TEC.ORM.SqlServer.UnitOfWork;

/// <summary>
/// <see cref="IUnitOfWork"/> do TEC.Cqrs sobre o <c>DbContext</c> do TEC.ORM: com ele registrado, o <c>TransactionBehavior</c>
/// envolve cada command numa transação, sem configuração adicional. Também pode ser usado direto.
/// </summary>
/// <remarks>
/// As escritas do repositório já chamam <c>SaveChangesAsync</c>; dentro da transação elas só ficam visíveis a outras conexões
/// depois do <see cref="CommitAsync"/>. O rollback e um commit que falha descartam também o que estiver no rastreador de
/// alterações (<c>ChangeTracker.Clear()</c>): as entidades rastreadas pelo escopo deixam de ser rastreadas.
/// </remarks>
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
            // Nada foi confirmado: o que ficou no rastreador (inclusive o que o SaveChanges já tinha aceitado) é descartado,
            // como no rollback, para não ser regravado fora da transação pela próxima operação do escopo
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
