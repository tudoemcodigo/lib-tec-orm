using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Validation;
using TEC.ORM.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.Queries;
using TEC.ORM.SqlServer.Security;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests.Integration;

/// <summary>
/// Contra o SQL Server real: rollback do command externo quando um interno falha (TEC.Cqrs), limites de linhas das leituras
/// complexas sem carregar o resultado inteiro, pool de conexões esgotado e isolamento do banco de testes.
/// </summary>
[Category(TestCategories.Integration)]
public class ResilienceIntegrationTests
{
    private const string HugeQuery =
        "SELECT a.object_id AS Id, a.name AS Name FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c";

    private static string Unique() => Guid.NewGuid().ToString("N")[..10];

    // ---------- Transação: command dentro de command ----------

    [AllowAnonymousRequest]
    [SkipValidation]
    public sealed record CreateOuterCommand(string Email, string InnerEmail) : ICommand;

    [AllowAnonymousRequest]
    [SkipValidation]
    public sealed record CreateInnerCommand(string Email) : ICommand;

    public sealed class CreateOuterHandler(IOrmRepository<Customer, Guid> customers, ISender sender) : ICommandHandler<CreateOuterCommand>
    {
        public async Task<Result> Handle(CreateOuterCommand request, CancellationToken cancellationToken)
        {
            var created = await customers.CreateAsync(new Customer { Name = "Externo", Email = request.Email }, cancellationToken);
            if (created.IsFailure)
                return created;
            // O handler externo "trata" a falha do interno e segue: a transação ainda assim tem de ser desfeita
            _ = await sender.Send(new CreateInnerCommand(request.InnerEmail), cancellationToken);
            return Result.Success();
        }
    }

    public sealed class CreateInnerHandler(IOrmRepository<Customer, Guid> customers) : ICommandHandler<CreateInnerCommand>
    {
        public async Task<Result> Handle(CreateInnerCommand request, CancellationToken cancellationToken)
        {
            var created = await customers.CreateAsync(new Customer { Name = "Interno", Email = request.Email }, cancellationToken);
            if (created.IsFailure)
                return created;
            // Mesmo e-mail de novo: violação do índice único dentro da transação do externo
            return await customers.CreateAsync(new Customer { Name = "Interno duplicado", Email = request.Email }, cancellationToken);
        }
    }

    [Test]
    public async Task Nested_command_failure_rolls_back_the_outer_transaction()
    {
        await SqlServerFixture.RequireAsync();
        var logs = new CapturingLoggerProvider();
        await using var provider = SqlServerFixture.Build<CodeFirstContext>(logs, register: services => services.AddTecCqrs(cqrs => cqrs
            .AddCommandHandler<CreateOuterCommand, CreateOuterHandler>()
            .AddCommandHandler<CreateInnerCommand, CreateInnerHandler>()));
        string suffix = Unique();
        string outerEmail = $"externo{suffix}@exemplo.com", innerEmail = $"interno{suffix}@exemplo.com";

        Result result;
        await using (var scope = provider.CreateAsyncScope())
            result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateOuterCommand(outerEmail, innerEmail));

