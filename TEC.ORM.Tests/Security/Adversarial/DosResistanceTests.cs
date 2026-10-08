using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TEC.ORM.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.Paging;
using TEC.ORM.Queries;
using TEC.ORM.Specifications;
using TEC.ORM.SqlServer.Internal;
using TEC.ORM.SqlServer.SoftDelete;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Fakes;
using TEC.ORM.Tests.Integration;

namespace TEC.ORM.Tests.Security.Adversarial;

/// <summary>
/// Negação de serviço: entradas adversariais (SQL no limite de tamanho com aninhamento e aspas de pior caso, parâmetros
/// demais, páginas fora do limite, ordenação gigante, buscas sem limite e consultas caras) precisam falhar rápido, sem
/// materializar dados nem segurar o banco.
/// </summary>
/// <remarks>
/// Os limites de tempo são folgados (máquinas de CI lentas e testes em paralelo): pegam laços sem fim e crescimento
/// quadrático/exponencial, não pequenas regressões de desempenho (essas ficam com o TEC.ORM.Benchmarks).
/// </remarks>
[NotInParallel(TimingKey)]
public class DosResistanceTests
{
    /// <summary>Chave dos testes que medem tempo: não rodam ao mesmo tempo.</summary>
    public const string TimingKey = "seguranca-tempo";

    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(5);

    // ---------- Verificação de somente leitura: tempo linear no pior caso ----------

    public static IEnumerable<Func<(string Scenario, string Sql)>> WorstCaseSql()
    {
        int max = SqlQuery.MaxSqlLength;
        yield return () => ("comentários aninhados sem fim", Repeat("/*", max / 2));
        yield return () => ("comentários aninhados fechados", Repeat("/*", max / 4) + Repeat("*/", max / 4));
        yield return () => ("aspas sem fechamento", "SELECT " + new string('\'', max - 7));
        yield return () => ("aspas escapadas", "SELECT '" + Repeat("''", (max - 10) / 2) + "'");
        yield return () => ("colchetes escapados", "SELECT [" + Repeat("]]", (max - 10) / 2) + "]");
        yield return () => ("comentários de linha", "SELECT 1" + Repeat("--\r", (max - 8) / 3));
        yield return () => ("palavras curtas", "SELECT " + Repeat("a ", (max - 7) / 2));
        yield return () => ("número gigante colado", "SELECT " + new string('1', max - 7));
        yield return () => ("ponto e vírgula", "SELECT 1" + new string(';', max - 8));
        yield return () => ("dígitos e letras alternados", "SELECT " + Repeat("1a", (max - 7) / 2));
    }

    [Test]
    [MethodDataSource(nameof(WorstCaseSql))]
    public async Task ReadOnlyGuard_WorstCaseInputsAtMaxLength_FinishInLinearTime(string scenario, string sql)
    {
        await Assert.That(sql.Length).IsLessThanOrEqualTo(SqlQuery.MaxSqlLength);

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
            _ = ReadOnlySqlGuard.Check(sql);

        await Assert.That(watch.Elapsed).IsLessThan(Fast).Because($"{scenario}: 50 verificações de 32 KB devem levar milissegundos");
    }

    // ---------- SqlQuery: limites de tamanho e de parâmetros ----------

    [Test]
    public async Task SqlQuery_AboveLimits_IsRejectedWithoutBuildingTheQuery()
    {
        var watch = Stopwatch.StartNew();

        var tooManyArguments = Enumerable.Range(0, SqlQuery.MaxParameters + 1).Select(i => (object?)i).ToArray();
        string tooManyFormat = "SELECT 1 WHERE 1 IN (" + string.Join(',', tooManyArguments.Select((_, i) => $"{{{i}}}")) + ")";
        await Assert.That(() => SqlQuery.Interpolated("limite", FormattableStringFactory.Create(tooManyFormat, tooManyArguments)))
            .Throws<ArgumentException>();

        var tooManyParameters = Enumerable.Range(0, SqlQuery.MaxParameters + 1).ToDictionary(i => $"p{i}", i => (object?)i);
        await Assert.That(() => SqlQuery.Create("limite", "SELECT 1", tooManyParameters)).Throws<ArgumentException>();

        await Assert.That(() => SqlQuery.Create("limite", "SELECT " + new string('1', SqlQuery.MaxSqlLength))).Throws<ArgumentException>();

        // No limite exato é aceito
        var atLimit = Enumerable.Range(0, SqlQuery.MaxParameters).ToDictionary(i => $"p{i}", i => (object?)i);
        await Assert.That(SqlQuery.Create("limite", "SELECT 1", atLimit).Parameters.Count).IsEqualTo(SqlQuery.MaxParameters);
        await Assert.That(SqlQuery.Create("limite", "SELECT " + new string('1', SqlQuery.MaxSqlLength - 7)).Sql.Length).IsEqualTo(SqlQuery.MaxSqlLength);

        await Assert.That(watch.Elapsed).IsLessThan(Fast);
    }

    // ---------- Repositório: nada é materializado além do limite ----------

    /// <summary>Conta as entidades materializadas pelo EF Core (prova de que a busca não carrega tudo para depois recusar).</summary>
    private sealed class MaterializationCounter : IMaterializationInterceptor
    {
        private int _count;

