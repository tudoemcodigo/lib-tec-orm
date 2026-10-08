using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using TEC.Core.Common.Results;
using TEC.Core.Exceptions;
using TEC.ORM.Common;
using TEC.ORM.Queries;
using TEC.ORM.SqlServer;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.Diagnostics;
using TEC.ORM.SqlServer.Internal;
using TEC.ORM.SqlServer.SoftDelete;
using TEC.ORM.SqlServer.UnitOfWork;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests;

/// <summary>
/// Falhas transitórias, esgotamento do pool e limites: novas tentativas só em leituras fora de transação (nunca em escritas),
/// pool esgotado como infraestrutura e entradas que não podem ser alteradas depois de validadas.
/// </summary>
public class ResilienceTests
{
    private const int Deadlock = 1205;
    private const int AzureDatabaseUnavailable = 40613;
    private const int ClientTimeout = -2;
    private const int LoginFailed = 18456;

    private static (OrmOperationRunner Runner, CapturingLoggerProvider Logs) CreateRunner(int retries = 2)
    {
        var logs = new CapturingLoggerProvider();
        var options = TestOrm.Options(o =>
        {
            o.TransientRetryCount = retries;
            o.TransientRetryDelay = TimeSpan.FromMilliseconds(10);
        });
        return (new OrmOperationRunner(logs.CreateLogger<OrmOperationRunner>(), options), logs);
    }

    private static OrmOperation Read(bool retryable = true) =>
        new(OrmDiagnostics.EntityFrameworkProvider, "get", "ResilienceTarget", isWrite: false) { IsRetryable = retryable };

    private static OrmOperation Write() =>
        new(OrmDiagnostics.EntityFrameworkProvider, "update", "ResilienceTarget", isWrite: true) { IsRetryable = true };

    /// <summary>Falha com <paramref name="exception"/> nas primeiras <paramref name="failures"/> tentativas.</summary>
    private static Func<CancellationToken, Task<Result<int>>> FailingAction(Exception exception, int failures, Action onAttempt) =>
        _ =>
        {
            onAttempt();
            return failures-- > 0 ? throw exception : Task.FromResult(Result<int>.Success(42));
        };

    // ---------- Novas tentativas ----------

