using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TEC.Vault.Abstractions;
using TEC.Vault.DependencyInjection;
using TEC.Vault.InMemory;
using TEC.ORM.SqlServer;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.Diagnostics;
using TEC.ORM.SqlServer.SoftDelete;
using TEC.ORM.Tests.Database.CodeFirst;

namespace TEC.ORM.Tests.Fakes;

/// <summary>Logger que guarda tudo o que foi escrito (mensagem + exceção), para provar que valores sensíveis não vazam.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, EventId EventId, string Text)> Entries { get; } = new();

    public string AllText => string.Join('\n', Entries.Select(e => e.Text));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public ILogger<T> CreateLogger<T>() => new Logger<T>(new LoggerFactory([this]));

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider.Entries.Enqueue((logLevel, eventId, formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception)));
    }
}

/// <summary>Relógio fixo.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Captura as Activities e medições do TEC.ORM de um alvo (orm.target), isolando testes que rodam em paralelo.</summary>
internal sealed class OrmTelemetryListener : IDisposable
{
    private readonly ActivityListener _activities;
    private readonly MeterListener _meter;

    public OrmTelemetryListener(string target)
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OrmDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("orm.target"), target))
                    Activities.Enqueue(activity);
            }
        };
        ActivitySource.AddActivityListener(_activities);

        _meter = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OrmDiagnostics.MeterName && instrument.Name == OrmDiagnostics.OperationDurationName)
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _meter.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
                copy[tag.Key] = tag.Value;
            if (Equals(copy.GetValueOrDefault("orm.target"), target))
                Measurements.Enqueue((value, copy));
        });
        _meter.Start();
    }

    public ConcurrentQueue<Activity> Activities { get; } = new();

    public ConcurrentQueue<(double Seconds, Dictionary<string, object?> Tags)> Measurements { get; } = new();

    public void Dispose()
    {
        _activities.Dispose();
        _meter.Dispose();
    }
}

/// <summary>Montagem dos objetos do TEC.ORM para testes unitários (sem banco real).</summary>
internal static class TestOrm
{
    public const string SecretName = "dbtecbase-sql";
    public const string ReadOnlySecretName = "dbtecbase-sql-leitura";

    public static OrmOptions Options(Action<OrmOptions>? configure = null)
    {
        var options = new OrmOptions { ConnectionSecretName = SecretName };
        configure?.Invoke(options);
        options.Validate();
        return options;
    }

    /// <summary>TEC.Vault em memória com os segredos informados.</summary>
    public static ISecretReader Secrets(params (string Name, string Value)[] secrets)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecVault(vault => vault.UseInMemory(o =>
        {
            o.AllowOutsideDevelopment = true;
            foreach (var (name, value) in secrets)
                o.InitialSecrets[name] = value;
        }));
        return services.BuildServiceProvider().GetRequiredService<ISecretReader>();
    }

    /// <summary>Contexto code first sobre o provedor InMemory do EF Core, com as convenções e o interceptor de exclusão lógica.</summary>
    public static CodeFirstContext InMemoryContext(TimeProvider? time = null, string? database = null) =>
        new(new DbContextOptionsBuilder<CodeFirstContext>()
            .UseInMemoryDatabase(database ?? Guid.NewGuid().ToString("N"))
            .AddInterceptors(new SoftDeleteInterceptor(time ?? TimeProvider.System))
            .Options);

    public static OrmRepository<TEntity, TKey> Orm<TEntity, TKey>(DbContext context, OrmOptions? options = null,
        ILogger<OrmOperationRunner>? logger = null)
        where TEntity : class, ORM.Entities.IEntity<TKey>, ORM.Entities.ISoftDelete
        where TKey : notnull, IEquatable<TKey>
    {
        options ??= Options();
        return new OrmRepository<TEntity, TKey>(context, new OrmOperationRunner(logger ?? NullLogger<OrmOperationRunner>.Instance, options), options);
    }
}
