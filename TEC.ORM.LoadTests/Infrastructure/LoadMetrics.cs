using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TEC.ORM.LoadTests.Infrastructure;

/// <summary>Registra a primeira divergência (para a mensagem) e conta todas.</summary>
public sealed class Divergences
{
    private readonly ConcurrentQueue<string> _samples = new();
    private int _count;

    public int Count => _count;

    public void Add(string description)
    {
        if (Interlocked.Increment(ref _count) <= 5)
            _samples.Enqueue(description);
    }

    public override string ToString() => string.Join(Environment.NewLine, _samples);
}

/// <summary>
/// Latência e resultado de cada operação, por cenário: percentis (p50/p95/p99/máx.), vazão e contagem por código de erro.
/// Cada worker grava na própria lista (sem disputa); a consolidação é feita no fim.
/// </summary>
public sealed class LoadMetrics
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> _latencies = new();
    private readonly ConcurrentDictionary<string, long> _outcomes = new();

    /// <summary>Operações registradas.</summary>
    public long Operations => _outcomes.Values.Sum();

    /// <summary>Operações com falha (qualquer código de erro).</summary>
    public long Failures => _outcomes.Where(o => !o.Key.EndsWith(":ok", StringComparison.Ordinal)).Sum(o => o.Value);

    /// <summary>Contagem por "cenário:código" (ex.: <c>criar:ok</c>, <c>atualizar:ORM_CONCORRENCIA</c>).</summary>
    public IReadOnlyDictionary<string, long> Outcomes => _outcomes;

    /// <summary>Mede a operação e registra o resultado (<c>null</c> = sucesso, ou o código de erro).</summary>
    public async Task<string?> MeasureAsync(string scenario, Func<Task<string?>> operation)
    {
        long start = Stopwatch.GetTimestamp();
        string? error = await operation();
        Record(scenario, Stopwatch.GetTimestamp() - start, error);
        return error;
    }

    public void Record(string scenario, long elapsedTicks, string? error)
    {
        _latencies.GetOrAdd(scenario, _ => new ConcurrentQueue<long>()).Enqueue(elapsedTicks);
        _outcomes.AddOrUpdate($"{scenario}:{error ?? "ok"}", 1, (_, count) => count + 1);
    }

    /// <summary>Percentil de latência (ms) de um cenário, ou de todos.</summary>
    public double Percentile(double fraction, string? scenario = null)
    {
        var values = (scenario is null ? _latencies.Values.SelectMany(q => q) : _latencies.GetValueOrDefault(scenario) ?? new())
            .Order().ToArray();
        if (values.Length == 0)
            return 0;
        return values[Math.Clamp((int)(fraction * values.Length), 0, values.Length - 1)] * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>Tabela Markdown por cenário: operações, falhas e latências.</summary>
    public string ToMarkdown(TimeSpan duration)
    {
        var report = new StringBuilder();
        report.AppendLine("| Cenário | operações | op/s | falhas | p50 (ms) | p95 (ms) | p99 (ms) | máx. (ms) |");
        report.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var scenario in _latencies.Keys.Order(StringComparer.Ordinal))
        {
            long count = _latencies[scenario].Count;
            long failures = _outcomes.Where(o => o.Key.StartsWith(scenario + ":", StringComparison.Ordinal) && !o.Key.EndsWith(":ok", StringComparison.Ordinal))
                .Sum(o => o.Value);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {scenario} | {count:N0} | {count / duration.TotalSeconds:N0} | {failures:N0} | {Percentile(0.50, scenario):F1} | {Percentile(0.95, scenario):F1} | {Percentile(0.99, scenario):F1} | {Percentile(1.0, scenario):F1} |");
        }
        report.AppendLine(CultureInfo.InvariantCulture,
            $"| **total** | {Operations:N0} | {Operations / duration.TotalSeconds:N0} | {Failures:N0} | {Percentile(0.50):F1} | {Percentile(0.95):F1} | {Percentile(0.99):F1} | {Percentile(1.0):F1} |");

        var errors = _outcomes.Where(o => !o.Key.EndsWith(":ok", StringComparison.Ordinal)).OrderBy(o => o.Key, StringComparer.Ordinal).ToList();
        if (errors.Count > 0)
            report.AppendLine().AppendLine("Falhas por código: " + string.Join(" · ", errors.Select(e => $"{e.Key} = {e.Value:N0}")));
        return report.ToString();
    }
}