        await using var check = provider.CreateAsyncScope();
        var customers = check.ServiceProvider.GetRequiredService<IOrmRepository<Customer, Guid>>();
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConflictCode);
        await Assert.That((await customers.ExistsAsync(c => c.Email == outerEmail)).Value).IsFalse();
        await Assert.That((await customers.ExistsAsync(c => c.Email == innerEmail)).Value).IsFalse();
        await Assert.That(logs.AllText).DoesNotContain(innerEmail);
        await Assert.That(logs.AllText).DoesNotContain(SqlServerFixture.Password);
    }

    [Test]
    public async Task Test_database_uses_read_committed_snapshot()
    {
        await SqlServerFixture.RequireAsync();
        await using var provider = SqlServerFixture.Build<CodeFirstContext>(new CapturingLoggerProvider());
        await using var scope = provider.CreateAsyncScope();

        var rcsi = await scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>().ExecuteScalarAsync<bool>(SqlQuery.Create(
            "testes.rcsi", "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()"));

        await Assert.That(rcsi.Value).IsTrue();
    }

    // ---------- Leituras complexas: limites sem carregar tudo ----------

    [Test]
    public async Task Query_over_the_row_limit_fails_fast_without_reading_the_rest()
    {
        await SqlServerFixture.RequireAsync();
        await using var provider = SqlServerFixture.Build<CodeFirstContext>(new CapturingLoggerProvider(), o => o.MaxQueryRows = 100);
        await using var scope = provider.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>();

        var watch = Stopwatch.StartNew();
        var result = await queries.QueryAsync<NamedObject>(SqlQuery.Create("testes.limite", HugeQuery));
        watch.Stop();

        // Centenas de milhões de linhas: só termina rápido se o comando for cancelado em vez de drenado
        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.TooManyResultsCode);
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        var next = await queries.ExecuteScalarAsync<int>(SqlQuery.Create("testes.um", "SELECT 1"));
        await Assert.That(next.Value).IsEqualTo(1);
    }

    [Test]
    public async Task Query_single_with_many_rows_fails_after_the_second_row()
    {
        await SqlServerFixture.RequireAsync();
        await using var provider = SqlServerFixture.Build<CodeFirstContext>(new CapturingLoggerProvider());
        await using var scope = provider.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>();

        var watch = Stopwatch.StartNew();
        var result = await queries.QuerySingleOrDefaultAsync<NamedObject>(SqlQuery.Create("testes.unica", HugeQuery));
        watch.Stop();

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.InvalidInputCode);
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Scalar_reads_only_the_first_row_and_null_is_default()
    {
        await SqlServerFixture.RequireAsync();
        await using var provider = SqlServerFixture.Build<CodeFirstContext>(new CapturingLoggerProvider());
        await using var scope = provider.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>();

        var watch = Stopwatch.StartNew();
        var first = await queries.ExecuteScalarAsync<long>(SqlQuery.Create("testes.escalar", HugeQuery));
        watch.Stop();
        var nullValue = await queries.ExecuteScalarAsync<int>(SqlQuery.Create("testes.nulo", "SELECT CAST(NULL AS int)"));
        var converted = await queries.ExecuteScalarAsync<long>(SqlQuery.Create("testes.conversao", "SELECT CAST(7 AS int)"));
        var none = await queries.ExecuteScalarAsync<string>(SqlQuery.Create("testes.vazio", "SELECT name FROM sys.objects WHERE 1 = 0"));

        await Assert.That(first.IsSuccess).IsTrue();
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        await Assert.That(nullValue.Value).IsEqualTo(0);
        await Assert.That(converted.Value).IsEqualTo(7L);
        await Assert.That(none.Value).IsNull();
    }

    [Test]
    public async Task Join_splits_on_the_last_matching_column_and_left_join_gives_null()
    {
        await SqlServerFixture.RequireAsync();
        await using var provider = SqlServerFixture.Build<CodeFirstContext>(new CapturingLoggerProvider());
        await using var scope = provider.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>();

        var rows = await queries.QueryAsync<NamedObject, NamedObject?, (NamedObject, NamedObject?)>(SqlQuery.Create("testes.join",
            "SELECT 1 AS Id, N'a' AS Name, 2 AS Id, N'b' AS Name UNION ALL SELECT 3, N'c', NULL, NULL ORDER BY 1"),
            (first, second) => (first, second));
        var missing = await queries.QueryAsync<NamedObject, NamedObject, NamedObject>(SqlQuery.Create("testes.join-sem-coluna",
            "SELECT 1 AS Id, N'a' AS Name"), (first, _) => first, splitOn: "Inexistente");

        await Assert.That(rows.Value.Count).IsEqualTo(2);
        await Assert.That(rows.Value[0].Item1.Id).IsEqualTo(1);
        await Assert.That(rows.Value[0].Item2!.Id).IsEqualTo(2);
        await Assert.That(rows.Value[0].Item2!.Name).IsEqualTo("b");
        await Assert.That(rows.Value[1].Item2).IsNull();
        await Assert.That(missing.Error!.Code).IsEqualTo(OrmErrors.InvalidInputCode);
    }

    // ---------- Pool de conexões esgotado ----------

    [Test]
    public async Task Exhausted_pool_is_connection_unavailable_in_both_providers_without_stack_trace()
    {
        await SqlServerFixture.RequireAsync();
        var logs = new CapturingLoggerProvider();
        // Pool exclusivo (Application Name único) com uma conexão só, e espera curta por ela
        var tiny = new SqlConnectionStringBuilder(SqlServerFixture.ConnectionString) { MaxPoolSize = 1, ConnectTimeout = 2 };
        await using var provider = SqlServerFixture.Build<CodeFirstContext>(logs, o => o.ApplicationName = "TEC.ORM.Tests.pool-" + Unique(),
            secretOverride: tiny.ConnectionString);
        var security = provider.GetRequiredService<IOrmConnectionSecurity>();
        var writeHeld = await security.OpenConnectionAsync(OrmConnectionKind.ReadWrite, CancellationToken.None);
        var readHeld = await security.OpenConnectionAsync(OrmConnectionKind.ReadOnly, CancellationToken.None);
        await using var heldWrite = writeHeld.Value;
        await using var heldRead = readHeld.Value;

        await using var scope = provider.CreateAsyncScope();
        var count = await scope.ServiceProvider.GetRequiredService<IOrmRepository<Customer, Guid>>().CountAsync();
        var query = await scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>()
            .ExecuteScalarAsync<int>(SqlQuery.Create("testes.pool", "SELECT 1"));

        await Assert.That(count.Error!.Code).IsEqualTo(OrmErrors.ConnectionUnavailableCode);
        await Assert.That(query.Error!.Code).IsEqualTo(OrmErrors.ConnectionUnavailableCode);
        await Assert.That(logs.Entries.Any(e => e.EventId.Id == 3005)).IsFalse();
        await Assert.That(logs.AllText).DoesNotContain(SqlServerFixture.Password);
    }

    public sealed class NamedObject
    {
        public int Id { get; set; }

        public string? Name { get; set; }
    }
}
