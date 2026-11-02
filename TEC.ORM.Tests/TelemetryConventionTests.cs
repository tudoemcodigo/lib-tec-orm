using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Diagnostics;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests;

/// <summary>Trava a convenção de telemetria da Doutrina (CMP-OBS-06/07): nomes <c>tec.orm.*</c> e EventId na faixa 8000-8999.</summary>
public class TelemetryConventionTests
{
    private const int FirstEventId = 8000;
    private const int LastEventId = 8999;

#pragma warning disable CS0618
    private static readonly string[] LegacyNames = [OrmDiagnostics.LegacyOperationDurationName, OrmDiagnostics.LegacyCircuitStateChangesName];
#pragma warning restore CS0618

    private static List<string> PublishedInstruments()
    {
        _ = OrmDiagnostics.OperationDuration;   // força o registro dos instrumentos do Meter
        var names = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == OrmDiagnostics.MeterName)
                    names.Add(instrument.Name);
            }
        };
        listener.Start();   // Start reenvia os instrumentos já criados
        return names;
    }

    [Test]
    public async Task Every_instrument_starts_with_tec_orm_except_the_deprecated_aliases()
    {
        var names = PublishedInstruments();

        await Assert.That(names).Contains(OrmDiagnostics.OperationDurationName);
        await Assert.That(names).Contains(OrmDiagnostics.CircuitStateChangesName);
        foreach (var name in names.Except(LegacyNames))
            await Assert.That(name.StartsWith("tec.orm.", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Deprecated_aliases_coexist_for_one_minor()
    {
        var names = PublishedInstruments();

        foreach (var legacy in LegacyNames)
            await Assert.That(names).Contains(legacy);
        await Assert.That(OrmDiagnostics.Meter.Name).IsEqualTo("TEC.ORM");
        await Assert.That(OrmDiagnostics.ActivitySourceName).IsEqualTo("TEC.ORM");
    }

    [Test]
    public async Task Every_logger_message_event_id_is_in_the_8000_range_and_unique()
    {
        var assemblies = new[]
        {
            typeof(OrmDiagnostics).Assembly,
            typeof(TEC.ORM.SqlServer.SqlServerDialect).Assembly,
            typeof(TEC.ORM.PostgreSql.Dialect.PostgreSqlDialect).Assembly,
            typeof(TEC.ORM.MySql.Dialect.MySqlDialect).Assembly,
            typeof(TEC.ORM.Oracle.Dialect.OracleDialect).Assembly
        };

        var ids = assemblies
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetCustomAttributes<LoggerMessageAttribute>())
            .Select(a => a.EventId)
            .ToList();

        await Assert.That(ids.Count).IsGreaterThan(0);
        foreach (int id in ids)
            await Assert.That(id is >= FirstEventId and <= LastEventId).IsTrue();
        await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Count);
    }

    [Test]
    public async Task New_and_deprecated_duration_instruments_receive_the_same_measurement_with_unchanged_tags()
    {
        string target = "Alvo" + Guid.NewGuid().ToString("N")[..8];
        using var telemetry = new OrmTelemetryListener(target);
        var runner = new OrmOperationRunner(new CapturingLoggerProvider().CreateLogger<OrmOperationRunner>(), TestOrm.Options());
        var operation = new OrmOperation(OrmDiagnostics.EntityFrameworkProvider, "update", target, isWrite: true);

        await runner.ExecuteAsync(operation, _ => Task.FromResult(TEC.Core.Common.Results.Result<string>.Success("ok")), CancellationToken.None);

        var current = telemetry.Measurements.Single();
        var legacy = telemetry.LegacyMeasurements.Single();
        await Assert.That(legacy.Seconds).IsEqualTo(current.Seconds);
        await Assert.That(legacy.Tags.OrderBy(t => t.Key).Select(t => (t.Key, t.Value)))
            .IsEquivalentTo(current.Tags.OrderBy(t => t.Key).Select(t => (t.Key, t.Value)));
        await Assert.That(current.Tags.Keys.Order())
            .IsEquivalentTo(new[] { "db.system.name", "orm.operation", "orm.provider", "orm.target" }.Order());
    }
}
