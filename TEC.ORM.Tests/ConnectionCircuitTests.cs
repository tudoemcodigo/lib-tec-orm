using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using TEC.Core.Exceptions;
using TEC.ORM.Common;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.Internal;
using TEC.ORM.SqlServer.Security;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests;

public class ConnectionCircuitTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static OrmOptions Sensitive(Action<OrmCircuitBreakerOptions>? configure = null)
    {
        var options = TestOrm.Options(o =>
        {
            o.CircuitBreaker.MinimumThroughput = 2;
            o.CircuitBreaker.FailureRatio = 0.5;
            o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
        });
        configure?.Invoke(options.CircuitBreaker);
        return options;
    }

    [Test]
    [Arguments(-1)]
    [Arguments(40613)]
    [Arguments(18456)]
    public async Task Repeated_open_failures_open_circuit_and_reject_without_touching_database(int sqlError)
    {
        var clock = new Clock(Start);
        var logs = new CapturingLoggerProvider();
        var circuit = OrmConnectionCircuit.Create(Sensitive(), OrmConnectionKind.ReadWrite, clock, logs.CreateLogger<ConnectionCircuitTests>())!;
        var connection = new ScriptedConnection(() => throw SqlExceptionFactory.Create(sqlError));

        await Assert.That(async () => await circuit.OpenAsync(connection, default)).Throws<SqlException>();
        await Assert.That(async () => await circuit.OpenAsync(connection, default)).Throws<SqlException>();
        await Assert.That(circuit.IsOpen).IsTrue();

        var rejected = await Assert.That(async () => await circuit.OpenAsync(connection, default)).Throws<OrmConnectionException>();
        await Assert.That(rejected!.Error.Code).IsEqualTo(OrmErrors.ConnectionUnavailableCode);
        await Assert.That(() => circuit.Open(connection)).Throws<OrmConnectionException>();
        await Assert.That(connection.Opens).IsEqualTo(2);
        await Assert.That(logs.AllText).Contains("circuito da conexão escrita aberto");

        // Fim da pausa: abertura de teste com o banco de volta fecha o circuito
        connection.Behavior = () => { };
        clock.Now = Start.AddSeconds(31);
        await circuit.OpenAsync(connection, default);
        await Assert.That(circuit.IsOpen).IsFalse();
        await Assert.That(logs.AllText).Contains("fechado");
    }

    [Test]
    [Arguments("cancelada")]
    [Arguments("outra-excecao")]
    public async Task Probe_that_does_not_open_the_connection_keeps_circuit_open(string kind)
    {
        var clock = new Clock(Start);
        var circuit = OrmConnectionCircuit.Create(Sensitive(), OrmConnectionKind.ReadWrite, clock, NullLogger.Instance)!;
        var connection = new ScriptedConnection(() => throw SqlExceptionFactory.Create(-1));
        for (int i = 0; i < 2; i++)
            await Assert.That(async () => await circuit.OpenAsync(connection, default)).Throws<SqlException>();

        clock.Now = Start.AddSeconds(31);
        connection.Behavior = kind == "cancelada"
            ? () => throw new OperationCanceledException()
            : () => throw new InvalidOperationException("falha local");
        await Assert.That(async () => await circuit.OpenAsync(connection, default)).Throws<Exception>();

        // Sem conexão aberta na tentativa de teste: o circuito volta a abrir
        await Assert.That(circuit.IsOpen).IsTrue();
        await Assert.That(async () => await circuit.OpenAsync(connection, default)).Throws<OrmConnectionException>();
    }

    [Test]
    public async Task Programming_errors_do_not_open_circuit()
    {
        var circuit = OrmConnectionCircuit.Create(Sensitive(), OrmConnectionKind.ReadWrite, new Clock(Start), NullLogger.Instance)!;
        var connection = new ScriptedConnection(() => throw new InvalidOperationException("ConnectionString não definida"));

        for (int i = 0; i < 10; i++)
            await Assert.That(async () => await circuit.OpenAsync(connection, default)).Throws<InvalidOperationException>();

        await Assert.That(circuit.IsOpen).IsFalse();
        await Assert.That(connection.Opens).IsEqualTo(10);
    }

    [Test]
    public async Task Pool_exhaustion_counts_as_failure_and_is_marked()
    {
        // Esgotamento do pool: InvalidOperationException depois de esperar ~todo o Connect Timeout (1 s aqui)
        var circuit = OrmConnectionCircuit.Create(Sensitive(), OrmConnectionKind.ReadOnly, new Clock(Start), NullLogger.Instance)!;
        var connection = new ScriptedConnection(() =>
        {
            Thread.Sleep(950);
            throw new InvalidOperationException("Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool.");
        }) { Timeout = 1 };

        var first = await Assert.That(async () => await circuit.OpenAsync(connection, default)).Throws<InvalidOperationException>();
        await Assert.That(OrmExceptionTranslator.IsMarkedConnectionOpenFailure(first!)).IsTrue();
        await Assert.That(() => circuit.Open(connection)).Throws<InvalidOperationException>();

        await Assert.That(async () => await circuit.OpenAsync(connection, default)).Throws<OrmConnectionException>();
    }

    [Test]
    public async Task Dapper_connection_fails_fast_with_circuit_open()
    {
        Skip.When(TestDatabase.GlobalizationInvariant, TestDatabase.SqlClientRequiresIcu);
        // Porta fechada no loopback: recusa rápida, sem depender de servidor
        const string secret = "Server=tcp:127.0.0.1,1;Database=x;Encrypt=True;TrustServerCertificate=False;Connect Timeout=1;ConnectRetryCount=0";
        var options = Sensitive();
        var security = new OrmConnectionSecurity(TestOrm.Secrets((TestOrm.SecretName, secret)), options,
            new CapturingLoggerProvider().CreateLogger<OrmConnectionSecurity>(), new Clock(Start));

        for (int i = 0; i < 2; i++)
            await Assert.That((await security.OpenConnectionAsync(OrmConnectionKind.ReadOnly, default)).Error!.Code)
                .IsEqualTo(OrmErrors.ConnectionUnavailableCode);

        var watch = Stopwatch.StartNew();
        var rejected = await security.OpenConnectionAsync(OrmConnectionKind.ReadOnly, default);
        await Assert.That(rejected.Error!.Code).IsEqualTo(OrmErrors.ConnectionUnavailableCode);
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromMilliseconds(500));
    }

    [Test]
    public async Task Disabled_circuit_is_not_created()
    {
        await Assert.That(OrmConnectionCircuit.Create(Sensitive(c => c.Enabled = false), OrmConnectionKind.ReadWrite, null,
            NullLogger.Instance)).IsNull();
    }

    [Test]
    [Arguments(0.0, 10, 30, 30)]
    [Arguments(0.5, 1, 30, 30)]
    [Arguments(0.5, 10, 0.1, 30)]
    [Arguments(0.5, 10, 30, 7200)]
    public async Task Out_of_range_options_are_rejected(double ratio, int throughput, double samplingSeconds, double breakSeconds)
    {
        await Assert.That(() => TestOrm.Options(o =>
        {
            o.CircuitBreaker.FailureRatio = ratio;
            o.CircuitBreaker.MinimumThroughput = throughput;
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(samplingSeconds);
            o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(breakSeconds);
        }).Validate()).Throws<InvalidConfigurationException>();
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => Now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    /// <summary>Conexão cuja abertura executa <see cref="Behavior"/> (lança para simular falha).</summary>
    private sealed class ScriptedConnection(Action behavior) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;

        public Action Behavior { get; set; } = behavior;

        public int Opens;

        public int Timeout { get; init; } = 15;

        public override int ConnectionTimeout => Timeout;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "teste";

        public override string DataSource => "teste";

        public override string ServerVersion => "0";

        public override ConnectionState State => _state;

        public override void Open()
        {
            Interlocked.Increment(ref Opens);
            Behavior();
            _state = ConnectionState.Open;
        }

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            Open();
            return Task.CompletedTask;
        }

        public override void Close() => _state = ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
