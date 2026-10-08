using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TEC.ORM.Abstractions;
using TEC.ORM.LoadTests.Infrastructure;
using TEC.ORM.Paging;

namespace TEC.ORM.LoadTests.Performance;

/// <summary>
/// Custo do TEC.ORM por operação em relação ao EF Core puro: bytes alocados a mais (métrica determinística, boa para pegar
/// regressões no CI, sem banco) e vazão. No SQL Server (sob demanda), a vazão precisa crescer com os workers.
/// </summary>
/// <remarks>
/// A alocação é medida na própria thread com o provedor InMemory, cujas operações assíncronas terminam de forma síncrona: não
/// sofre interferência de outros testes rodando em paralelo no processo.
/// </remarks>
[NotInParallel(LoadSettings.Exclusive)]
public class PerformanceTests
{
    private const int WarmUp = 300;
    private const int Iterations = 3_000;

    private sealed record Measurement(double BytesPerOperation, double OperationsPerSecond, bool Synchronous);

    [Test]
    [Category(TestCategories.LoadCi)]
    public async Task OrmOverheadPerOperation_StaysWithinBudget()
    {
        var orm = new InMemoryOrm();
        var user = new TestUser().As(1);
        await using (var seed = orm.NewContext(user))
        {
            seed.Accounts.AddRange(Enumerable.Range(0, 200).Select(i => new Account { Name = $"Account {i:D3}", Email = $"c{i}@carga", Category = i % 20, Balance = i }));
            await seed.SaveChangesAsync();
        }

        await using var context = orm.NewContext(user);
        var accounts = orm.Orm<Account, Guid>(context);
        var id = (await context.Accounts.AsNoTracking().FirstAsync()).Id;

        // Orçamentos com folga (cerca do dobro do medido no .NET 8 e no .NET 10: ~1,4 KB, ~4,3 KB, ~15,4 KB, ~15,4 KB e ~2,4 KB a
        // mais que o EF Core puro por operação): pegam regressões grosseiras no caminho do
        // ORM (reflexão por chamada, logs montados sem necessidade, cópias de listas), não medem a máquina
        (string Scenario, long Budget, Func<Task> Orm, Func<Task> Raw)[] scenarios =
        [
            ("GetByIdAsync", 3_000,
                () => accounts.GetByIdAsync(id),
                () => context.Accounts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id)),
            ("FindAsync (10 itens)", 9_000,
                () => accounts.FindAsync(c => c.Category == 3),
                () => context.Accounts.AsNoTracking().Where(c => c.Category == 3).OrderBy(c => c.Id).Take(1_001).ToListAsync()),
            ("ListAsync (página de 20, SortBy)", 32_000,
                () => accounts.ListAsync(new PageRequest(1, 20, "Name")),
                async () =>
                {
                    _ = await context.Accounts.AsNoTracking().LongCountAsync();
                    _ = await context.Accounts.AsNoTracking().OrderBy(c => c.Name).ThenBy(c => c.Id).Skip(0).Take(20).ToListAsync();
                }),
            ("ListAsync<TDto> (projeção)", 32_000,
                () => accounts.ListAsync<AccountSummaryDto>(new PageRequest(1, 20, "Name")),
                async () =>
                {
                    _ = await context.Accounts.AsNoTracking().LongCountAsync();
                    _ = await context.Accounts.AsNoTracking().OrderBy(c => c.Name).ThenBy(c => c.Id).Skip(0).Take(20)
                        .Select(AccountSummaryDto.Projection).ToListAsync();
                }),
            ("CountAsync", 5_000,
                () => accounts.CountAsync(c => c.Category == 3),
                () => context.Accounts.AsNoTracking().LongCountAsync(c => c.Category == 3)),
        ];

