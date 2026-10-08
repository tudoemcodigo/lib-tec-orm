using Microsoft.Extensions.DependencyInjection;
using TEC.ORM.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.Paging;
using TEC.ORM.Queries;
using TEC.ORM.SqlServer.Security;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Fakes;
using TEC.ORM.Tests.Integration;

namespace TEC.ORM.Tests.Security.Adversarial;

/// <summary>
/// Injeção de SQL contra o SQL Server real: um corpus de cargas clássicas e específicas do T-SQL passa por <b>todas</b> as
/// entradas que costumam vir do usuário (valores de consultas Dapper, chave, filtro LINQ, ordenação, gravação e leitura de
/// volta) e, ao final, uma tabela-canário e um registro-canário precisam continuar intactos.
/// </summary>
/// <remarks>
/// Também prova, no servidor real, que a verificação de somente leitura recusa comandos de escrita colados a uma leitura
/// (o T-SQL não exige separador entre comandos).
/// </remarks>
public class SqlInjectionTests
{
    private const string Canary = "{canario}";

    /// <summary>Cargas de injeção; <c>{canario}</c> é trocado pelo nome da tabela-canário do teste.</summary>
    private static readonly string[] Payloads =
    [
        "' OR '1'='1", "' OR 1=1 --", "' OR ''='", "admin'--", "'; DROP TABLE {canario}; --", "\"; DROP TABLE {canario}; --",
        "1; DROP TABLE {canario}", "1 OR 1=1", "') OR ('1'='1", "'; DELETE FROM codefirst.Categories; --",
        "' UNION SELECT name, name FROM sys.tables --", "'; EXEC('DROP TABLE {canario}'); --", "'; EXEC sp_executesql N'DROP TABLE {canario}'; --",
        "'; WAITFOR DELAY '00:00:05'; --", "\\'; DROP TABLE {canario}; --", "]; DROP TABLE {canario}; --", "'+(SELECT TOP 1 name FROM sys.tables)+'",
        "' AND 1=CONVERT(int,(SELECT @@version)) --", "\0'; DROP TABLE {canario}; --", "''; DROP TABLE {canario} --",
        "＇; DROP TABLE {canario}; --", "ʼ; DROP TABLE {canario}; --", "‘; DROP TABLE {canario}; --", "%27; DROP TABLE {canario}; --",
        "' COLLATE Latin1_General_CI_AS --", "/*' OR 1=1 */", "'/**/OR/**/1=1--", "1'1DROP TABLE {canario}--", "%", "_", "[a-z]%",
    ];

    /// <summary>Cargas puramente de curinga do LIKE: a contagem com <c>Contains</c> tem de ser exata (curinga tratado como texto).</summary>
    private static readonly string[] WildcardPayloads = ["%", "_", "[a-z]%"];

