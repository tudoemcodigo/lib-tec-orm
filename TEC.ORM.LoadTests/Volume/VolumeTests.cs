using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TEC.ORM.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.LoadTests.Infrastructure;
using TEC.ORM.Paging;
using TEC.ORM.Queries;
using TEC.ORM.Specifications;

namespace TEC.ORM.LoadTests.Volume;

/// <summary>
/// Grandes volumes no SQL Server: gravação em massa pelo ORM, paginação profunda, busca no limite de resultados, agregação,
/// leitura grande pelo Dapper e expurgo físico em lote. Quantidade em TEC_CARGA_LINHAS (padrão 50 mil).
/// </summary>
[Explicit]
[Category(TestCategories.LoadHeavy)]
[NotInParallel(TestCategories.LoadHeavy)]
public class VolumeTests
{
    [Test]
    public async Task LargeTable_PaginationFindLimitAggregationAndPurge()
    {
        await LoadDatabase.RequireAsync();
        int rows = LoadSettings.Rows;
        const int categories = 10, maxFind = 1_000;
        await using var provider = LoadDatabase.Build(configure: o => o.MaxFindResults = maxFind);
        var batch = LoadDatabase.NewBatch();
        var report = new StringBuilder();
        long baseline = MemoryProbe.RetainedBytes();

        try
        {
            // ---------- Gravação em massa: 16 workers, uma conta por "requisição" ----------
            var watch = Stopwatch.StartNew();
            await Parallel.ForAsync(0, rows, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, ct) =>
            {
                await using var scope = provider.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TestUser>().As(i % 16);
                var created = await scope.ServiceProvider.GetRequiredService<IOrmRepository<Account, Guid>>().CreateAsync(new Account
                {
                    Batch = batch,
                    Name = $"Account {i:D7}",
                    Email = $"v{i}-{batch:N}@carga",
                    Category = i % categories,
                    Balance = i % 1_000
                }, ct);
                if (created.IsFailure)
                    throw new InvalidOperationException($"Gravação {i} falhou: {created.Error!.Code}");
            });
            var writeTime = watch.Elapsed;
            report.AppendLine(CultureInfo.InvariantCulture, $"Gravação: {rows:N0} contas em {writeTime.TotalSeconds:F1} s ({rows / writeTime.TotalSeconds:N0} inserções/s, 16 workers)");

            await using var scope = provider.CreateAsyncScope();
            var accounts = scope.ServiceProvider.GetRequiredService<IOrmRepository<Account, Guid>>();
            var queries = scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>();
            var ofBatch = new Specification<Account>(c => c.Batch == batch);

            // ---------- Contagem e paginação profunda (primeira, do meio, última e além do fim) ----------
            await Assert.That((await accounts.CountAsync(ofBatch)).Value).IsEqualTo((long)rows);
            const int pageSize = 100;
            int lastPage = (rows + pageSize - 1) / pageSize;
            foreach (var (label, page) in new[] { ("primeira", 1), ("do meio", lastPage / 2), ("última", lastPage), ("além do fim", lastPage + 10) })
            {
                watch.Restart();
                var result = await accounts.ListAsync<AccountSummaryDto>(new PageRequest(page, pageSize, "Name"), ofBatch);
                var elapsed = watch.Elapsed;
                report.AppendLine(CultureInfo.InvariantCulture, $"Página {label} ({page:N0}): {elapsed.TotalMilliseconds:F0} ms, {result.Value.Items.Count} itens");

                int expected = page < lastPage ? pageSize : page == lastPage ? rows - (lastPage - 1) * pageSize : 0;
                await Assert.That(result.Value.Items.Count).IsEqualTo(expected);
                await Assert.That(result.Value.TotalItems).IsEqualTo((long)rows);
                if (expected > 0)
                    await Assert.That(result.Value.Items[0].Name).IsEqualTo($"Account {(page - 1) * pageSize:D7}");
                await Assert.That(elapsed).IsLessThan(TimeSpan.FromSeconds(10));
            }

            // ---------- Busca no limite: exatamente o máximo passa; um a mais é recusado sem trazer a tabela ----------
            string bound = $"Account {Math.Min(maxFind, rows):D7}";
            var atLimit = await accounts.FindAsync(c => c.Batch == batch && string.Compare(c.Name, bound) < 0);
            await Assert.That(atLimit.Value.Count).IsEqualTo(Math.Min(maxFind, rows));

            watch.Restart();
            var overLimit = await accounts.FindAsync(new Specification<Account>(c => c.Batch == batch).OrderByAscending(c => c.Name));
            var findTime = watch.Elapsed;
            report.AppendLine(CultureInfo.InvariantCulture, $"Busca acima do limite ({maxFind:N0}) em {rows:N0} linhas: {overLimit.Error?.Code ?? "accepted"} em {findTime.TotalMilliseconds:F0} ms");
            await Assert.That(overLimit.Error?.Code).IsEqualTo(rows > maxFind ? OrmErrors.TooManyResultsCode : null);
            await Assert.That(findTime).IsLessThan(TimeSpan.FromSeconds(10));

            // ---------- Agregação (Dapper) confere com o que foi gravado ----------
            watch.Restart();
            var totals = await queries.QueryAsync<(int Category, long Quantity, decimal Total)>(SqlQuery.Interpolated("carga.totais-por-categoria",
                $"SELECT Category, COUNT_BIG(*) AS Quantity, SUM(Balance) AS Total FROM loadtest.Accounts WHERE Batch = {batch} GROUP BY Category ORDER BY Category"));
            report.AppendLine(CultureInfo.InvariantCulture, $"Agregação por categoria: {watch.Elapsed.TotalMilliseconds:F0} ms");
            await Assert.That(totals.Value.Sum(t => t.Quantity)).IsEqualTo((long)rows);
            await Assert.That(totals.Value.Sum(t => t.Total)).IsEqualTo(Enumerable.Range(0, rows).Sum(i => (decimal)(i % 1_000)));

            // ---------- Leitura grande pelo Dapper: sem limite de linhas, tudo é carregado (documenta o custo) ----------
            long beforeRead = MemoryProbe.RetainedBytes();
            watch.Restart();
            var all = await queries.QueryAsync<AccountSummaryDto>(SqlQuery.Interpolated("carga.todas",
                $"SELECT Id, Name, Category, Balance FROM loadtest.Accounts WHERE Batch = {batch}"));
            var readTime = watch.Elapsed;
            long heldByResult = MemoryProbe.RetainedBytes() - beforeRead;
            report.AppendLine(CultureInfo.InvariantCulture, $"Dapper, {all.Value.Count:N0} linhas de uma vez: {readTime.TotalMilliseconds:F0} ms, resultado ocupa {MemoryProbe.Megabytes(heldByResult)} (não há limite de linhas nas leituras complexas)");
            await Assert.That(all.Value.Count).IsEqualTo(rows);
        }
        finally
        {
            // ---------- Expurgo físico em lote (o próprio TEC.ORM) ----------
            var watch = Stopwatch.StartNew();
            await LoadDatabase.PurgeAsync(provider, batch);
            report.AppendLine(CultureInfo.InvariantCulture, $"Purger físico (HardDeleteAsync por critério): {watch.Elapsed.TotalSeconds:F1} s");
            report.AppendLine(CultureInfo.InvariantCulture, $"Memória retida ao final: {MemoryProbe.Megabytes(MemoryProbe.RetainedBytes() - baseline)} acima do início");
            LoadSettings.Report($"Volume no SQL Server ({rows:N0} contas)", report.ToString());
        }

        await Assert.That(MemoryProbe.RetainedBytes() - baseline).IsLessThan(64L * 1024 * 1024).Because(report.ToString());
    }
}