        var report = new StringBuilder("| Operação | bytes/op TEC.ORM | bytes/op EF Core | diferença | orçamento | op/s TEC.ORM | op/s EF Core |\n|---|---:|---:|---:|---:|---:|---:|\n");
        var results = new List<(string Scenario, double Overhead, long Budget, bool Synchronous)>();
        foreach (var (scenario, budget, ormCall, rawCall) in scenarios)
        {
            var withOrm = Measure(ormCall);
            var raw = Measure(rawCall);
            double overhead = withOrm.BytesPerOperation - raw.BytesPerOperation;
            results.Add((scenario, overhead, budget, withOrm.Synchronous && raw.Synchronous));
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {scenario} | {withOrm.BytesPerOperation:N0} | {raw.BytesPerOperation:N0} | {overhead:N0} | {budget:N0} | {withOrm.OperationsPerSecond:N0} | {raw.OperationsPerSecond:N0} |");
        }
        LoadSettings.Report("Custo do TEC.ORM por operação (EF Core InMemory, 1 thread)", report.ToString());

        foreach (var (scenario, overhead, budget, synchronous) in results)
        {
            await Assert.That(synchronous).IsTrue().Because($"{scenario}: a medição por thread exige operações síncronas no InMemory");
            await Assert.That(overhead).IsLessThan(budget).Because(report.ToString());
        }
    }

    private static Measurement Measure(Func<Task> operation)
    {
        for (int i = 0; i < WarmUp; i++)
            operation().GetAwaiter().GetResult();

        bool synchronous = true;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < Iterations; i++)
        {
            var task = operation();
            synchronous &= task.IsCompleted;
            task.GetAwaiter().GetResult();
        }
        var elapsed = Stopwatch.GetElapsedTime(start);
        return new Measurement((GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)Iterations, Iterations / elapsed.TotalSeconds, synchronous);
    }

    [Test]
    [Explicit]
    [Category(TestCategories.LoadHeavy)]
    [NotInParallel(TestCategories.LoadHeavy)]
    public async Task SqlServer_ThroughputScalesWithWorkers()
    {
        // Sem trava global no caminho do ORM (singletons, cache do segredo, pool): com vários workers a vazão cresce
        await LoadDatabase.RequireAsync();
        await using var provider = LoadDatabase.Build();
        var batch = LoadDatabase.NewBatch();
        var ids = new List<Guid>();
        try
        {
            for (int i = 0; i < 200; i++)
            {
                await using var scope = provider.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TestUser>().As(0);
                ids.Add((await scope.ServiceProvider.GetRequiredService<IOrmRepository<Account, Guid>>()
                    .CreateAsync(new Account { Batch = batch, Name = $"Account {i}", Email = $"p{i}-{batch:N}@carga" })).Value.Id);
            }

            var duration = TimeSpan.FromSeconds(5);
            int workers = Math.Min(Environment.ProcessorCount * 2, 64);
            var (single, singleP50) = await ThroughputAsync(provider, ids, 1, duration);
            var (parallel, parallelP50) = await ThroughputAsync(provider, ids, workers, duration);

            LoadSettings.Report("Vazão do GetByIdAsync no SQL Server (escopo por requisição)", string.Create(CultureInfo.InvariantCulture,
                $"1 worker: {single:N0} op/s (p50 {singleP50:F2} ms) · {workers} workers: {parallel:N0} op/s (p50 {parallelP50:F2} ms) · {parallel / single:F1}×{Environment.NewLine}"));

            if (Environment.ProcessorCount >= 4)
                await Assert.That(parallel).IsGreaterThan(single * 1.5);
        }
        finally
        {
            await LoadDatabase.PurgeAsync(provider, batch);
        }
    }

    private static async Task<(double OperationsPerSecond, double P50)> ThroughputAsync(IServiceProvider provider, List<Guid> ids, int workers, TimeSpan duration)
    {
        var metrics = new LoadMetrics();
        using var stop = new CancellationTokenSource(duration);
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
        {
            var random = new Random(w);
            while (!stop.IsCancellationRequested)
            {
                await metrics.MeasureAsync("ler", async () =>
                {
                    await using var scope = provider.CreateAsyncScope();
                    return (await scope.ServiceProvider.GetRequiredService<IOrmRepository<Account, Guid>>().GetByIdAsync(ids[random.Next(ids.Count)])).Error?.Code;
                });
            }
        })));
        return (metrics.Operations / watch.Elapsed.TotalSeconds, metrics.Percentile(0.5));
    }
}
