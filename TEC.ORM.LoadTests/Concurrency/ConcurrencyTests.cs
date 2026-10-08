using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.LoadTests.Infrastructure;
using TEC.ORM.Paging;
using TEC.ORM.Queries;
using TEC.ORM.Specifications;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.Internal;
using TEC.ORM.SqlServer.Security;
using TEC.Vault.Abstractions;
using TEC.Vault.DependencyInjection;
using TEC.Vault.InMemory;

namespace TEC.ORM.LoadTests.Concurrency;

/// <summary>
/// Os tipos que o <c>AddTecOrm</c> registra como Singleton (opções, observabilidade, interceptores de exclusão lógica e de
/// auditoria, segurança da conexão) e as peças estáticas (verificação de somente leitura, <c>SqlQuery</c>, mapeamento gerado)
/// são usados por muitas threads ao mesmo tempo: cada teste compara o resultado concorrente com o esperado de cada worker.
/// Sem banco (EF Core InMemory): roda no CI a cada push.
/// </summary>
[Category(TestCategories.LoadCi)]
public class ConcurrencyTests
{
    private const int Workers = 64;

    [Test]
    public async Task Repository_ConcurrentScopes_EveryWorkerSeesOnlyItsOwnConsistentData()
    {
        var orm = new InMemoryOrm();
        const int iterations = 40;
        var divergences = new Divergences();

        await Parallel.ForAsync(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (worker, ct) =>
        {
            var user = new TestUser().As(worker);
            for (int i = 0; i < iterations; i++)
            {
                // Um contexto por "requisição", como num escopo de DI; os singletons são os mesmos para todos
                await using var context = orm.NewContext(user);
                var accounts = orm.Orm<Account, Guid>(context);

                var created = await accounts.CreateAsync(new Account { Name = $"w{worker:D3}-{i:D3}", Email = $"w{worker}-{i}@carga", Category = worker, Balance = i }, ct);
                if (created.IsFailure)
                {
                    divergences.Add($"worker {worker}: criar falhou com {created.Error!.Code}");
                    continue;
                }

                var id = created.Value.Id;
                var read = await accounts.GetByIdAsync(id, ct);
                if (read.Value?.Name != $"w{worker:D3}-{i:D3}" || read.Value.CreatedBy != user.Id || read.Value.CreatedByTenant != user.TenantId)
                    divergences.Add($"worker {worker}: leu {read.Value?.Name} criado por {read.Value?.CreatedBy}");

                created.Value.Balance += 1;
                var updated = await accounts.UpdateAsync(created.Value, ct);
                if (updated.IsFailure || updated.Value.UpdatedBy != user.Id)
                    divergences.Add($"worker {worker}: atualizar → {updated.Error?.Code ?? updated.Value.UpdatedBy}");

                if (i % 3 == 0)
                {
                    var deleted = await accounts.DeleteAsync(id, ct);
                    var gone = await accounts.GetByIdAsync(id, ct);
                    if (deleted.IsFailure || gone.Error?.Code != OrmErrors.NotFoundCode)
                        divergences.Add($"worker {worker}: excluir → {deleted.Error?.Code}, depois {gone.Error?.Code ?? "found"}");
                }

                var page = await accounts.ListAsync<AccountSummaryDto>(new PageRequest(1, 100, "Name"),
                    new Specification<Account>(c => c.Category == worker), ct);
                if (page.IsFailure || page.Value.Items.Any(c => c.Category != worker))
                    divergences.Add($"worker {worker}: página com dados de outro worker ou falha {page.Error?.Code}");
            }
        });

        await Assert.That(divergences.Count).IsEqualTo(0).Because(divergences.ToString());

        // Estado final: cada worker tem exatamente as suas contas não excluídas, com a autoria certa e o saldo atualizado
        await using var check = orm.NewContext();
        var all = await check.Accounts.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        int expectedAlive = Workers * (iterations - (iterations + 2) / 3);
        await Assert.That(all.Count).IsEqualTo(Workers * iterations);
        await Assert.That(all.Count(c => !c.IsDeleted)).IsEqualTo(expectedAlive);
        await Assert.That(all.All(c => c.CreatedBy == $"oid-worker-{c.Category:D3}" && c.UpdatedBy == c.CreatedBy)).IsTrue();
        await Assert.That(all.Where(c => c.IsDeleted).All(c => c.DeletedBy == c.CreatedBy && c.DeletedAt is not null)).IsTrue();
        await Assert.That(all.All(c => c.Balance == int.Parse(c.Name[^3..], System.Globalization.CultureInfo.InvariantCulture) + 1)).IsTrue();
    }

