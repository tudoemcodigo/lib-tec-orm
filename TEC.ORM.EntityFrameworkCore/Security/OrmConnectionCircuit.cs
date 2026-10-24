using System.Data.Common;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Diagnostics;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Internal;

namespace TEC.ORM.EntityFrameworkCore.Security;

internal sealed class OrmConnectionException(Error error) : InvalidOperationException("Conexão recusada: " + error.Code)
{
    public Error Error { get; } = error;
}

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

    public bool IsOpen => _state.CircuitState is CircuitState.Open or CircuitState.Isolated;

    public static OrmConnectionCircuit? Create(OrmOptions options, OrmConnectionKind kind, TimeProvider? time, ILogger logger, ISqlDialect? dialect = null) =>
        options.CircuitBreaker is { Enabled: true } circuit
            ? new OrmConnectionCircuit(circuit, kind == OrmConnectionKind.ReadOnly ? "leitura" : "escrita", time ?? TimeProvider.System, logger, dialect ?? options.Dialect)
            : null;

    public async Task OpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await _pipeline.ExecuteAsync(static async (state, ct) =>
            {
                await state.Connection.OpenAsync(ct).ConfigureAwait(false);
            }, (Circuit: this, Connection: connection), cancellationToken).ConfigureAwait(false);
        }
        catch (BrokenCircuitException)
        {
            throw Rejected();
        }
    }

    public void Open(DbConnection connection)
    {
        try
        {
            _pipeline.Execute(static state =>
            {
                state.Connection.Open();
            }, (Circuit: this, Connection: connection));
        }
        catch (BrokenCircuitException)
        {
            throw Rejected();
        }
    }

    private bool CountsAsFailure(Exception exception)
    {
        if (_dialect?.ExceptionTranslator is { } translator && translator.IsConnectionFailure(exception))
            return true;

        return exception is DbException or TimeoutException;
    }

    private OrmConnectionException Rejected()
    {
        OrmLog.CircuitRejected(_logger, _kind);
        return new OrmConnectionException(OrmErrors.ConnectionUnavailable());
    }
}
