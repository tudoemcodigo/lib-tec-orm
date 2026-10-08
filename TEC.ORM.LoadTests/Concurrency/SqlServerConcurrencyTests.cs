using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TEC.Cqrs.Persistence;
using TEC.ORM.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.LoadTests.Infrastructure;
using TEC.ORM.Queries;

namespace TEC.ORM.LoadTests.Concurrency;

/// <summary>
/// Disputa real no SQL Server: corrida pelo mesmo e-mail único, atualizações concorrentes da mesma linha (rowversion),
/// exclusões simultâneas e transferências em transação. O ORM precisa devolver os códigos padronizados (conflito,
/// concorrência, não encontrado) e o banco precisa terminar consistente. Pulados sem SQL Server.
/// </summary>
[Category(TestCategories.LoadCi)]
[NotInParallel(LoadSettings.Database)]
public class SqlServerConcurrencyTests
{
    private const int Workers = 32;

    private static async Task WithBatchAsync(Func<ServiceProvider, Guid, Task> test)
    {
        await LoadDatabase.RequireAsync();
        await using var provider = LoadDatabase.Build();
        var batch = LoadDatabase.NewBatch();
        try
        {
            await test(provider, batch);
        }
        finally
        {
            await LoadDatabase.PurgeAsync(provider, batch);
        }
    }

    private static async Task<T> InScopeAsync<T>(IServiceProvider provider, int worker, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestUser>().As(worker);
        return await action(scope.ServiceProvider);
    }

