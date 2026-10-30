using System.Data.Common;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Diagnostics;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Internal;

namespace TEC.ORM.EntityFrameworkCore.Security;

/// <summary>
/// Circuit breaker da abertura de conexões de um contexto e tipo de conexão (<see cref="OrmOptions.CircuitBreaker"/>).
/// Só envolve o <c>Open</c>: nenhum comando é executado nem repetido aqui.
/// </summary>
/// <remarks>
/// Contam como falha: exceção do driver na abertura (<see cref="DbException"/>), falha de conexão reconhecida pelo dialeto,
/// tempo esgotado e esgotamento do pool. Com o circuito aberto a abertura lança <see cref="OrmConnectionException"/> com
/// <c>ORM_CONEXAO_INDISPONIVEL</c>, sem tocar no banco. Thread-safe.
/// </remarks>
internal sealed class OrmConnectionCircuit
{
    private readonly ResiliencePipeline _pipeline;
    private readonly CircuitBreakerStateProvider _state = new();
    private readonly string _kind;
    private readonly ILogger _logger;
    private readonly ISqlDialect? _dialect;

    private OrmConnectionCircuit(OrmCircuitBreakerOptions options, string kind, TimeProvider time, ILogger logger, ISqlDialect? dialect)
    {
        _kind = kind;
        _logger = logger;
        _dialect = dialect;
        _pipeline = new ResiliencePipelineBuilder { TimeProvider = time }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = options.FailureRatio,
                MinimumThroughput = options.MinimumThroughput,
                SamplingDuration = options.SamplingDuration,
                BreakDuration = options.BreakDuration,
                StateProvider = _state,
                // Para o Polly, o que não é falha conta como sucesso (e fecha o circuito na meia-abertura): na abertura de teste só
                // uma conexão aberta fecha o circuito; cancelamento ou qualquer outra exceção contam como falha
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is { } ex
                    && (CountsAsFailure(ex) || _state.CircuitState == CircuitState.HalfOpen)),
                OnOpened = args =>
                {
                    OrmDiagnostics.RecordCircuitState(_kind, OrmDiagnostics.CircuitOpen);
                    OrmLog.CircuitOpened(_logger, _kind, args.BreakDuration.TotalSeconds);
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    OrmDiagnostics.RecordCircuitState(_kind, OrmDiagnostics.CircuitHalfOpen);
                    OrmLog.CircuitHalfOpened(_logger, _kind);
                    return default;
                },
                OnClosed = _ =>
                {
                    OrmDiagnostics.RecordCircuitState(_kind, OrmDiagnostics.CircuitClosed);
                    OrmLog.CircuitClosed(_logger, _kind);
                    return default;
                }
            })
            .Build();
    }

    /// <summary>Indica se o circuito está aberto.</summary>
    public bool IsOpen => _state.CircuitState is CircuitState.Open or CircuitState.Isolated;

    /// <summary>Cria o circuito conforme as opções; <c>null</c> com o circuit breaker desligado.</summary>
    public static OrmConnectionCircuit? Create(OrmOptions options, OrmConnectionKind kind, TimeProvider? time, ILogger logger,
        ISqlDialect? dialect = null) =>
        options.CircuitBreaker is { Enabled: true } circuit
            ? new OrmConnectionCircuit(circuit, kind == OrmConnectionKind.ReadOnly ? "leitura" : "escrita", time ?? TimeProvider.System, logger,
                dialect ?? options.Dialect)
            : null;

    /// <summary>Abre a conexão pelo circuito.</summary>
    /// <exception cref="OrmConnectionException">Circuito aberto.</exception>
    public async Task OpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await _pipeline.ExecuteAsync(static async (state, ct) =>
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    await state.Connection.OpenAsync(ct).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    MarkPoolExhaustion(exception, state.Connection, started);
                    throw;
                }
            }, (Circuit: this, Connection: connection), cancellationToken).ConfigureAwait(false);
        }
        catch (BrokenCircuitException)
        {
            throw Rejected();
        }
    }

    /// <summary>Abre a conexão pelo circuito (caminho síncrono do EF Core).</summary>
    /// <exception cref="OrmConnectionException">Circuito aberto.</exception>
    public void Open(DbConnection connection)
    {
        try
        {
            _pipeline.Execute(static state =>
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    state.Connection.Open();
                }
                catch (Exception exception)
                {
                    MarkPoolExhaustion(exception, state.Connection, started);
                    throw;
                }
            }, (Circuit: this, Connection: connection));
        }
        catch (BrokenCircuitException)
        {
            throw Rejected();
        }
    }

    private bool CountsAsFailure(Exception exception) =>
        exception is DbException or TimeoutException || OrmConnectionFailures.IsMarked(exception) ||
        _dialect?.ExceptionTranslator.IsConnectionFailure(exception) == true;

    private static void MarkPoolExhaustion(Exception exception, DbConnection connection, long started)
    {
        if (OrmConnectionFailures.IsPoolExhaustion(exception, System.Diagnostics.Stopwatch.GetElapsedTime(started), connection.ConnectionTimeout))
            OrmConnectionFailures.Mark(exception);
    }

    private OrmConnectionException Rejected()
    {
        OrmLog.CircuitRejected(_logger, _kind);
        return new OrmConnectionException(OrmErrors.ConnectionUnavailable());
    }
}
