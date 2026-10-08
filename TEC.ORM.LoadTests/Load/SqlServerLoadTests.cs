using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TEC.ORM.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.LoadTests.Infrastructure;
using TEC.ORM.Queries;

namespace TEC.ORM.LoadTests.Load;

/// <summary>
/// Carga mista no SQL Server real (CRUD, listagem com projeção, buscas, agregações Dapper e transações), cada operação num
/// escopo de DI novo, como numa API. Fumaça no CI (com banco); carga sustentada e tempestade no pool de conexões sob demanda.
/// </summary>
public class SqlServerLoadTests
{
    [Test]
    [Category(TestCategories.LoadCi)]
    [NotInParallel(LoadSettings.Database)]
    public async Task SmokeLoad_MixedOperations_NoErrorsAndNothingSensitiveLogged()
    {
        await LoadDatabase.RequireAsync();
        var logs = new CapturingLoggerProvider();
        await using var provider = LoadDatabase.Build(logs);
        var batch = LoadDatabase.NewBatch();
        var metrics = new LoadMetrics();
        var duration = TimeSpan.FromSeconds(3);

        try
        {
            using var stop = new CancellationTokenSource(duration);
            await new Workload(provider, batch).RunAsync(16, metrics, stop.Token);
        }
        finally
        {
            await LoadDatabase.PurgeAsync(provider, batch);
        }

        string report = metrics.ToMarkdown(duration);
        LoadSettings.Report("Fumaça: carga mista no SQL Server (16 workers, 3 s)", report);

        await Assert.That(metrics.Failures).IsEqualTo(0L).Because(report);
        await Assert.That(metrics.Operations).IsGreaterThan(100L).Because(report);
        foreach (string scenario in Workload.Scenarios.Except(["transacao", "delete"]))
            await Assert.That(metrics.Outcomes.ContainsKey($"{scenario}:ok")).IsTrue().Because($"cenário {scenario} não exercitado");
        await Assert.That(logs.AllText).DoesNotContain(LoadDatabase.Password);
    }

    [Test]
    [Explicit]
    [Category(TestCategories.LoadHeavy)]
    [NotInParallel(TestCategories.LoadHeavy)]
    public async Task SustainedLoad_MixedOperations_ErrorRateLatencyAndResourcesStayWithinLimits()
    {
        await LoadDatabase.RequireAsync();
        await using var provider = LoadDatabase.Build();
        var batch = LoadDatabase.NewBatch();
        var metrics = new LoadMetrics();
        var duration = TimeSpan.FromSeconds(LoadSettings.LoadSeconds);
        int workers = LoadSettings.Concurrency;

        long memoryBefore = MemoryProbe.RetainedBytes();
        int handlesBefore = MemoryProbe.HandleCount();
        int? peakSessions = null;
        var watch = Stopwatch.StartNew();
        try
        {
            using var stop = new CancellationTokenSource(duration);
            var run = new Workload(provider, batch).RunAsync(workers, metrics, stop.Token);
            while (!run.IsCompleted)
            {
                await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
                if (await LoadDatabase.ServerSessionsAsync(provider) is { } sessions)
                    peakSessions = Math.Max(peakSessions ?? 0, sessions);
            }
            await run;
        }
        finally
        {
            watch.Stop();
            await LoadDatabase.PurgeAsync(provider, batch);
        }

        long memoryAfter = MemoryProbe.RetainedBytes();
        double errorRate = metrics.Operations == 0 ? 1 : (double)metrics.Failures / metrics.Operations;
        string report = string.Create(CultureInfo.InvariantCulture,
            $"Duração: {watch.Elapsed.TotalSeconds:F0} s · workers: {workers} · taxa de erro: {errorRate:P3} · memória retida: {MemoryProbe.Megabytes(memoryBefore)} → {MemoryProbe.Megabytes(memoryAfter)} · handles: {handlesBefore} → {MemoryProbe.HandleCount()} · pico de sessões no servidor: {peakSessions?.ToString(CultureInfo.InvariantCulture) ?? "n/d (sem VIEW SERVER STATE)"}{Environment.NewLine}{Environment.NewLine}")
            + metrics.ToMarkdown(watch.Elapsed);
        LoadSettings.Report("Carga sustentada no SQL Server", report);

        await Assert.That(errorRate).IsLessThanOrEqualTo(0.001).Because(report);
        await Assert.That(metrics.Percentile(0.99)).IsLessThan(2_000).Because(report);
        await Assert.That(memoryAfter - memoryBefore).IsLessThan(128L * 1024 * 1024).Because(report);
        // Dois pools (escrita e leitura, ApplicationIntent diferente), até 100 conexões cada: nenhuma conexão vazada além disso
        if (peakSessions is { } peak)
            await Assert.That(peak).IsLessThanOrEqualTo(201).Because(report);
    }

    [Test]
    [Explicit]
    [Category(TestCategories.LoadHeavy)]
    [NotInParallel(TestCategories.LoadHeavy)]
    public async Task ConnectionPoolStorm_SlowQueriesBeyondThePool_FailCleanlyAndRecover()
    {
        // Mais consultas lentas simultâneas do que conexões no pool (Max Pool Size padrão = 100): cada uma segura a conexão até
        // o tempo limite do comando; quem não consegue conexão espera o Connect Timeout (15 s) e falha. Nenhuma exceção pode
        // escapar do ORM, e o serviço volta ao normal em seguida. WAITFOR segura a conexão sem gastar CPU do servidor (por isso a
        // verificação de somente leitura fica desligada só neste container).
        // Pool esgotado é ORM_CONEXAO_INDISPONIVEL (infraestrutura), nunca ORM_FALHA (falha de programação com stack trace).
        await LoadDatabase.RequireAsync();
        await using var provider = LoadDatabase.Build(configure: o =>
        {
            o.CommandTimeoutSeconds = 20;
            o.EnforceReadOnlyQueries = false;
        });
        const int storm = 130;
        var codes = new ConcurrentDictionary<string, int>();
        var escaped = new ConcurrentBag<string>();

        var watch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, storm).Select(i => Task.Run(async () =>
        {
            try
            {
                await using var scope = provider.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>().ExecuteScalarAsync<int>(SqlQuery.Create("carga.lenta",
                    "WAITFOR DELAY '00:00:30'; SELECT 1"));
                codes.AddOrUpdate(result.Error?.Code ?? "ok", 1, (_, n) => n + 1);
            }
            catch (Exception exception)
            {
                escaped.Add(exception.GetType().Name);
            }
        })));
        var stormTime = watch.Elapsed;

        // Recuperação: leituras normais voltam a responder
        watch.Restart();
        var recovered = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>().ExecuteScalarAsync<int>(SqlQuery.Create("carga.um", "SELECT 1"))).IsSuccess;
        })));
        var recoveryTime = watch.Elapsed;

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"{storm} consultas lentas simultâneas · tempestade: {stormTime.TotalSeconds:F1} s · recuperação (50 leituras): {recoveryTime.TotalMilliseconds:F0} ms");
        report.AppendLine("Resultados: " + string.Join(" · ", codes.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} = {c.Value}")));
        LoadSettings.Report("Tempestade no pool de conexões", report.ToString());

        await Assert.That(escaped).IsEmpty();
        await Assert.That(codes.Keys.All(c => c is "ok" or OrmErrors.TimeoutCode or OrmErrors.ConnectionUnavailableCode))
            .IsTrue().Because(report.ToString());
        await Assert.That(recovered.All(ok => ok)).IsTrue().Because(report.ToString());
        await Assert.That(recoveryTime).IsLessThan(TimeSpan.FromSeconds(30));
    }
}
