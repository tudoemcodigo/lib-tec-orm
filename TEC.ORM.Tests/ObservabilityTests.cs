using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.Diagnostics;
using TEC.ORM.SqlServer.Internal;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests;

/// <summary>Integração com o TEC.Observability: Activity, métrica e log de auditoria em sucesso, falha, exceção e cancelamento.</summary>
public class ObservabilityTests
{
    private static (OrmOperationRunner Logger, CapturingLoggerProvider Logs) Create(IdentifierLogMode mode = IdentifierLogMode.Plain)
    {
        var logs = new CapturingLoggerProvider();
        return (new OrmOperationRunner(logs.CreateLogger<OrmOperationRunner>(), TestOrm.Options(o => o.IdentifierLogMode = mode)), logs);
    }

    private static string NewTarget() => "Alvo" + Guid.NewGuid().ToString("N")[..8];

    [Test]
    public async Task Successful_write_produces_activity_metric_and_audit()
    {
        string target = NewTarget();
        using var telemetry = new OrmTelemetryListener(target);
        var (observability, logs) = Create();
        var operation = new OrmOperation(OrmDiagnostics.EntityFrameworkProvider, "create", target, isWrite: true);

        var result = await observability.ExecuteAsync(operation, _ =>
        {
            operation.Identifier = 42L;   // identificador gerado durante a operação
            return Task.FromResult(Result<string>.Success("ok"));
        }, CancellationToken.None);

        await Assert.That(result.Value).IsEqualTo("ok");

        var activity = telemetry.Activities.Single();
        await Assert.That(activity.DisplayName).IsEqualTo($"create {target}");
        await Assert.That(activity.Kind).IsEqualTo(ActivityKind.Client);
        await Assert.That(activity.GetTagItem("db.system.name")).IsEqualTo("microsoft.sql_server");
        await Assert.That(activity.GetTagItem("orm.provider")).IsEqualTo("entityframework");
        await Assert.That(activity.GetTagItem("orm.success") is true).IsTrue();
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Unset);
        // O identificador fica fora do trace (traces costumam ir para terceiros)
        await Assert.That(activity.TagObjects.Any(t => Equals(t.Value, 42L))).IsFalse();

        var (seconds, tags) = telemetry.Measurements.Single();
        await Assert.That(seconds).IsGreaterThanOrEqualTo(0);
        await Assert.That(tags["orm.operation"]).IsEqualTo("create");
        await Assert.That(tags.ContainsKey("error.type")).IsFalse();

