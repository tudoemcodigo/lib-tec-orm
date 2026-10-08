using Microsoft.Extensions.DependencyInjection;
using TEC.Cqrs.Persistence;
using TEC.ORM.Abstractions;
using TEC.ORM.Paging;
using TEC.ORM.Queries;
using TEC.ORM.Specifications;

namespace TEC.ORM.LoadTests.Infrastructure;

/// <summary>
/// Carga mista típica de uma API sobre o TEC.ORM: cada operação é uma "requisição" (escopo de DI novo, com a identidade do
/// worker), sobre os dados do próprio worker (sem disputa: qualquer falha é inesperada).
/// </summary>
/// <remarks>
/// Mistura: 22% criar · 25% ler por Id · 15% atualizar · 10% listar página (projeção para DTO) · 10% agregação (Dapper) ·
/// 6% buscar por critério · 5% contar · 4% excluir logicamente · 3% transação (conta + 3 lançamentos com <c>IUnitOfWork</c>).
/// </remarks>
public sealed class Workload(IServiceProvider provider, Guid batch)
{
    /// <summary>Cenários da mistura, na ordem do relatório.</summary>
    public static readonly string[] Scenarios = ["criar", "ler", "atualizar", "listar", "agregar", "buscar", "contar", "delete", "transacao"];

    /// <summary>Estado de um worker: as contas que ele criou e ainda não excluiu.</summary>
    public sealed class Worker(int id)
    {
        public int Id { get; } = id;

        public Random Random { get; } = new(id);

        public List<Guid> Accounts { get; } = [];

        public int Sequence { get; set; }
    }

    /// <summary>Executa uma operação sorteada e registra latência e resultado.</summary>
    public Task RunOnceAsync(Worker worker, LoadMetrics metrics)
    {
        int roll = worker.Random.Next(100);
        if (worker.Accounts.Count < 5 || roll < 22)
            return metrics.MeasureAsync("criar", () => CreateAsync(worker));
        return roll switch
        {
            < 47 => metrics.MeasureAsync("ler", () => InScopeAsync(worker, async s =>
                (await s.GetRequiredService<IOrmRepository<Account, Guid>>().GetByIdAsync(Pick(worker))).Error?.Code)),
            < 62 => metrics.MeasureAsync("atualizar", () => InScopeAsync(worker, async s =>
            {
                var accounts = s.GetRequiredService<IOrmRepository<Account, Guid>>();
                var account = await accounts.GetByIdAsync(Pick(worker));
                if (account.IsFailure)
                    return account.Error!.Code;
                account.Value.Balance += 1;
                return (await accounts.UpdateAsync(account.Value)).Error?.Code;
            })),
            < 72 => metrics.MeasureAsync("listar", () => InScopeAsync(worker, async s =>
                (await s.GetRequiredService<IOrmRepository<Account, Guid>>().ListAsync<AccountSummaryDto>(
                    new PageRequest(1, 20, "Name", Descending: worker.Random.Next(2) == 0),
                    new Specification<Account>(c => c.Batch == batch && c.Category == worker.Id))).Error?.Code)),
            < 82 => metrics.MeasureAsync("agregar", () => InScopeAsync(worker, async s =>
                (await s.GetRequiredService<IOrmQueryExecutor>().QueryAsync<(int Category, decimal Total, int Quantity)>(SqlQuery.Interpolated("carga.saldo-por-categoria",
                    $"SELECT Category, SUM(Balance) AS Total, COUNT(*) AS Quantity FROM loadtest.Accounts WHERE Batch = {batch} AND IsDeleted = 0 AND Category = {worker.Id} GROUP BY Category"))).Error?.Code)),
            < 88 => metrics.MeasureAsync("buscar", () => InScopeAsync(worker, async s =>
            {
                decimal minimum = worker.Random.Next(50);
                return (await s.GetRequiredService<IOrmRepository<Account, Guid>>()
                    .FindAsync(c => c.Batch == batch && c.Category == worker.Id && c.Balance >= minimum && c.Balance < minimum + 2)).Error?.Code;
            })),
            < 93 => metrics.MeasureAsync("contar", () => InScopeAsync(worker, async s =>
                (await s.GetRequiredService<IOrmRepository<Account, Guid>>().CountAsync(c => c.Batch == batch && c.Category == worker.Id)).Error?.Code)),
            < 97 => metrics.MeasureAsync("delete", () => DeleteAsync(worker)),
            _ => metrics.MeasureAsync("transacao", () => TransactionAsync(worker)),
        };
    }

