using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Security;
using TEC.ORM.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.Queries;
using TEC.ORM.Specifications;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests.Integration;

/// <summary>
/// Regressões da revisão técnica de 2026-10-19 contra o SQL Server real: executor dentro de <c>TransactionScope</c> (BUG-01),
/// vários resultados contornando o <c>MaxQueryRows</c> (SEG-04) e autoria/linhas no log da exclusão física (SEG-02).
/// </summary>
[Category(TestCategories.Integration)]
public class RevisionIntegrationTests
{
    private static string Unique() => Guid.NewGuid().ToString("N")[..10];

    private sealed class Operator(string id) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public PrincipalKind Kind => PrincipalKind.User;

        public string? Id => id;

        public string? TenantId => null;
    }

    private static async Task WithScopeAsync(CapturingLoggerProvider logs, Func<IServiceProvider, Task> test,
        Action<IServiceCollection>? register = null)
    {
        await SqlServerFixture.RequireAsync();
        await using (var provider = SqlServerFixture.Build<CodeFirstContext>(logs, register: register))
        await using (var scope = provider.CreateAsyncScope())
            await test(scope.ServiceProvider);

        await Assert.That(logs.AllText).DoesNotContain(SqlServerFixture.Password);
    }

    [Test]
    public async Task Query_executor_inside_transaction_scope_with_the_repository_is_not_promoted() =>
        await WithScopeAsync(new CapturingLoggerProvider(), async services =>
        {
            // BUG-01: com a conexão do EF Core aberta no TransactionScope, a do executor (outra string: ApplicationIntent=ReadOnly)
            // se alistaria e a transação seria promovida a distribuída (PlatformNotSupportedException no Linux)
            var customers = services.GetRequiredService<IOrmRepository<Customer, Guid>>();
            var queries = services.GetRequiredService<IOrmQueryExecutor>();
            var context = services.GetRequiredService<CodeFirstContext>();
            string suffix = Unique();

            using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                await context.Database.OpenConnectionAsync();
                try
                {
                    var created = await customers.CreateAsync(new Customer { Name = "Escopo " + suffix, Email = $"{suffix}@exemplo.com" });
                    var count = await queries.ExecuteScalarAsync<int>(SqlQuery.Interpolated("clientes.contagem",
                        $"SELECT COUNT(*) FROM codefirst.Customers WHERE Email = {suffix + "@exemplo.com"}"));

                    await Assert.That(created.IsSuccess).IsTrue();
                    // Fora da transação: não enxerga o cliente ainda não confirmado (READ_COMMITTED_SNAPSHOT: lê a versão confirmada)
                    await Assert.That(count.Error?.Code).IsNull();
                    await Assert.That(count.Value).IsEqualTo(0);
                    await Assert.That(Transaction.Current!.TransactionInformation.DistributedIdentifier).IsEqualTo(Guid.Empty);
                }
                finally
                {
                    await context.Database.CloseConnectionAsync();
                }
                // Sem Complete: desfeito
            }

            await Assert.That((await customers.ExistsAsync(c => c.Email == suffix + "@exemplo.com")).Value).IsFalse();
        });

    [Test]
    public async Task Second_result_set_is_rejected_without_being_read() =>
        await WithScopeAsync(new CapturingLoggerProvider(), async services =>
        {
            // SEG-04: sem ';' o guarda aceita; o segundo resultado (1 milhão de linhas) seria drenado ao fechar o leitor
            var queries = services.GetRequiredService<IOrmQueryExecutor>();
            var watch = System.Diagnostics.Stopwatch.StartNew();

            var result = await queries.QueryAsync<int>(SqlQuery.Create("varios-resultados",
                """
                SELECT 1 AS X
                SELECT TOP (1000000) a.object_id FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c
                """));
            var scalar = await queries.ExecuteScalarAsync<int>(SqlQuery.Create("varios-resultados-escalar", "SELECT 1 SELECT 2"));
            var single = await queries.QuerySingleOrDefaultAsync<int>(SqlQuery.Create("varios-resultados-unico", "SELECT 1 SELECT 2"));
            var plain = await queries.QueryAsync<int>(SqlQuery.Create("um-resultado", "SELECT 1 AS X"));

            await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.QueryNotAllowedCode);
            await Assert.That(scalar.Error!.Code).IsEqualTo(OrmErrors.QueryNotAllowedCode);
            await Assert.That(single.Error!.Code).IsEqualTo(OrmErrors.QueryNotAllowedCode);
            await Assert.That(plain.Value.Single()).IsEqualTo(1);
            await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        });

    [Test]
    public async Task Hard_delete_audit_log_has_actor_and_rows()
    {
        // SEG-02: o 3007 responde "quem" e "quanto", inclusive no lote
        var logs = new CapturingLoggerProvider();
        string suffix = Unique();
        await WithScopeAsync(logs, async services =>
        {
            var customers = services.GetRequiredService<IOrmRepository<Customer, Guid>>();
            for (int i = 0; i < 3; i++)
                await customers.CreateAsync(new Customer { Name = "Lote " + suffix, Email = $"{suffix}-{i}@exemplo.com" });

#pragma warning disable TECORM014 // exclusão física é o objeto do teste
            var removed = await customers.HardDeleteAsync(new Specification<Customer>(c => c.Name == "Lote " + suffix));
#pragma warning restore TECORM014

            await Assert.That(removed.Value).IsEqualTo(3L);
        }, register: services => services.AddScoped<ICurrentUser>(_ => new Operator("7f3c6a1e-0d2b-4c55-9a8e-2b1d4c6e8f00")));

        var entry = logs.Entries.Single(e => e.EventId.Id == 3007);
        await Assert.That(entry.Text).Contains("por 7f3c6a1e-0d2b-4c55-9a8e-2b1d4c6e8f00 removeu 3 linha(s)");
    }
}