        var entry = logs.Entries.Single();
        await Assert.That(entry.Level).IsEqualTo(LogLevel.Information);
        await Assert.That(entry.EventId.Id).IsEqualTo(3001);
        await Assert.That(entry.Text).Contains($"create de {target} (42)");
    }

    [Test]
    public async Task Successful_hard_delete_is_audited_as_warning_with_alert()
    {
        string target = NewTarget();
        var (observability, logs) = Create();
        var operation = new OrmOperation(OrmDiagnostics.EntityFrameworkProvider, "hard-delete", target, isWrite: true)
        {
            Identifier = 42L,
            IsHardDelete = true
        };

        await observability.ExecuteAsync(operation, _ => Task.FromResult(Result.Success()), CancellationToken.None);

        var entry = logs.Entries.Single();
        await Assert.That(entry.Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(entry.EventId.Id).IsEqualTo(3007);
        await Assert.That(entry.Text).Contains("EXCLUSÃO FÍSICA");
        await Assert.That(entry.Text).Contains($"hard-delete de {target} (42)");
    }

    [Test]
    public async Task Successful_read_is_logged_as_debug()
    {
        var (observability, logs) = Create();
        await observability.ExecuteAsync(new OrmOperation("dapper", "query", NewTarget(), isWrite: false),
            _ => Task.FromResult(Result<int>.Success(1)), CancellationToken.None);

        await Assert.That(logs.Entries.Single().Level).IsEqualTo(LogLevel.Debug);
    }

    [Test]
    public async Task Expected_failure_marks_error_on_activity_and_metric()
    {
        string target = NewTarget();
        using var telemetry = new OrmTelemetryListener(target);
        var (observability, logs) = Create();

        var result = await observability.ExecuteAsync(new OrmOperation("entityframework", "get", target, isWrite: false) { Identifier = 7 },
            _ => Task.FromResult(Result<int>.Failure(OrmErrors.NotFound())), CancellationToken.None);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.NotFoundCode);
        var activity = telemetry.Activities.Single();
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(activity.GetTagItem("error.type")).IsEqualTo(OrmErrors.NotFoundCode);
        await Assert.That(telemetry.Measurements.Single().Tags["error.type"]).IsEqualTo(OrmErrors.NotFoundCode);
        await Assert.That(logs.Entries.Single().Text).Contains("retornou ORM_NAO_ENCONTRADO");
    }

    [Test]
    public async Task Database_exception_becomes_result_without_the_original_message_in_the_log()
    {
        var (observability, logs) = Create();
        // A mensagem do banco costuma trazer valores (ex.: e-mail duplicado): não pode chegar ao log
        var exception = new DbUpdateConcurrencyException("Linha alterada: ana@exemplo.com");

        var result = await observability.ExecuteAsync(new OrmOperation("entityframework", "update", NewTarget(), isWrite: true),
            _ => Task.FromException<Result<int>>(exception), CancellationToken.None);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConcurrencyCode);
        await Assert.That(logs.AllText).DoesNotContain("ana@exemplo.com");
        await Assert.That(logs.Entries.Single().Level).IsEqualTo(LogLevel.Warning);
    }

    [Test]
    public async Task Connection_exception_becomes_connection_unavailable()
    {
        var (observability, logs) = Create();

        var result = await observability.ExecuteAsync(new OrmOperation("entityframework", "list", NewTarget(), isWrite: false),
            _ => Task.FromException<Result<int>>(new OrmConnectionException(OrmErrors.InvalidConnectionSecret())), CancellationToken.None);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.InvalidConnectionSecretCode);
        await Assert.That(logs.Entries.Single().Level).IsEqualTo(LogLevel.Error);
    }

    [Test]
    public async Task Unexpected_exception_becomes_failure_and_is_logged_with_stack_trace()
    {
        var (observability, logs) = Create();

        var result = await observability.ExecuteAsync(new OrmOperation("entityframework", "find", NewTarget(), isWrite: false),
            _ => Task.FromException<Result<int>>(new InvalidOperationException("navegação inexistente")), CancellationToken.None);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.FailureCode);
        var entry = logs.Entries.Single();
        await Assert.That(entry.EventId.Id).IsEqualTo(3005);
        await Assert.That(entry.Text).Contains("InvalidOperationException");
    }

    [Test]
    public async Task Timeout_becomes_timed_out()
    {
        var (observability, _) = Create();
        var result = await observability.ExecuteAsync(new OrmOperation("dapper", "query", NewTarget(), isWrite: false),
            _ => Task.FromException<Result<int>>(new TimeoutException()), CancellationToken.None);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.TimeoutCode);
    }

    [Test]
    public async Task Cancellation_is_rethrown_and_measured_as_canceled()
    {
        string target = NewTarget();
        using var telemetry = new OrmTelemetryListener(target);
        var (observability, _) = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await observability.ExecuteAsync(new OrmOperation("dapper", "query", target, isWrite: false),
                token => Task.FromException<Result<int>>(new OperationCanceledException(token)), cts.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(telemetry.Measurements.Single().Tags["error.type"]).IsEqualTo(OrmDiagnostics.CanceledErrorType);
    }

    [Test]
    public async Task Operation_without_value_is_also_observed()
    {
        var (observability, logs) = Create();
        var result = await observability.ExecuteAsync(new OrmOperation("entityframework", "delete", NewTarget(), isWrite: true) { Identifier = 3 },
            _ => Task.FromResult(Result.Success()), CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(logs.Entries.Single().EventId.Id).IsEqualTo(3001);
    }

    // ---------- Identificador no log ----------

    [Test]
    public async Task Hashed_identifier_does_not_expose_the_value()
    {
        var (observability, _) = Create(IdentifierLogMode.Hashed);
        string formatted = observability.FormatIdentifier("123.456.789-09");

        await Assert.That(formatted).Contains("hmac:");
        await Assert.That(formatted).DoesNotContain("123");
        await Assert.That(observability.FormatIdentifier("123.456.789-09")).IsEqualTo(formatted);   // correlacionável no processo
    }

    [Test]
    public async Task Omitted_identifier_and_control_characters_removed()
    {
        var (omitted, _) = Create(IdentifierLogMode.Omitted);
        var (plain, _) = Create();

        await Assert.That(omitted.FormatIdentifier(Guid.NewGuid())).IsEqualTo("-");
        await Assert.That(plain.FormatIdentifier("a\nINFO falso")).IsEqualTo("a?INFO falso");
        await Assert.That(plain.FormatIdentifier(new string('x', 100)).Length).IsEqualTo(65);
        await Assert.That(plain.FormatIdentifier(null)).IsEqualTo("-");
    }
}