    private async Task<string?> CreateAsync(Worker worker)
    {
        int sequence = ++worker.Sequence;
        return await InScopeAsync(worker, async s =>
        {
            var created = await s.GetRequiredService<IOrmRepository<Account, Guid>>().CreateAsync(new Account
            {
                Batch = batch,
                Name = $"Account {worker.Id:D3}-{sequence:D6}",
                Email = $"w{worker.Id}-{sequence}-{batch:N}@carga",
                Category = worker.Id,
                Balance = worker.Random.Next(100)
            });
            if (created.IsSuccess)
                worker.Accounts.Add(created.Value.Id);
            return created.Error?.Code;
        });
    }

    private async Task<string?> DeleteAsync(Worker worker)
    {
        int index = worker.Random.Next(worker.Accounts.Count);
        var id = worker.Accounts[index];
        string? code = await InScopeAsync(worker, async s => (await s.GetRequiredService<IOrmRepository<Account, Guid>>().DeleteAsync(id)).Error?.Code);
        worker.Accounts.RemoveAt(index);
        return code;
    }

    private async Task<string?> TransactionAsync(Worker worker)
    {
        int sequence = ++worker.Sequence;
        return await InScopeAsync(worker, async s =>
        {
            var unitOfWork = s.GetRequiredService<IUnitOfWork>();
            await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            var account = await s.GetRequiredService<IOrmRepository<Account, Guid>>().CreateAsync(new Account
            {
                Batch = batch,
                Name = $"Account {worker.Id:D3}-{sequence:D6}",
                Email = $"w{worker.Id}-{sequence}-{batch:N}@carga",
                Category = worker.Id
            });
            var ledgerEntries = s.GetRequiredService<IOrmRepository<LedgerEntry, long>>();
            for (int i = 0; account.IsSuccess && i < 3; i++)
            {
                var ledgerEntry = await ledgerEntries.CreateAsync(new LedgerEntry
                {
                    Batch = batch,
                    AccountId = account.Value.Id,
                    Amount = 10m + i,
                    PostedAt = DateTimeOffset.UtcNow,
                    Description = $"Lançamento {i}"
                });
                if (ledgerEntry.IsFailure)
                {
                    await unitOfWork.RollbackAsync(CancellationToken.None);
                    return ledgerEntry.Error!.Code;
                }
            }
            if (account.IsFailure)
            {
                await unitOfWork.RollbackAsync(CancellationToken.None);
                return account.Error!.Code;
            }
            await unitOfWork.CommitAsync(CancellationToken.None);
            worker.Accounts.Add(account.Value.Id);
            return null;
        });
    }

    private static Guid Pick(Worker worker) => worker.Accounts[worker.Random.Next(worker.Accounts.Count)];

    private async Task<string?> InScopeAsync(Worker worker, Func<IServiceProvider, Task<string?>> action)
    {
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestUser>().As(worker.Id);
        return await action(scope.ServiceProvider);
    }

    /// <summary>Roda <paramref name="workers"/> workers até o cancelamento; devolve os workers (para conferências no fim).</summary>
    public async Task<Worker[]> RunAsync(int workers, LoadMetrics metrics, CancellationToken stop)
    {
        var state = Enumerable.Range(0, workers).Select(i => new Worker(i)).ToArray();
        await Task.WhenAll(state.Select(worker => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
                await RunOnceAsync(worker, metrics);
        })));
        return state;
    }
}
