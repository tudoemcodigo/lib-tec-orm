using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TEC.ORM.SqlServer.Diagnostics;

/// <summary>Rastreamento e métricas (OpenTelemetry) do TEC.ORM.</summary>
/// <remarks>
/// Segurança: traces e métricas nunca levam identificador do registro, SQL, valores de parâmetros nem dados da conexão
/// (costumam ser enviados a terceiros); o identificador fica só no log de auditoria. As dimensões são de baixa cardinalidade:
/// provedor, operação, alvo (nome da entidade ou da consulta) e código do erro.
/// </remarks>
/// <example>
/// <code>
/// // Com o TEC.Observability nada é preciso: o AddTecObservability exporta as fontes "TEC.*". Sem ele:
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(OrmDiagnostics.ActivitySourceName))
///     .WithMetrics(m => m.AddMeter(OrmDiagnostics.MeterName));
/// </code>
/// </example>
public static class OrmDiagnostics
{
    /// <summary>
    /// Nome do <see cref="System.Diagnostics.ActivitySource"/>. Cada operação gera uma <c>Activity</c> (Client)
    /// <c>"{operação} {alvo}"</c> com as tags <c>db.system.name</c>, <c>orm.provider</c>, <c>orm.operation</c>,
    /// <c>orm.target</c>, <c>orm.success</c>, <c>orm.retries</c> (só quando houve nova tentativa por falha transitória) e, em
    /// falha, <c>error.type</c> (o mesmo valor da métrica).
    /// </summary>
    public const string ActivitySourceName = "TEC.ORM";

    /// <summary>
    /// Nome do <see cref="System.Diagnostics.Metrics.Meter"/>. Instrumento <see cref="OperationDurationName"/> (histograma,
    /// segundos) com <c>db.system.name</c>, <c>orm.provider</c>, <c>orm.operation</c>, <c>orm.target</c> e, em falha,
    /// <c>error.type</c> (código de <c>OrmErrors</c>, ou <c>canceled</c>). A contagem por operação/resultado vem do histograma.
    /// Exportado sem configuração pelo <c>AddTecObservability</c> do TEC.Observability (prefixo <c>TEC.*</c>).
    /// </summary>
    public const string MeterName = "TEC.ORM";

    /// <summary>Histograma da duração das operações (segundos).</summary>
    public const string OperationDurationName = "orm.operation.duration";

    /// <summary>Provedor das operações de CRUD.</summary>
    public const string EntityFrameworkProvider = "entityframework";

    /// <summary>Provedor das leituras complexas.</summary>
    public const string DapperProvider = "dapper";

    /// <summary>Valor de <c>db.system.name</c> (convenção semântica do OpenTelemetry).</summary>
    public const string DbSystem = "microsoft.sql_server";

    /// <summary>Valor de <c>error.type</c> quando a operação é cancelada.</summary>
    public const string CanceledErrorType = "canceled";

    internal const string DbSystemTag = "db.system.name";
    internal const string ProviderTag = "orm.provider";
    internal const string OperationTag = "orm.operation";
    internal const string TargetTag = "orm.target";
    internal const string SuccessTag = "orm.success";
    internal const string ErrorTypeTag = "error.type";
    internal const string RetriesTag = "orm.retries";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    internal static readonly Meter Meter = new(MeterName, typeof(OrmDiagnostics).Assembly.GetName().Version?.ToString());

    internal static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        OperationDurationName, unit: "s", description: "Duração das operações do TEC.ORM.");

    /// <summary>Registra a duração de uma operação. <paramref name="errorType"/> só em falha.</summary>
    internal static void RecordOperation(string provider, string operation, string target, double seconds, string? errorType)
    {
        if (!OperationDuration.Enabled)
            return;

        var tags = new TagList
        {
            { DbSystemTag, DbSystem },
            { ProviderTag, provider },
            { OperationTag, operation },
            { TargetTag, target }
        };
        if (errorType is not null)
            tags.Add(ErrorTypeTag, errorType);
        OperationDuration.Record(seconds, tags);
    }
}
