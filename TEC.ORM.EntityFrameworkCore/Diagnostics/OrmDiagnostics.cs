using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TEC.ORM.EntityFrameworkCore.Diagnostics;

/// <summary>Rastreamento e métricas (OpenTelemetry) do TEC.ORM.</summary>
public static class OrmDiagnostics
{
    /// <summary>Nome da fonte de atividades OpenTelemetry.</summary>
    public const string ActivitySourceName = "TEC.ORM";

    /// <summary>Nome do medidor de métricas OpenTelemetry.</summary>
    public const string MeterName = "TEC.ORM";

    /// <summary>Nome da métrica de duração de operação.</summary>
    public const string OperationDurationName = "orm.operation.duration";

    /// <summary>Nome da métrica de mudanças de estado de circuito.</summary>
    public const string CircuitStateChangesName = "orm.circuit.state_changes";

    /// <summary>Identificador do provedor EF Core.</summary>
    public const string EntityFrameworkProvider = "entityframework";

    /// <summary>Identificador do provedor Dapper.</summary>
    public const string DapperProvider = "dapper";

    /// <summary>Identificador de erro de cancelamento.</summary>
    public const string CanceledErrorType = "canceled";

    internal const string DbSystemTag = "db.system.name";
    internal const string ProviderTag = "orm.provider";
    internal const string OperationTag = "orm.operation";
    internal const string TargetTag = "orm.target";
    internal const string SuccessTag = "orm.success";
    internal const string ErrorTypeTag = "error.type";
    internal const string RetriesTag = "orm.retries";
    internal const string ConnectionTag = "orm.connection";
    internal const string CircuitStateTag = "orm.circuit.state";
    internal const string CircuitOpen = "open";
    internal const string CircuitHalfOpen = "half_open";
    internal const string CircuitClosed = "closed";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    internal static readonly Meter Meter = new(MeterName, typeof(OrmDiagnostics).Assembly.GetName().Version?.ToString());

    internal static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        OperationDurationName, unit: "s", description: "Duração das operações do TEC.ORM.");

    internal static readonly Counter<long> CircuitStateChanges = Meter.CreateCounter<long>(
        CircuitStateChangesName, unit: "{change}", description: "Mudanças de estado do circuit breaker da abertura de conexões.");

    internal static void RecordCircuitState(string connection, string state)
    {
        if (CircuitStateChanges.Enabled)
            CircuitStateChanges.Add(1, new KeyValuePair<string, object?>(ConnectionTag, connection), new KeyValuePair<string, object?>(CircuitStateTag, state));
    }

    internal static void RecordOperation(string dbSystem, string provider, string operation, string target, double seconds, string? errorType)
    {
        if (!OperationDuration.Enabled)
            return;

        var tags = new TagList
        {
            { DbSystemTag, dbSystem },
            { ProviderTag, provider },
            { OperationTag, operation },
            { TargetTag, target }
        };
        if (errorType is not null)
            tags.Add(ErrorTypeTag, errorType);

        OperationDuration.Record(seconds, tags);
    }
}