    [Test]
    public async Task UniqueEmail_Race_ExactlyOneWinsAndTheRestAreConflicts() => await WithBatchAsync(async (provider, batch) =>
    {
        string email = $"disputa-{batch:N}@carga";
        using var gate = new Barrier(Workers);

        var codes = await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
        {
            gate.SignalAndWait();
            return await InScopeAsync(provider, worker, async services =>
            {
                var result = await services.GetRequiredService<IOrmRepository<Account, Guid>>()
                    .CreateAsync(new Account { Batch = batch, Name = $"Disputa {worker}", Email = email, Category = worker });
                return result.Error?.Code ?? "ok";
            });
        })));

        await Assert.That(codes.Count(c => c == "ok")).IsEqualTo(1);
        await Assert.That(codes.Where(c => c != "ok").Distinct()).IsEquivalentTo(new[] { OrmErrors.ConflictCode });
        var rows = await InScopeAsync(provider, 0, services => services.GetRequiredService<IOrmRepository<Account, Guid>>().CountAsync(c => c.Batch == batch));
        await Assert.That(rows.Value).IsEqualTo(1L);
    });

    [Test]
    public async Task OptimisticConcurrency_ReadModifyWriteWithRetry_LosesNoUpdate() => await WithBatchAsync(async (provider, batch) =>
    {
        const int incrementsPerWorker = 15;
        var id = await InScopeAsync(provider, 0, async services =>
            (await services.GetRequiredService<IOrmRepository<Account, Guid>>()
                .CreateAsync(new Account { Batch = batch, Name = "Contador", Email = $"contador-{batch:N}@carga" })).Value.Id);
        long conflicts = 0;
        var unexpected = new ConcurrentBag<string>();

        await Parallel.ForAsync(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (worker, _) =>
        {
            for (int i = 0; i < incrementsPerWorker; i++)
            {
                for (int attempt = 0; ; attempt++)
                {
                    // Lê, incrementa e grava num escopo novo; versão antiga (rowversion) = ORM_CONCORRENCIA, e o worker tenta de novo
                    string? code = await InScopeAsync(provider, worker, async services =>
                    {
                        var accounts = services.GetRequiredService<IOrmRepository<Account, Guid>>();
                        var account = (await accounts.GetByIdAsync(id)).Value;
                        account.Balance += 1;
                        return (await accounts.UpdateAsync(account)).Error?.Code;
                    });
                    if (code is null)
                        break;
                    if (code != OrmErrors.ConcurrencyCode || attempt > 500)
                    {
                        unexpected.Add(code);
                        break;
                    }
                    Interlocked.Increment(ref conflicts);
                }
            }
        });

        var final = await InScopeAsync(provider, 0, services => services.GetRequiredService<IOrmRepository<Account, Guid>>().GetByIdAsync(id));
        LoadSettings.Report("Concorrência otimista (rowversion)", string.Create(CultureInfo.InvariantCulture,
            $"{Workers} workers × {incrementsPerWorker} incrementos na mesma linha · conflitos detectados e repetidos: {conflicts:N0}{Environment.NewLine}"));

        await Assert.That(unexpected).IsEmpty();
        await Assert.That(final.Value.Balance).IsEqualTo((decimal)(Workers * incrementsPerWorker));
        await Assert.That(conflicts).IsGreaterThan(0L).Because("a disputa precisa ter acontecido para o teste provar algo");
    });

    [Test]
    public async Task SoftDelete_Race_ExactlyOneDeletes() => await WithBatchAsync(async (provider, batch) =>
    {
        var id = await InScopeAsync(provider, 0, async services =>
            (await services.GetRequiredService<IOrmRepository<Account, Guid>>()
                .CreateAsync(new Account { Batch = batch, Name = "Alvo", Email = $"alvo-{batch:N}@carga" })).Value.Id);
        using var gate = new Barrier(Workers);

        var codes = await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
        {
            gate.SignalAndWait();
            return await InScopeAsync(provider, worker, async services =>
                (await services.GetRequiredService<IOrmRepository<Account, Guid>>().DeleteAsync(id)).Error?.Code ?? "ok");
        })));

        await Assert.That(codes.Count(c => c == "ok")).IsEqualTo(1);
        await Assert.That(codes.Where(c => c != "ok").All(c => c is OrmErrors.NotFoundCode or OrmErrors.ConcurrencyCode)).IsTrue()
            .Because(string.Join(", ", codes.Distinct()));

        // Quem excluiu foi o vencedor: a autoria da exclusão é dele, não de quem perdeu a corrida
        var deletedBy = await InScopeAsync(provider, 0, async services => (await services.GetRequiredService<IOrmQueryExecutor>()
            .QuerySingleOrDefaultAsync<string>(SqlQuery.Interpolated("carga.excluido-por", $"SELECT DeletedBy FROM loadtest.Accounts WHERE Id = {id} AND IsDeleted = 1"))).Value);
        await Assert.That(deletedBy).StartsWith("oid-worker-");
    });

    [Test]
    public async Task UnitOfWork_ConcurrentTransfers_PreserveTheTotal() => await WithBatchAsync(async (provider, batch) =>
    {
        const int accounts = 8, transfersPerWorker = 10;
        var ids = new Guid[accounts];
        for (int a = 0; a < accounts; a++)
        {
            int index = a;
            ids[a] = await InScopeAsync(provider, 0, async services =>
                (await services.GetRequiredService<IOrmRepository<Account, Guid>>()
                    .CreateAsync(new Account { Batch = batch, Name = $"Account {index}", Email = $"t{index}-{batch:N}@carga", Balance = 1_000m })).Value.Id);
        }
        long committed = 0, retried = 0;
        var unexpected = new ConcurrentBag<string>();

        await Parallel.ForAsync(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (worker, _) =>
        {
            var random = new Random(worker);
            for (int t = 0; t < transfersPerWorker; t++)
            {
                int from = random.Next(accounts), to = (from + 1 + random.Next(accounts - 1)) % accounts;
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    // Débito e crédito na mesma transação (IUnitOfWork do TEC.Cqrs): ou os dois, ou nenhum
                    string? code = await InScopeAsync(provider, worker, async services =>
                    {
                        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
                        var accounts = services.GetRequiredService<IOrmRepository<Account, Guid>>();
                        await unitOfWork.BeginTransactionAsync(CancellationToken.None);
                        // Leitura também pode ser vítima de deadlock (ORM_CONCORRENCIA): desfaz e tenta de novo
                        var debit = await accounts.GetByIdAsync(ids[from]);
                        var credit = debit.IsSuccess ? await accounts.GetByIdAsync(ids[to]) : debit;
                        string? failure = credit.Error?.Code;
                        if (failure is null)
                        {
                            debit.Value.Balance -= 10m;
                            credit.Value.Balance += 10m;
                            var first = await accounts.UpdateAsync(debit.Value);
                            var second = first.IsSuccess ? await accounts.UpdateAsync(credit.Value) : first;
                            failure = second.Error?.Code;
                        }
                        if (failure is not null)
                        {
                            await unitOfWork.RollbackAsync(CancellationToken.None);
                            return failure;
                        }
                        await unitOfWork.CommitAsync(CancellationToken.None);
                        return null;
                    });
                    if (code is null)
                    {
                        Interlocked.Increment(ref committed);
                        break;
                    }
                    if (code != OrmErrors.ConcurrencyCode)
                    {
                        unexpected.Add(code);
                        break;
                    }
                    Interlocked.Increment(ref retried);
                }
            }
        });

        var total = await InScopeAsync(provider, 0, async services => (await services.GetRequiredService<IOrmQueryExecutor>()
            .ExecuteScalarAsync<decimal>(SqlQuery.Interpolated("carga.total", $"SELECT SUM(Balance) FROM loadtest.Accounts WHERE Batch = {batch}"))).Value);
        LoadSettings.Report("Transferências em transação", string.Create(CultureInfo.InvariantCulture,
            $"{Workers} workers × {transfersPerWorker} transferências entre {accounts} contas · confirmadas: {committed:N0} · repetidas (concorrência/deadlock): {retried:N0}{Environment.NewLine}"));

        await Assert.That(unexpected).IsEmpty();
        await Assert.That(committed).IsEqualTo((long)Workers * transfersPerWorker);
        await Assert.That(total).IsEqualTo(accounts * 1_000m);
    });
}