    [Test]
    public async Task ReadOnlyGuard_ConcurrentChecks_MatchSequentialVerdicts()
    {
        string[] corpus =
        [
            "SELECT * FROM t", "SELECT 1 DROP TABLE t", "SELECT 1DROP TABLE t", "WITH c AS (SELECT 1 a) SELECT a FROM c",
            "SELECT 'DELETE' AS x", "SELECT [Update] FROM t", "SELECT 1 /* a /* b */ c */ DELETE FROM t", "SELECT 0x1FRECONFIGURE",
            "SELECT a FROM t ORDER BY a OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY", "SELECT NEXT VALUE FOR s", "SELECT 1 --x\rDROP TABLE t",
            "SELECT 1; SELECT 2", "EXEC sp_who", "SELECT 1 DISABLE TRIGGER ALL ON t", "SELECT N'it''s' AS s", "SELECT 'sem fechar",
        ];
        var expected = corpus.Select(ReadOnlySqlGuard.Check).ToArray();
        var divergences = new Divergences();

        await Parallel.ForAsync(0, 200_000, new ParallelOptions { MaxDegreeOfParallelism = Workers }, (i, _) =>
        {
            int q = i % corpus.Length;
            if (ReadOnlySqlGuard.Check(corpus[q]) != expected[q])
                divergences.Add($"{corpus[q]}: veredito diferente do sequencial");

            var query = SqlQuery.Interpolated("carga.paralela", $"SELECT * FROM t WHERE a = {i} AND b = {corpus[q]}");
            if (query.Sql != "SELECT * FROM t WHERE a = @p0 AND b = @p1" || !Equals(query.Parameters["p0"], i))
                divergences.Add($"SqlQuery {i}: {query.Sql}");
            return ValueTask.CompletedTask;
        });

        await Assert.That(divergences.Count).IsEqualTo(0).Because(divergences.ToString());
    }

    [Test]
    public async Task ConnectionSecurity_ConcurrentBuilds_AreIdenticalAndWithinPolicy()
    {
        // Singleton do AddTecOrm: monta a string de conexão a cada nova conexão do EF Core e do Dapper, em paralelo
        const string secret = "Server=tcp:db.interno,1433;Database=dbCarga;User ID=app;Password=SenhaCarga!1;Encrypt=True;Persist Security Info=True";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecVault(vault => vault
            .UseInMemory(o =>
            {
                o.AllowOutsideDevelopment = true;
                o.InitialSecrets["carga-sql"] = secret;
            })
            .EnableSecretCache(TimeSpan.FromMinutes(5)));
        await using var provider = services.BuildServiceProvider();
        var options = new OrmOptions { ConnectionSecretName = "carga-sql", ApplicationName = "Carga" };
        var security = new OrmConnectionSecurity(provider.GetRequiredService<ISecretReader>(), options, NullLogger<OrmConnectionSecurity>.Instance);

        var reference = (await security.BuildConnectionStringAsync(OrmConnectionKind.ReadWrite, CancellationToken.None)).Value.Reveal();
        var readOnlyReference = (await security.BuildConnectionStringAsync(OrmConnectionKind.ReadOnly, CancellationToken.None)).Value.Reveal();
        var divergences = new Divergences();

        await Parallel.ForAsync(0, 20_000, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (i, ct) =>
        {
            var kind = i % 2 == 0 ? OrmConnectionKind.ReadWrite : OrmConnectionKind.ReadOnly;
            var built = await security.BuildConnectionStringAsync(kind, ct);
            string value = built.Value.Reveal();
            if (value != (kind == OrmConnectionKind.ReadWrite ? reference : readOnlyReference))
                divergences.Add($"{kind}: conexão diferente da referência");
        });

        var applied = new SqlConnectionStringBuilder(reference);
        await Assert.That(divergences.Count).IsEqualTo(0).Because(divergences.ToString());
        await Assert.That(applied.PersistSecurityInfo).IsFalse();
        await Assert.That(applied.ApplicationName).IsEqualTo("Carga");
        await Assert.That(new SqlConnectionStringBuilder(readOnlyReference).ApplicationIntent).IsEqualTo(ApplicationIntent.ReadOnly);
    }

    [Test]
    public async Task OperationRunner_HashedIdentifiers_AreDeterministicAcrossThreads()
    {
        var logger = new SqlServer.Diagnostics.OrmOperationRunner(NullLogger<SqlServer.Diagnostics.OrmOperationRunner>.Instance,
            new OrmOptions { ConnectionSecretName = "x", IdentifierLogMode = IdentifierLogMode.Hashed });
        var expected = Enumerable.Range(0, 1_000).Select(i => logger.FormatIdentifier($"cpf-{i:D11}")).ToArray();
        var divergences = new Divergences();

        await Parallel.ForAsync(0, 100_000, new ParallelOptions { MaxDegreeOfParallelism = Workers }, (i, _) =>
        {
            int k = i % expected.Length;
            if (logger.FormatIdentifier($"cpf-{k:D11}") != expected[k])
                divergences.Add($"hash diferente para {k}");
            return ValueTask.CompletedTask;
        });

        await Assert.That(divergences.Count).IsEqualTo(0).Because(divergences.ToString());
        await Assert.That(expected.Distinct().Count()).IsEqualTo(expected.Length);
    }
}
