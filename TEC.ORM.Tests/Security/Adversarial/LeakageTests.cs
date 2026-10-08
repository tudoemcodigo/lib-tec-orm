using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.ORM.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.Paging;
using TEC.ORM.Queries;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Fakes;
using TEC.ORM.Tests.Integration;

namespace TEC.ORM.Tests.Security.Adversarial;

/// <summary>
/// Vazamento de dados: um marcador secreto é injetado nos valores (colunas, parâmetros, identificadores, texto do SQL,
/// ordenação) e o teste varre o resultado devolvido ao chamador (mensagem e <c>ToString</c> do erro), todos os logs
/// (inclusive exceções registradas) e as tags das <see cref="Activity"/>, garantindo que ele nunca aparece.
/// </summary>
/// <remarks>
/// As mensagens do SQL Server costumam repetir o valor que causou o erro (chave duplicada, conversão, objeto inexistente):
/// é exatamente esse caminho que os testes com banco exercitam.
/// </remarks>
public class LeakageTests
{
    private static string NewSecret() => "SEGREDO" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    /// <summary>Tudo o que sai do ORM numa operação: resultado, logs e telemetria.</summary>
    private static async Task AssertNoSecretAsync(string secret, Result result, CapturingLoggerProvider logs, OrmTelemetryListener telemetry)
    {
        await Assert.That(result.Error?.Message ?? string.Empty).DoesNotContain(secret);
        await Assert.That(result.Error?.ToString() ?? string.Empty).DoesNotContain(secret);
        await Assert.That(logs.AllText).DoesNotContain(secret);

        foreach (var activity in telemetry.Activities)
        {
            await Assert.That(activity.DisplayName).DoesNotContain(secret);
            foreach (var tag in activity.TagObjects)
                await Assert.That(tag.Value?.ToString() ?? string.Empty).DoesNotContain(secret);
        }
        foreach (var (_, tags) in telemetry.Measurements)
            foreach (var value in tags.Values)
                await Assert.That(value?.ToString() ?? string.Empty).DoesNotContain(secret);
    }

    // ---------- Sem banco (InMemory) ----------