    [Test]
    [Category(TestCategories.Integration)]
    public async Task Payloads_ThroughEveryUserInput_NeverExecuteAndRoundTripExactly()
    {
        await SqlServerFixture.RequireAsync();
        var logs = new CapturingLoggerProvider();
        await using var provider = SqlServerFixture.Build<CodeFirstContext>(logs);
        string suffix = Guid.NewGuid().ToString("N")[..10];
        string canaryTable = $"codefirst.Canario_{suffix}";
        string prefix = "I" + suffix;

        await ExecuteAsync(provider, $"CREATE TABLE {canaryTable} (Id int NOT NULL)");
        try
        {
            await using var scope = provider.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var categories = services.GetRequiredService<IOrmRepository<Category, string>>();
            var queries = services.GetRequiredService<IOrmQueryExecutor>();
            await categories.CreateAsync(new Category { Id = "K" + suffix, Name = "Canário " + suffix });

            var stored = new List<(string Id, string Name)>();
            for (int i = 0; i < Payloads.Length; i++)
            {
                string payload = Payloads[i].Replace(Canary, canaryTable, StringComparison.Ordinal);
                string id = prefix + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);

                // 1. Valor em consulta Dapper (Interpolated): vira parâmetro
                var byValue = await queries.QueryAsync<string>(SqlQuery.Interpolated("injecao.por-valor",
                    $"SELECT Id FROM codefirst.Categories WHERE Name = {payload} OR Id = {payload}"));
                await Assert.That(byValue.IsSuccess).IsTrue().Because(payload);

                // 2. Chave vinda da rota (GetById/Exists): não encontrado, nunca erro
                await Assert.That((await categories.GetByIdAsync(payload)).Error?.Code).IsEqualTo(OrmErrors.NotFoundCode).Because(payload);
                await Assert.That((await categories.ExistsAsync(payload)).Value).IsFalse().Because(payload);

                // 3. Ordenação vinda da query string: lista branca
                await Assert.That((await categories.ListAsync(new PageRequest(1, 10, payload))).Error?.Code).IsEqualTo(OrmErrors.InvalidInputCode).Because(payload);

                // 4. Gravação e leitura de volta exata (segunda ordem: o valor gravado é usado de novo como filtro)
                var created = await categories.CreateAsync(new Category { Id = id, Name = payload });
                await Assert.That(created.IsSuccess).IsTrue().Because(payload);
                stored.Add((id, payload));

                var roundTrip = await queries.QuerySingleOrDefaultAsync<string>(SqlQuery.Interpolated("injecao.segunda-ordem",
                    $"SELECT Name FROM codefirst.Categories WHERE Id = {id} AND Name = {payload}"));
                await Assert.That(roundTrip.Value).IsEqualTo(payload);

                // 5. Filtro LINQ com o valor: igualdade e Contains (curingas do LIKE tratados como texto)
                var found = await categories.FindAsync(c => c.Id.StartsWith(prefix) && c.Name == payload);
                await Assert.That(found.Value.Select(c => c.Id)).Contains(id);
                var contains = await categories.CountAsync(c => c.Id.StartsWith(prefix) && c.Name.Contains(payload));
                await Assert.That(contains.Value).IsGreaterThanOrEqualTo(1L);
                if (WildcardPayloads.Contains(payload))
                    await Assert.That(contains.Value).IsEqualTo(stored.LongCount(s => s.Name.Contains(payload, StringComparison.Ordinal)));
            }

            // Canários intactos: a tabela existe e o registro continua lá; só as linhas gravadas pelo teste foram criadas
            var canaryExists = await queries.ExecuteScalarAsync<int>(SqlQuery.Interpolated("injecao.canario",
                $"SELECT CASE WHEN OBJECT_ID({canaryTable}) IS NULL THEN 0 ELSE 1 END"));
            await Assert.That(canaryExists.Value).IsEqualTo(1);
            await Assert.That((await categories.ExistsAsync("K" + suffix)).Value).IsTrue();
            await Assert.That((await categories.CountAsync(c => c.Id.StartsWith(prefix))).Value).IsEqualTo(Payloads.LongLength);
            await Assert.That(logs.AllText).DoesNotContain(SqlServerFixture.Password);
        }
        finally
        {
            await ExecuteAsync(provider, $"DROP TABLE IF EXISTS {canaryTable}");
        }
    }

    public static IEnumerable<Func<string>> GluedWrites()
    {
        // Comando de escrita colado a uma leitura, sem espaço nem ponto e vírgula: o SQL Server separa os dois comandos
        yield return () => "SELECT 1DROP TABLE {canario}";
        yield return () => "SELECT 1.5DROP TABLE {canario}";
        yield return () => "SELECT 1e5DROP TABLE {canario}";
        yield return () => "SELECT $1DROP TABLE {canario}";
        yield return () => "SELECT 0x1FTRUNCATE TABLE {canario}";
        yield return () => "SELECT 1/**/DROP TABLE {canario}";
        yield return () => "SELECT 'x'DROP TABLE {canario}";
        yield return () => "SELECT [x]=1DROP TABLE {canario}";
        yield return () => "SELECT 1--x\rDROP TABLE {canario}";
        yield return () => "SELECT 1 DISABLE TRIGGER ALL ON {canario}";
    }

    [Test]
    [Category(TestCategories.Integration)]
    [MethodDataSource(nameof(GluedWrites))]
    public async Task ReadOnlyQuery_WithGluedWriteCommand_IsRejectedAndNothingRuns(string template)
    {
        await SqlServerFixture.RequireAsync();
        await using var provider = SqlServerFixture.Build<CodeFirstContext>(new CapturingLoggerProvider());
        string canaryTable = $"codefirst.Canario_{Guid.NewGuid().ToString("N")[..10]}";
        await ExecuteAsync(provider, $"CREATE TABLE {canaryTable} (Id int NOT NULL)");
        try
        {
            await using var scope = provider.CreateAsyncScope();
            var queries = scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>();

            var result = await queries.QueryAsync<int>(SqlQuery.Create("injecao.colada", template.Replace(Canary, canaryTable, StringComparison.Ordinal)));

            await Assert.That(result.Error?.Code).IsEqualTo(OrmErrors.QueryNotAllowedCode);
            var exists = await queries.ExecuteScalarAsync<int>(SqlQuery.Interpolated("injecao.canario",
                $"SELECT CASE WHEN OBJECT_ID({canaryTable}) IS NULL THEN 0 ELSE 1 END"));
            await Assert.That(exists.Value).IsEqualTo(1);
        }
        finally
        {
            await ExecuteAsync(provider, $"DROP TABLE IF EXISTS {canaryTable}");
        }
    }

    /// <summary>DDL do próprio teste (nome da tabela gerado aqui, nunca vindo de fora), pela conexão de escrita do ORM.</summary>
    private static async Task ExecuteAsync(IServiceProvider provider, string sql)
    {
        var opened = await provider.GetRequiredService<IOrmConnectionSecurity>().OpenConnectionAsync(OrmConnectionKind.ReadWrite, CancellationToken.None);
        if (opened.IsFailure)
            throw new InvalidOperationException("Conexão indisponível: " + opened.Error!.Code);

        await using var connection = opened.Value;
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // nome de tabela gerado pelo próprio teste
        command.CommandText = sql;
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }
}