        public int Count => _count;

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            Interlocked.Increment(ref _count);
            return entity;
        }
    }

    private static async Task<(CodeFirstContext Context, MaterializationCounter Counter, IOrmRepository<Customer, Guid> Customers)> SeedAsync(int rows,
        Action<SqlServer.Configuration.OrmOptions>? configure = null)
    {
        var counter = new MaterializationCounter();
        var context = new CodeFirstContext(new DbContextOptionsBuilder<CodeFirstContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .AddInterceptors(new SoftDeleteInterceptor(TimeProvider.System), counter)
            .Options);
        context.Customers.AddRange(Enumerable.Range(0, rows).Select(i => new Customer { Name = $"Customer {i}", Email = $"c{i}@exemplo.com" }));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return (context, counter, TestOrm.Orm<Customer, Guid>(context, TestOrm.Options(configure)));
    }

    [Test]
    public async Task FindAsync_AboveMaxFindResults_MaterializesAtMostLimitPlusOne()
    {
        var (context, counter, customers) = await SeedAsync(2_000, o => o.MaxFindResults = 50);
        await using var _ = context;

        var result = await customers.FindAsync(c => c.Name.StartsWith("Customer"));

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.TooManyResultsCode);
        await Assert.That(counter.Count).IsLessThanOrEqualTo(51);
    }

    public static IEnumerable<Func<(string Scenario, PageRequest Page)>> HostilePages()
    {
        yield return () => ("tamanho acima do máximo", new PageRequest(1, 100_000));
        yield return () => ("tamanho negativo", new PageRequest(1, -5));
        yield return () => ("página negativa", new PageRequest(-1, 10));
        yield return () => ("deslocamento que estoura int", new PageRequest(int.MaxValue, 100));
        yield return () => ("ordenação de 1 MB", new PageRequest(1, 10, new string('a', 1024 * 1024)));
        yield return () => ("ordenação por coluna bloqueada", new PageRequest(1, 10, "IsDeleted"));
        yield return () => ("ordenação inexistente", new PageRequest(1, 10, "Name; DROP TABLE Customers"));
    }

    [Test]
    [MethodDataSource(nameof(HostilePages))]
    public async Task ListAsync_HostilePage_IsRejectedBeforeQueryingTheDatabase(string scenario, PageRequest page)
    {
        var (context, counter, customers) = await SeedAsync(200);
        await using var _ = context;

        var watch = Stopwatch.StartNew();
        var result = await customers.ListAsync(page);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.InvalidInputCode).Because(scenario);
        await Assert.That(counter.Count).IsEqualTo(0).Because(scenario);
        await Assert.That(watch.Elapsed).IsLessThan(Fast);
    }

    [Test]
    public async Task ListAsync_LastValidPage_IsAnsweredWithoutLoadingTheTable()
    {
        // Página muito além do total: a contagem já responde (página vazia), sem buscar linhas
        var (context, counter, customers) = await SeedAsync(300);
        await using var _ = context;

        var result = await customers.ListAsync(new PageRequest(int.MaxValue / 100, 100));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Items).IsEmpty();
        await Assert.That(counter.Count).IsEqualTo(0);
    }

    // ---------- Banco real: consulta cara é interrompida ----------

    [Test]
    [Category(TestCategories.Integration)]
    public async Task Dapper_ExpensiveQuery_IsStoppedByTheCommandTimeout()
    {
        await SqlServerFixture.RequireAsync();
        var logs = new CapturingLoggerProvider();
        await using var provider = SqlServerFixture.Build<CodeFirstContext>(logs, o => o.CommandTimeoutSeconds = 1);
        await using var scope = provider.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>();

        var watch = Stopwatch.StartNew();
        var result = await queries.ExecuteScalarAsync<long>(SqlQuery.Create("carga.produto-cartesiano",
            "SELECT COUNT_BIG(*) FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c CROSS JOIN sys.all_objects d"));
        watch.Stop();

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.TimeoutCode);
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(15));
        await Assert.That(logs.AllText).DoesNotContain(SqlServerFixture.Password);

        // A conexão volta ao pool utilizável: a próxima consulta responde normalmente
        var next = await queries.ExecuteScalarAsync<int>(SqlQuery.Create("carga.um", "SELECT 1"));
        await Assert.That(next.Value).IsEqualTo(1);
    }

    [Test]
    [Category(TestCategories.Integration)]
    public async Task Dapper_ExpensiveQuery_IsStoppedByCancellation()
    {
        await SqlServerFixture.RequireAsync();
        await using var provider = SqlServerFixture.Build<CodeFirstContext>(new CapturingLoggerProvider());
        await using var scope = provider.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var watch = Stopwatch.StartNew();
        bool stopped;
        try
        {
            var result = await queries.ExecuteScalarAsync<long>(SqlQuery.Create("carga.produto-cartesiano",
                "SELECT COUNT_BIG(*) FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c CROSS JOIN sys.all_objects d"),
                cancel.Token);
            stopped = result.IsFailure;
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }
        watch.Stop();

        await Assert.That(stopped).IsTrue();
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
    }

    private static string Repeat(string value, int count) => string.Concat(Enumerable.Repeat(value, count));
}