    [Test]
    public async Task SortBy_NotAllowed_IsNeverEchoed()
    {
        string secret = NewSecret();
        var logs = new CapturingLoggerProvider();
        using var telemetry = new OrmTelemetryListener(nameof(Customer));
        await using var context = TestOrm.InMemoryContext();
        var customers = TestOrm.Orm<Customer, Guid>(context, logger: logs.CreateLogger<SqlServer.Diagnostics.OrmOperationRunner>());

        var result = await customers.ListAsync(new PageRequest(1, 10, "Name" + secret));

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.InvalidInputCode);
        await AssertNoSecretAsync(secret, result, logs, telemetry);
    }

    [Test]
    [Arguments(IdentifierLogMode.Hashed)]
    [Arguments(IdentifierLogMode.Omitted)]
    public async Task StringIdentifier_WithHashedOrOmittedMode_NeverReachesLogsOrTelemetry(IdentifierLogMode mode)
    {
        // Chave que é dado pessoal (ex.: CPF, e-mail): com Hashed/Omitted ela não vai para a auditoria
        string secret = NewSecret();
        var logs = new CapturingLoggerProvider();
        using var telemetry = new OrmTelemetryListener(nameof(Category));
        await using var context = TestOrm.InMemoryContext();
        var categories = TestOrm.Orm<Category, string>(context, TestOrm.Options(o => o.IdentifierLogMode = mode),
            logs.CreateLogger<SqlServer.Diagnostics.OrmOperationRunner>());

        var created = await categories.CreateAsync(new Category { Id = secret, Name = "Category" });
        var read = await categories.GetByIdAsync(secret);
        var deleted = await categories.DeleteAsync(secret);
        var missing = await categories.GetByIdAsync(secret);

        await Assert.That(created.IsSuccess && read.IsSuccess && deleted.IsSuccess).IsTrue();
        await Assert.That(missing.Error!.Code).IsEqualTo(OrmErrors.NotFoundCode);
        await Assert.That(logs.Entries.Count).IsGreaterThanOrEqualTo(4);
        await AssertNoSecretAsync(secret, missing, logs, telemetry);
    }

    [Test]
    public async Task SqlQuery_ToStringAndDebugger_NeverShowSqlOrValues()
    {
        string secret = NewSecret();
        var query = SqlQuery.Interpolated("clientes.por-email", $"SELECT * FROM Customers /* {secret} */ WHERE Email = {secret}");

        await Assert.That(query.ToString()).DoesNotContain(secret);
        await Assert.That($"{query}").DoesNotContain(secret);
        // O valor interpolado só existe como parâmetro (o comentário é texto constante do desenvolvedor)
        await Assert.That(query.Sql).DoesNotContain("= " + secret);
    }

    // ---------- Banco real: mensagens do SQL Server que repetem o valor ----------

    /// <summary>Executa com o ORM registrado pela fixture de integração (logs capturados).</summary>
    private static async Task WithScopeAsync(CapturingLoggerProvider logs, Func<IServiceProvider, Task> test,
        Action<OrmOptions>? configure = null)
    {
        await SqlServerFixture.RequireAsync();
        await using var provider = SqlServerFixture.Build<CodeFirstContext>(logs, configure);
        await using var scope = provider.CreateAsyncScope();
        await test(scope.ServiceProvider);
        await Assert.That(logs.AllText).DoesNotContain(SqlServerFixture.Password);
    }

    [Test]
    [Category(TestCategories.Integration)]
    public async Task UniqueViolation_DuplicatedValue_NeverLeaks()
    {
        // SQL Server 2601: "Cannot insert duplicate key row ... The duplicate key value is (<valor>)"
        string secret = NewSecret();
        var logs = new CapturingLoggerProvider();
        using var telemetry = new OrmTelemetryListener(nameof(Customer));

        await WithScopeAsync(logs, async services =>
        {
            var customers = services.GetRequiredService<IOrmRepository<Customer, Guid>>();
            string email = $"{secret}@exemplo.com".ToLowerInvariant();
            await customers.CreateAsync(new Customer { Name = "Primeiro", Email = email });

            var duplicated = await customers.CreateAsync(new Customer { Name = "Segundo", Email = email });

            await Assert.That(duplicated.Error!.Code).IsEqualTo(OrmErrors.ConflictCode);
            await AssertNoSecretAsync(secret.ToLowerInvariant(), duplicated, logs, telemetry);
            await AssertNoSecretAsync(secret, duplicated, logs, telemetry);
        }, o => o.IdentifierLogMode = IdentifierLogMode.Omitted);
    }

    [Test]
    [Category(TestCategories.Integration)]
    public async Task ConversionError_ParameterValue_NeverLeaks()
    {
        // SQL Server 245: "Conversion failed when converting the nvarchar value '<valor>' to data type int"
        string secret = NewSecret();
        var logs = new CapturingLoggerProvider();
        string name = "vazamento.conversao-" + Guid.NewGuid().ToString("N")[..8];
        using var telemetry = new OrmTelemetryListener(name);

        await WithScopeAsync(logs, async services =>
        {
            var result = await services.GetRequiredService<IOrmQueryExecutor>()
                .ExecuteScalarAsync<int>(SqlQuery.Interpolated(name, $"SELECT CAST({secret} AS int)"));

            await Assert.That(result.IsFailure).IsTrue();
            await AssertNoSecretAsync(secret, result, logs, telemetry);
        });
    }

    [Test]
    [Category(TestCategories.Integration)]
    public async Task InvalidObject_SqlText_NeverLeaks()
    {
        // SQL Server 208: "Invalid object name '<nome>'" (o texto do SQL também nunca é registrado)
        string secret = NewSecret();
        var logs = new CapturingLoggerProvider();
        string name = "vazamento.objeto-" + Guid.NewGuid().ToString("N")[..8];
        using var telemetry = new OrmTelemetryListener(name);

        await WithScopeAsync(logs, async services =>
        {
            var result = await services.GetRequiredService<IOrmQueryExecutor>()
                .QueryAsync<int>(SqlQuery.Create(name, $"SELECT Id /* {secret} */ FROM codefirst.Tabela{secret}"));

            await Assert.That(result.IsFailure).IsTrue();
            await AssertNoSecretAsync(secret, result, logs, telemetry);
        });
    }

    [Test]
    [Category(TestCategories.Integration)]
    public async Task ForeignKeyViolation_Values_NeverLeak()
    {
        // SQL Server 547: "The INSERT statement conflicted with the FOREIGN KEY constraint ... column 'CategoriaId'"
        string secret = "K" + Guid.NewGuid().ToString("N")[..12];
        var logs = new CapturingLoggerProvider();
        using var telemetry = new OrmTelemetryListener(nameof(Product));

        await WithScopeAsync(logs, async services =>
        {
            var result = await services.GetRequiredService<IOrmRepository<Product, int>>()
                .CreateAsync(new Product { Name = "Product " + secret, Price = 1m, CategoryId = secret });

            await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConflictCode);
            await AssertNoSecretAsync(secret, result, logs, telemetry);
        });
    }
}
