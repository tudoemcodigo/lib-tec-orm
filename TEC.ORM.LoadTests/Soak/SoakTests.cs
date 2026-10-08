using System.Diagnostics;
using System.Globalization;
using System.Text;
using TEC.ORM.LoadTests.Infrastructure;

namespace TEC.ORM.LoadTests.Soak;

/// <summary>
/// Soak: carga mista contínua no SQL Server por minutos, verificando que memória, handles, sessões no servidor e vazão ficam
/// estáveis (sem vazamento de contextos, conexões ou rastreadores, nem degradação). Duração em TEC_CARGA_SOAK_SEGUNDOS
/// (padrão 120 s) e workers em TEC_CARGA_CONCORRENCIA (padrão 32).
/// </summary>
[Explicit]
[Category(TestCategories.LoadHeavy)]
[NotInParallel(TestCategories.LoadHeavy)]
public class SoakTests
{
    private sealed record Sample(double Seconds, long Operations, long Failures, long RetainedBytes, int Handles, int? Sessions);

    [Test]
    public async Task MixedWorkload_MemoryHandlesConnectionsAndThroughputStayStable()
    {
        await LoadDatabase.RequireAsync();
        var duration = TimeSpan.FromSeconds(LoadSettings.SoakSeconds);
        var interval = TimeSpan.FromSeconds(Math.Clamp(LoadSettings.SoakSeconds / 24.0, 2, 30));
        int workers = LoadSettings.Concurrency;

        await using var provider = LoadDatabase.Build();
        var batch = LoadDatabase.NewBatch();
        var metrics = new LoadMetrics();
        var samples = new List<Sample>();
        var watch = Stopwatch.StartNew();

        try
        {
            using var stop = new CancellationTokenSource(duration);
            var run = new Workload(provider, batch).RunAsync(workers, metrics, stop.Token);
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(interval, stop.Token); }
                catch (OperationCanceledException) { break; }
                samples.Add(new Sample(watch.Elapsed.TotalSeconds, metrics.Operations, metrics.Failures, MemoryProbe.RetainedBytes(),
                    MemoryProbe.HandleCount(), await LoadDatabase.ServerSessionsAsync(provider)));
            }
            await run;
        }
        finally
        {
            await LoadDatabase.PurgeAsync(provider, batch);
        }

        await Assert.That(samples.Count).IsGreaterThanOrEqualTo(8).Because("o soak precisa de amostras suficientes (aumente TEC_CARGA_SOAK_SEGUNDOS)");

        var rates = samples.Select((s, i) => i == 0
            ? s.Operations / s.Seconds
            : (s.Operations - samples[i - 1].Operations) / (s.Seconds - samples[i - 1].Seconds)).ToArray();

        // Descarta o primeiro quarto (aquecimento: JIT, pools, cache de modelo e de consultas do EF Core) e compara o início com
        // o fim da janela estável
        int skip = samples.Count / 4;
        int third = Math.Max((samples.Count - skip) / 3, 1);
        var first = samples.Skip(skip).Take(third).ToList();
        var last = samples.TakeLast(third).ToList();
        double firstThroughput = rates.Skip(skip).Take(third).Average();
        double lastThroughput = rates.TakeLast(third).Average();
        long firstMemory = Median(first.Select(s => s.RetainedBytes));
        long lastMemory = Median(last.Select(s => s.RetainedBytes));

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Duração: {duration.TotalSeconds:F0} s · workers: {workers} · operações: {metrics.Operations:N0} · falhas: {metrics.Failures:N0}");
        report.AppendLine("| t (s) | operações | memória retida | handles | sessões no servidor |");
        report.AppendLine("|---:|---:|---:|---:|---:|");
        foreach (var s in samples)
            report.AppendLine(CultureInfo.InvariantCulture, $"| {s.Seconds:F0} | {s.Operations:N0} | {MemoryProbe.Megabytes(s.RetainedBytes)} | {s.Handles} | {s.Sessions?.ToString(CultureInfo.InvariantCulture) ?? "n/d"} |");
        report.AppendLine(CultureInfo.InvariantCulture, $"Vazão: {firstThroughput:N0} → {lastThroughput:N0} op/s · memória (mediana): {MemoryProbe.Megabytes(firstMemory)} → {MemoryProbe.Megabytes(lastMemory)}");
        report.AppendLine().Append(metrics.ToMarkdown(watch.Elapsed));
        LoadSettings.Report("Soak no SQL Server (carga mista)", report.ToString());

        await Assert.That(metrics.Failures).IsEqualTo(0L).Because(report.ToString());
        await Assert.That(lastMemory - firstMemory).IsLessThan(Math.Max(16L * 1024 * 1024, firstMemory / 5)).Because(report.ToString());
        await Assert.That(last[^1].Handles - first[0].Handles).IsLessThan(200).Because(report.ToString());
        await Assert.That(lastThroughput).IsGreaterThan(firstThroughput * 0.6).Because(report.ToString());
        // Conexões físicas estáveis: o pool não cresce ao longo do tempo (conexão não devolvida apareceria aqui)
        if (first[0].Sessions is { } firstSessions && last[^1].Sessions is { } lastSessions)
            await Assert.That(lastSessions).IsLessThanOrEqualTo(Math.Max(firstSessions * 2, firstSessions + 10)).Because(report.ToString());
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