    [Test]
    [Arguments(Deadlock)]
    [Arguments(AzureDatabaseUnavailable)]
    public async Task Retryable_read_with_transient_failure_is_retried_and_succeeds(int errorNumber)
    {
        var (runner, logs) = CreateRunner();
        int attempts = 0;

        var result = await runner.ExecuteAsync(Read(), FailingAction(SqlExceptionFactory.Create(errorNumber), 2, () => attempts++),
            CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(42);
        await Assert.That(attempts).IsEqualTo(3);
        await Assert.That(logs.Entries.Count(e => e.EventId.Id == 3008)).IsEqualTo(2);
    }

    [Test]
    public async Task Write_is_never_retried_even_with_transient_failure()
    {
        var (runner, _) = CreateRunner();
        int attempts = 0;

        var result = await runner.ExecuteAsync(Write(), FailingAction(SqlExceptionFactory.Create(Deadlock), 1, () => attempts++),
            CancellationToken.None);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConcurrencyCode);
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task Read_not_marked_retryable_is_not_retried()
    {
        var (runner, _) = CreateRunner();
        int attempts = 0;

        var result = await runner.ExecuteAsync(Read(retryable: false),
            FailingAction(SqlExceptionFactory.Create(AzureDatabaseUnavailable), 1, () => attempts++), CancellationToken.None);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConnectionUnavailableCode);
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    [Arguments(ClientTimeout)]
    [Arguments(LoginFailed)]
    [Arguments(2627)]
    public async Task Non_transient_failures_are_not_retried(int errorNumber)
    {
        var (runner, _) = CreateRunner();
        int attempts = 0;

        var result = await runner.ExecuteAsync(Read(), FailingAction(SqlExceptionFactory.Create(errorNumber), 1, () => attempts++),
            CancellationToken.None);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task Retries_stop_at_the_configured_count()
    {
        var (runner, _) = CreateRunner(retries: 3);
        int attempts = 0;

        var result = await runner.ExecuteAsync(Read(), FailingAction(SqlExceptionFactory.Create(AzureDatabaseUnavailable), 100, () => attempts++),
            CancellationToken.None);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConnectionUnavailableCode);
        await Assert.That(attempts).IsEqualTo(4);
    }

    [Test]
    public async Task Zero_retries_disables_the_retry()
    {
        var (runner, _) = CreateRunner(retries: 0);
        int attempts = 0;

        await runner.ExecuteAsync(Read(), FailingAction(SqlExceptionFactory.Create(Deadlock), 1, () => attempts++), CancellationToken.None);

        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_during_the_retry_wait_cancels_the_operation()
    {
        var logs = new CapturingLoggerProvider();
        var options = TestOrm.Options(o => o.TransientRetryDelay = TimeSpan.FromSeconds(10));
        var runner = new OrmOperationRunner(logs.CreateLogger<OrmOperationRunner>(), options);
        using var cancel = new CancellationTokenSource();
        int attempts = 0;

        var task = runner.ExecuteAsync(Read(), token =>
        {
            attempts++;
            cancel.CancelAfter(TimeSpan.FromMilliseconds(50));
            throw SqlExceptionFactory.Create(Deadlock);
        }, cancel.Token);

        await Assert.That(async () => await task).Throws<OperationCanceledException>();
        await Assert.That(attempts).IsEqualTo(1);
    }

    // ---------- Repositório: o que pode ser repetido ----------

    private sealed class RecordingRunner : IOrmOperationRunner
    {
        public ConcurrentQueue<OrmOperation> Operations { get; } = new();

        public Task<Result<T>> ExecuteAsync<T>(OrmOperation operation, Func<CancellationToken, Task<Result<T>>> action,
            CancellationToken cancellationToken)
        {
            Operations.Enqueue(operation);
            return action(cancellationToken);
        }

        public Task<Result> ExecuteAsync(OrmOperation operation, Func<CancellationToken, Task<Result>> action,
            CancellationToken cancellationToken)
        {
            Operations.Enqueue(operation);
            return action(cancellationToken);
        }
    }

    private static CodeFirstContext ContextWithFakeTransactions() =>
        new(new DbContextOptionsBuilder<CodeFirstContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .AddInterceptors(new SoftDeleteInterceptor(TimeProvider.System))
            .ReplaceService<IDbContextTransactionManager, FakeTransactionManager>()
            .Options);

    [Test]
    public async Task Repository_reads_are_retryable_only_outside_a_transaction_and_writes_never()
    {
        await using var context = ContextWithFakeTransactions();
        var runner = new RecordingRunner();
        var repository = new OrmRepository<Category, string>(context, runner, TestOrm.Options());

        await repository.CountAsync();
        await repository.CreateAsync(new Category { Id = "retry-1", Name = "Livros" });
        var unitOfWork = new OrmUnitOfWork(context);
        await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        await repository.ExistsAsync("retry-1");
        await unitOfWork.RollbackAsync(CancellationToken.None);

        var operations = runner.Operations.ToList();
        await Assert.That(operations.Select(o => (o.Operation, o.IsRetryable)))
            .IsEquivalentTo([("count", true), ("create", false), ("exists", false)]);
    }

    // ---------- Tradução: pool esgotado e falhas transitórias ----------

    [Test]
    public async Task Exhausted_pool_marked_on_open_is_connection_unavailable_infrastructure()
    {
        var exception = new InvalidOperationException("Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool.");
        OrmExceptionTranslator.MarkConnectionOpenFailure(exception);

        var translation = OrmExceptionTranslator.Translate(exception);

        await Assert.That(translation.Error.Code).IsEqualTo(OrmErrors.ConnectionUnavailableCode);
        await Assert.That(translation.IsInfrastructure).IsTrue();
        await Assert.That(OrmExceptionTranslator.IsDatabaseException(exception)).IsTrue();
        await Assert.That(OrmExceptionTranslator.IsTransient(exception)).IsFalse();   // repetir agravaria a tempestade no pool
    }

    [Test]
    public async Task Exhausted_pool_is_logged_without_stack_trace_or_message()
    {
        var (runner, logs) = CreateRunner();
        var exception = new InvalidOperationException("mensagem-do-pool-que-nao-vai-para-o-log");
        OrmExceptionTranslator.MarkConnectionOpenFailure(exception);

        var result = await runner.ExecuteAsync<int>(Read(), _ => throw exception, CancellationToken.None);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConnectionUnavailableCode);
        await Assert.That(logs.Entries.Any(e => e.EventId.Id == 3005)).IsFalse();
        await Assert.That(logs.AllText).DoesNotContain("mensagem-do-pool-que-nao-vai-para-o-log");
        await Assert.That(logs.Entries.Any(e => e.EventId.Id == 3004 && e.Level == LogLevel.Error)).IsTrue();
    }

    [Test]
    public async Task Unmarked_invalid_operation_is_still_an_unexpected_failure()
    {
        var translation = OrmExceptionTranslator.Translate(new InvalidOperationException("bug"));

        await Assert.That(translation.Error.Code).IsEqualTo(OrmErrors.FailureCode);
        await Assert.That(translation.IsInfrastructure).IsFalse();
    }

    [Test]
    public async Task Azure_transient_error_is_connection_unavailable()
    {
        var translation = OrmExceptionTranslator.Translate(SqlExceptionFactory.Create(AzureDatabaseUnavailable));

        await Assert.That(translation.Error.Code).IsEqualTo(OrmErrors.ConnectionUnavailableCode);
        await Assert.That(translation.IsInfrastructure).IsTrue();
    }

    // ---------- Opções novas ----------

    [Test]
    [Arguments(0, 2, 200)]
    [Arguments(1_000_001, 2, 200)]
    [Arguments(100, -1, 200)]
    [Arguments(100, 6, 200)]
    [Arguments(100, 2, 5)]
    [Arguments(100, 2, 10_001)]
    public async Task Out_of_range_limits_fail_at_startup(int maxQueryRows, int retries, int delayMilliseconds)
    {
        var options = new OrmOptions
        {
            ConnectionSecretName = TestOrm.SecretName,
            MaxQueryRows = maxQueryRows,
            TransientRetryCount = retries,
            TransientRetryDelay = TimeSpan.FromMilliseconds(delayMilliseconds)
        };

        await Assert.That(options.Validate).Throws<InvalidConfigurationException>();
    }

    // ---------- Entradas imutáveis e logs ----------

    [Test]
    public async Task SqlQuery_parameters_cannot_be_changed_after_validation()
    {
        var query = SqlQuery.Create("resiliencia.parametros", "SELECT 1 WHERE @a = 1", new Dictionary<string, object?> { ["a"] = 1 });

        var dictionary = query.Parameters as IDictionary<string, object?>;

        await Assert.That(dictionary).IsNotNull();
        await Assert.That(() => dictionary!["b; DROP TABLE x --"] = 2).Throws<NotSupportedException>();
        await Assert.That(query.Parameters.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("abc\u202Edef")]
    [Arguments("linha\u2028nova")]
    [Arguments("para\u2029grafo")]
    [Arguments("zero\u200Bwidth")]
    public async Task Plain_identifier_never_carries_format_or_line_separator_characters(string identifier)
    {
        var (runner, _) = CreateRunner();

        string formatted = runner.FormatIdentifier(identifier);

        await Assert.That(formatted.Length).IsEqualTo(identifier.Length);
        await Assert.That(formatted).Contains("?");
        await Assert.That(formatted.Any(c => char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)).IsFalse();
    }
}
