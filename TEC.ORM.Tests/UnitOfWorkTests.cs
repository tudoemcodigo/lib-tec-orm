using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using TEC.ORM.SqlServer.SoftDelete;
using TEC.ORM.SqlServer.UnitOfWork;
using TEC.ORM.Tests.Database.CodeFirst;

namespace TEC.ORM.Tests;

/// <summary>
/// Transações de mentira para o provedor InMemory (que não tem transação): permitem testar o <c>OrmUnitOfWork</c> sem banco.
/// </summary>
internal sealed class FakeTransactionManager : IDbContextTransactionManager
{
    public bool FailCommit { get; set; }

    public IDbContextTransaction? CurrentTransaction { get; private set; }

    public IDbContextTransaction BeginTransaction() => CurrentTransaction = new FakeTransaction(this);

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(BeginTransaction());

    public void CommitTransaction() => CurrentTransaction?.Commit();

    public Task CommitTransactionAsync(CancellationToken cancellationToken = default) =>
        CurrentTransaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    public void RollbackTransaction() => CurrentTransaction?.Rollback();

    public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) =>
        CurrentTransaction?.RollbackAsync(cancellationToken) ?? Task.CompletedTask;

    public void ResetState() => CurrentTransaction = null;

    public Task ResetStateAsync(CancellationToken cancellationToken = default)
    {
        ResetState();
        return Task.CompletedTask;
    }

    internal void Ended(FakeTransaction transaction)
    {
        if (ReferenceEquals(CurrentTransaction, transaction))
            CurrentTransaction = null;
    }

    internal sealed class FakeTransaction(FakeTransactionManager manager) : IDbContextTransaction
    {
        public Guid TransactionId { get; } = Guid.NewGuid();

        public void Commit()
        {
            if (manager.FailCommit)
                throw new InvalidOperationException("Falha simulada no commit.");
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Commit();
            return Task.CompletedTask;
        }

        public void Rollback()
        {
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose() => manager.Ended(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary><c>OrmUnitOfWork</c>: início duplicado, commit que falha e rollback descartam o rastreador.</summary>
public class UnitOfWorkTests
{
    private static (CodeFirstContext Context, FakeTransactionManager Transactions, OrmUnitOfWork UnitOfWork) Create()
    {
        var context = new CodeFirstContext(new DbContextOptionsBuilder<CodeFirstContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .AddInterceptors(new SoftDeleteInterceptor(TimeProvider.System))
            .ReplaceService<IDbContextTransactionManager, FakeTransactionManager>()
            .Options);
        var transactions = (FakeTransactionManager)context.GetService<IDbContextTransactionManager>();
        return (context, transactions, new OrmUnitOfWork(context));
    }

    [Test]
    public async Task Duplicate_begin_is_rejected()
    {
        var (context, _, unitOfWork) = Create();
        await using (context)
        {
            await unitOfWork.BeginTransactionAsync(CancellationToken.None);

            await Assert.That(unitOfWork.HasActiveTransaction).IsTrue();
            await Assert.That(async () => await unitOfWork.BeginTransactionAsync(CancellationToken.None)).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task Failing_commit_clears_the_tracker_and_ends_the_transaction()
    {
        var (context, transactions, unitOfWork) = Create();
        await using (context)
        {
            await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            context.Customers.Add(new Customer { Name = "Ana", Email = "ana@exemplo.com" });
            transactions.FailCommit = true;

            await Assert.That(async () => await unitOfWork.CommitAsync(CancellationToken.None)).Throws<InvalidOperationException>();

            await Assert.That(context.ChangeTracker.Entries().Any()).IsFalse();
            await Assert.That(unitOfWork.HasActiveTransaction).IsFalse();
        }
    }

    [Test]
    public async Task Successful_commit_saves_and_ends_the_transaction()
    {
        var (context, _, unitOfWork) = Create();
        await using (context)
        {
            await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            context.Customers.Add(new Customer { Name = "Bia", Email = "bia@exemplo.com" });

            await unitOfWork.CommitAsync(CancellationToken.None);

            await Assert.That(unitOfWork.HasActiveTransaction).IsFalse();
            await Assert.That(await context.Customers.CountAsync()).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Rollback_clears_the_tracker()
    {
        var (context, _, unitOfWork) = Create();
        await using (context)
        {
            await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            context.Customers.Add(new Customer { Name = "Caio", Email = "caio@exemplo.com" });

            await unitOfWork.RollbackAsync(CancellationToken.None);

            await Assert.That(context.ChangeTracker.Entries().Any()).IsFalse();
            await Assert.That(unitOfWork.HasActiveTransaction).IsFalse();
            await Assert.That(async () => await unitOfWork.CommitAsync(CancellationToken.None)).Throws<InvalidOperationException>();
        }
    }
}
