using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TEC.Core.Common.Results;
using TEC.Cqrs.Persistence;
using TEC.ORM.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.Paging;
using TEC.ORM.Queries;
using TEC.ORM.Specifications;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Fakes;
using DbFirst = TEC.ORM.Tests.Database.DbFirst;

namespace TEC.ORM.Tests.Integration;

/// <summary>TEC.ORM contra o SQL Server real (banco de testes): code first (migrations), database first, Dapper, transação e health check.</summary>
[Category(TestCategories.Integration)]
public class SqlServerIntegrationTests
{
    private static string Unique() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>Executa o teste num escopo e, ao final, prova que a senha da conexão não apareceu em nenhum log.</summary>
    private static async Task WithScopeAsync<TContext>(Func<IServiceProvider, Task> test) where TContext : DbContext
    {
        await SqlServerFixture.RequireAsync();
        var logs = new CapturingLoggerProvider();
        await using (var provider = SqlServerFixture.Build<TContext>(logs))
        await using (var scope = provider.CreateAsyncScope())
            await test(scope.ServiceProvider);

        await Assert.That(logs.AllText).DoesNotContain(SqlServerFixture.Password);
    }

    private static async Task<(Customer Customer, Product Product)> SeedAsync(IServiceProvider services, string suffix)
    {
        var category = await services.GetRequiredService<IOrmRepository<Category, string>>()
            .CreateAsync(new Category { Id = "C" + suffix, Name = "Category " + suffix });
        var product = await services.GetRequiredService<IOrmRepository<Product, int>>()
            .CreateAsync(new Product { Name = "Product " + suffix, Price = 10m, CategoryId = category.Value.Id });
        var customer = await services.GetRequiredService<IOrmRepository<Customer, Guid>>()
            .CreateAsync(new Customer { Name = "Customer " + suffix, Email = $"{suffix}@exemplo.com" });
        return (customer.Value, product.Value);
    }

    // ---------- Opção 1: code first (migrations) ----------

    [Test]
    public async Task Migrations_applied_on_the_codefirst_schema() => await WithScopeAsync<CodeFirstContext>(async services =>
    {
        var context = services.GetRequiredService<CodeFirstContext>();
        var applied = await context.Database.GetAppliedMigrationsAsync();
        var pending = await context.Database.GetPendingMigrationsAsync();

        await Assert.That(applied.Any()).IsTrue();
        await Assert.That(pending.Any()).IsFalse();
    });

    [Test]
    public async Task Full_crud_with_soft_delete_in_the_database() => await WithScopeAsync<CodeFirstContext>(async services =>
    {
        var customers = services.GetRequiredService<IOrmRepository<Customer, Guid>>();
        string suffix = Unique();

        var created = await customers.CreateAsync(new Customer { Name = "Ana " + suffix, Email = $"ana{suffix}@exemplo.com" });
        var id = created.Value.Id;
        await Assert.That((await customers.GetByIdAsync(id)).Value.Name).IsEqualTo("Ana " + suffix);

        created.Value.Name = "Ana Maria " + suffix;
        await Assert.That((await customers.UpdateAsync(created.Value)).IsSuccess).IsTrue();
        await Assert.That((await customers.ExistsAsync(id)).Value).IsTrue();
        await Assert.That((await customers.CountAsync(c => c.Email == $"ana{suffix}@exemplo.com")).Value).IsEqualTo(1L);

        await Assert.That((await customers.DeleteAsync(id)).IsSuccess).IsTrue();
        await Assert.That((await customers.GetByIdAsync(id)).Error!.Code).IsEqualTo(OrmErrors.NotFoundCode);

        // A linha continua no banco, marcada (lida pelo Dapper, que não aplica o filtro global)
        var row = await services.GetRequiredService<IOrmQueryExecutor>().QuerySingleOrDefaultAsync<(bool IsDeleted, DateTimeOffset? DeletedAt)>(
            SqlQuery.Interpolated("clientes.exclusao", $"SELECT IsDeleted, DeletedAt FROM codefirst.Customers WHERE Id = {id}"));
        await Assert.That(row.Value.IsDeleted).IsTrue();
        await Assert.That(row.Value.DeletedAt).IsNotNull();
    });

    // ---------- Exclusão física ----------

    private static Task<Result<int?>> CountRowsAsync(IServiceProvider services, Guid id) =>
        services.GetRequiredService<IOrmQueryExecutor>().ExecuteScalarAsync<int?>(
            SqlQuery.Interpolated("clientes.linhas", $"SELECT COUNT(*) FROM codefirst.Customers WHERE Id = {id}"));

#pragma warning disable TECORM014 // exclusão física intencional nos testes
    [Test]
    public async Task Hard_delete_actually_removes_the_row() => await WithScopeAsync<CodeFirstContext>(async services =>
    {
        var customers = services.GetRequiredService<IOrmRepository<Customer, Guid>>();
        string suffix = Unique();
        var active = (await customers.CreateAsync(new Customer { Name = "Ivo " + suffix, Email = $"ivo{suffix}@exemplo.com" })).Value.Id;
        var deleted = (await customers.CreateAsync(new Customer { Name = "Jo " + suffix, Email = $"jo{suffix}@exemplo.com" })).Value.Id;
        await customers.DeleteAsync(deleted);

        // Registro ativo e registro já excluído logicamente: os dois saem do banco
        await Assert.That((await customers.HardDeleteAsync(active)).IsSuccess).IsTrue();
        await Assert.That((await customers.HardDeleteAsync(deleted)).IsSuccess).IsTrue();
        await Assert.That((await CountRowsAsync(services, active)).Value).IsEqualTo(0);
        await Assert.That((await CountRowsAsync(services, deleted)).Value).IsEqualTo(0);

        // A instância rastreada foi desanexada: não volta a ser gravada
        await Assert.That(services.GetRequiredService<CodeFirstContext>().ChangeTracker.Entries<Customer>()
            .Any(e => e.Entity.Id == active || e.Entity.Id == deleted)).IsFalse();

        await Assert.That((await customers.HardDeleteAsync(active)).Error!.Code).IsEqualTo(OrmErrors.NotFoundCode);
    });

    [Test]
    public async Task Hard_delete_with_restricted_dependent_is_conflict() => await WithScopeAsync<CodeFirstContext>(async services =>
    {
        string suffix = Unique();
        var (customer, product) = await SeedAsync(services, suffix);
        await services.GetRequiredService<IOrmRepository<Order, long>>()
            .CreateAsync(new Order { CustomerId = customer.Id, ProductId = product.Id, Quantity = 1, Amount = 1m, CreatedOn = DateTimeOffset.UtcNow });

        var result = await services.GetRequiredService<IOrmRepository<Customer, Guid>>().HardDeleteAsync(customer.Id);

        // FK com Restrict: nada é excluído
        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConflictCode);
        await Assert.That((await CountRowsAsync(services, customer.Id)).Value).IsEqualTo(1);
    });

    [Test]
    public async Task Batch_hard_delete_purges_only_the_deleted_matching_the_criteria() => await WithScopeAsync<CodeFirstContext>(async services =>
    {
        var customers = services.GetRequiredService<IOrmRepository<Customer, Guid>>();
        string suffix = Unique();
        string domain = $"@{suffix}.exemplo.com";
        var ids = new List<Guid>();
        foreach (string name in new[] { "ka", "leo", "mia" })
            ids.Add((await customers.CreateAsync(new Customer { Name = name + suffix, Email = name + domain })).Value.Id);
        await customers.DeleteAsync(ids[0]);
        await customers.DeleteAsync(ids[1]);

        var limit = DateTimeOffset.UtcNow.AddMinutes(1);
        var result = await customers.HardDeleteAsync(c => c.IsDeleted && c.DeletedAt < limit && c.Email.EndsWith(domain));

        await Assert.That(result.Value).IsEqualTo(2L);
        await Assert.That((await CountRowsAsync(services, ids[0])).Value).IsEqualTo(0);
        await Assert.That((await CountRowsAsync(services, ids[1])).Value).IsEqualTo(0);
        await Assert.That((await customers.ExistsAsync(ids[2])).Value).IsTrue();
    });

    [Test]
    public async Task Hard_delete_undone_by_transaction_rollback() => await WithScopeAsync<CodeFirstContext>(async services =>
    {
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var customers = services.GetRequiredService<IOrmRepository<Customer, Guid>>();
        string suffix = Unique();
        var id = (await customers.CreateAsync(new Customer { Name = "Nina " + suffix, Email = $"nina{suffix}@exemplo.com" })).Value.Id;

        await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        await Assert.That((await customers.HardDeleteAsync(id)).IsSuccess).IsTrue();
        await unitOfWork.RollbackAsync(CancellationToken.None);

        await Assert.That((await customers.ExistsAsync(id)).Value).IsTrue();
        await customers.HardDeleteAsync(id);
    });
#pragma warning restore TECORM014

    [Test]
    public async Task Duplicate_email_is_conflict_without_the_value_in_the_log() => await WithScopeAsync<CodeFirstContext>(async services =>
    {
        var customers = services.GetRequiredService<IOrmRepository<Customer, Guid>>();
        string email = $"dup{Unique()}@exemplo.com";
        var first = await customers.CreateAsync(new Customer { Name = "Primeiro", Email = email });

        var duplicate = await customers.CreateAsync(new Customer { Name = "Segundo", Email = email });
        await Assert.That(duplicate.Error!.Code).IsEqualTo(OrmErrors.ConflictCode);
        await Assert.That(duplicate.Error.Message).DoesNotContain(email);

        // Índice único filtrado: depois da exclusão lógica, o e-mail pode ser reaproveitado
        await customers.DeleteAsync(first.Value.Id);
        await Assert.That((await customers.CreateAsync(new Customer { Name = "Terceiro", Email = email })).IsSuccess).IsTrue();
    });

    [Test]
    public async Task Paged_sorted_listing_on_sql_server() => await WithScopeAsync<CodeFirstContext>(async services =>
    {
        var customers = services.GetRequiredService<IOrmRepository<Customer, Guid>>();
        string suffix = Unique();
        foreach (string name in (string[])["Carla", "Ana", "Bruno"])
            await customers.CreateAsync(new Customer { Name = $"{name} {suffix}", Email = $"{name}{suffix}@exemplo.com" });

        var page = await customers.ListAsync(new PageRequest(1, 2, SortBy: "name"),
            new Specification<Customer>(c => c.Name.EndsWith(suffix)));

        await Assert.That(page.Value.TotalItems).IsEqualTo(3L);
        await Assert.That(page.Value.Items.Select(c => c.Name)).IsEquivalentTo([$"Ana {suffix}", $"Bruno {suffix}"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    });

    [Test]
    public async Task UnitOfWork_rollback_undoes_the_writes() => await WithScopeAsync<CodeFirstContext>(async services =>
    {
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var customers = services.GetRequiredService<IOrmRepository<Customer, Guid>>();
        string email = $"tx{Unique()}@exemplo.com";

        await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        await Assert.That(unitOfWork.HasActiveTransaction).IsTrue();
        await customers.CreateAsync(new Customer { Name = "Na transação", Email = email });
        await unitOfWork.RollbackAsync(CancellationToken.None);

        await Assert.That(unitOfWork.HasActiveTransaction).IsFalse();
        await Assert.That((await customers.ExistsAsync(c => c.Email == email)).Value).IsFalse();

        await unitOfWork.BeginTransactionAsync(CancellationToken.None);
        await customers.CreateAsync(new Customer { Name = "Confirmado", Email = email });
        await unitOfWork.CommitAsync(CancellationToken.None);
        await Assert.That((await customers.ExistsAsync(c => c.Email == email)).Value).IsTrue();
    });

    // ---------- Dapper: leituras complexas ----------

    [Test]
    public async Task Dapper_aggregation_and_join_with_parameters() => await WithScopeAsync<CodeFirstContext>(async services =>
    {
        string suffix = Unique();
        var (customer, product) = await SeedAsync(services, suffix);
        var orders = services.GetRequiredService<IOrmRepository<Order, long>>();
        foreach (decimal amount in (decimal[])[10m, 20m, 30m])
            await orders.CreateAsync(new Order { CustomerId = customer.Id, ProductId = product.Id, Quantity = 1, Amount = amount, CreatedOn = DateTimeOffset.UtcNow });
        var queries = services.GetRequiredService<IOrmQueryExecutor>();

        var total = await queries.ExecuteScalarAsync<decimal>(SqlQuery.Create("pedidos.total-por-cliente",
            "SELECT SUM(Amount) FROM codefirst.Orders WHERE CustomerId = @CustomerId AND IsDeleted = 0", new { CustomerId = customer.Id }));

        var summary = await queries.QueryAsync<CustomerSummary>(SqlQuery.Interpolated("clientes.resumo",
            $"""
            SELECT c.Name, COUNT(p.Id) AS Orders, SUM(p.Amount) AS Total
            FROM codefirst.Customers c
            JOIN codefirst.Orders p ON p.CustomerId = c.Id AND p.IsDeleted = 0
            WHERE c.Id = {customer.Id} AND c.IsDeleted = 0
            GROUP BY c.Name
            """));

        var join = await queries.QueryAsync<Order, Customer, Order>(SqlQuery.Interpolated("pedidos.com-cliente",
            $"SELECT p.*, c.* FROM codefirst.Orders p JOIN codefirst.Customers c ON c.Id = p.CustomerId WHERE c.Id = {customer.Id}"),
            (p, c) => { p.Customer = c; return p; });

        await Assert.That(total.Value).IsEqualTo(60m);
        await Assert.That(summary.Value.Single()).IsEqualTo(new CustomerSummary(customer.Name, 3, 60m));
        await Assert.That(join.Value.Count).IsEqualTo(3);
        await Assert.That(join.Value.All(p => p.Customer!.Email == customer.Email)).IsTrue();
    });

    [Test]
    public async Task Dapper_injection_through_value_has_no_effect() => await WithScopeAsync<CodeFirstContext>(async services =>
    {
        var (customer, _) = await SeedAsync(services, Unique());
        var queries = services.GetRequiredService<IOrmQueryExecutor>();
        string attack = "' OR 1=1; DROP TABLE codefirst.Customers; --";

        var result = await queries.QueryAsync<Guid>(SqlQuery.Interpolated("clientes.por-nome",
            $"SELECT Id FROM codefirst.Customers WHERE Name = {attack}"));

        await Assert.That(result.Value.Count).IsEqualTo(0);
        await Assert.That((await services.GetRequiredService<IOrmRepository<Customer, Guid>>().ExistsAsync(customer.Id)).Value).IsTrue();
    });

    [Test]
    public async Task Dapper_rejects_writes() => await WithScopeAsync<CodeFirstContext>(async services =>
    {
        var result = await services.GetRequiredService<IOrmQueryExecutor>().QueryAsync<int>(
            SqlQuery.Create("ataque", "SELECT 1 DELETE FROM codefirst.Customers"));

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.QueryNotAllowedCode);
    });

    // ---------- Mapeamento DTO: projeção traduzida pelo SQL Server ----------

    [Test]
    public async Task Nested_dto_projection_on_sql_server() => await WithScopeAsync<CodeFirstContext>(async services =>
    {
        string suffix = Unique();
        var (customer, product) = await SeedAsync(services, suffix);
        var orders = services.GetRequiredService<IOrmRepository<Order, long>>();
        var created = await orders.CreateAsync(new Order { CustomerId = customer.Id, ProductId = product.Id, Quantity = 2, Amount = 20m, CreatedOn = DateTimeOffset.UtcNow });
        await orders.CreateAsync(new Order { CustomerId = customer.Id, ProductId = product.Id, Quantity = 1, Amount = 5m, CreatedOn = DateTimeOffset.UtcNow });
        services.GetRequiredService<CodeFirstContext>().ChangeTracker.Clear();

        // Objeto, achatamento em dois níveis e coleção com ciclo limitado (Pedido → Cliente → Pedidos → Cliente cortado)
        var order = await orders.GetByIdAsync<Mapping.OrderDto>(created.Value.Id);
        await Assert.That(order.Value.CustomerName).IsEqualTo(customer.Name);
        await Assert.That(order.Value.CategoryName).IsEqualTo("Category " + suffix);
        await Assert.That(order.Value.Product!.Category!.Id).IsEqualTo("C" + suffix);
        await Assert.That(order.Value.Customer!.Orders.Count).IsEqualTo(2);
        await Assert.That(order.Value.Customer.Orders.All(p => p.Customer is null)).IsTrue();

        // Lista paginada de DTOs com coleção aninhada; ordenação por propriedade da entidade
        var customers = services.GetRequiredService<IOrmRepository<Customer, Guid>>();
        var page = await customers.ListAsync<Mapping.CustomerDto>(new PageRequest(1, 5, SortBy: "name"),
            new Specification<Customer>(c => c.Id == customer.Id));
        await Assert.That(page.Value.Items.Single().Orders.Sum(p => p.Amount)).IsEqualTo(25m);

        // Excluído logicamente some também da coleção aninhada (filtro global aplicado na subconsulta)
        await orders.DeleteAsync(created.Value.Id);
        services.GetRequiredService<CodeFirstContext>().ChangeTracker.Clear();
        var after = await customers.GetByIdAsync<Mapping.CustomerDto>(customer.Id);
        await Assert.That(after.Value.Orders.Count).IsEqualTo(1);
    });

    // ---------- Opção 2: database first ----------

    [Test]
    public async Task Database_first_crud_with_soft_delete() => await WithScopeAsync<DbFirst.DbFirstContext>(async services =>
    {
        var customers = services.GetRequiredService<IOrmRepository<DbFirst.Customer, Guid>>();
        var orders = services.GetRequiredService<IOrmRepository<DbFirst.Order, long>>();
        string suffix = Unique();

        var customer = await customers.CreateAsync(new DbFirst.Customer { Name = "DB First " + suffix, Email = $"dbf{suffix}@exemplo.com", Active = true });
        await Assert.That(customer.Value.Id).IsNotEqualTo(Guid.Empty);   // NEWSEQUENTIALID() do banco

        var order = await orders.CreateAsync(new DbFirst.Order { CustomerId = customer.Value.Id, Description = "Order " + suffix, Amount = 12.5m });
        await Assert.That(order.Value.Id).IsGreaterThan(0L);   // IDENTITY

        var list = await orders.FindAsync(new Specification<DbFirst.Order>(p => p.CustomerId == customer.Value.Id).Include(nameof(DbFirst.Order.Customer)));
        await Assert.That(list.Value.Single().Customer.Name).IsEqualTo("DB First " + suffix);

        await Assert.That((await orders.DeleteAsync(order.Value.Id)).IsSuccess).IsTrue();
        await Assert.That((await orders.CountAsync(p => p.CustomerId == customer.Value.Id)).Value).IsEqualTo(0L);
        var context = services.GetRequiredService<DbFirst.DbFirstContext>();
        await Assert.That(await context.Orders.IgnoreQueryFilters().CountAsync(p => p.Id == order.Value.Id && p.IsDeleted)).IsEqualTo(1);
    });

    // ---------- Conexão vinda do Azure Key Vault ----------

    [Test]
    public async Task Connection_read_from_azure_key_vault_through_tec_vault()
    {
        await SqlServerFixture.RequireAsync();
        Skip.When(SqlServerFixture.Source != ConnectionSource.KeyVault, "Conexão de testes veio do cofre em memória (TEC_TESTES_ORM_SQL_SEGREDO não definido), não do Key Vault.");
        var logs = new CapturingLoggerProvider();
        await using var provider = SqlServerFixture.Build<CodeFirstContext>(logs);
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IOrmRepository<Customer, Guid>>().CountAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        // Auditoria do TEC.Vault: leitura do segredo no provedor Azure Key Vault (o nome do item aparece; o valor, nunca)
        await Assert.That(logs.AllText).Contains(TestDatabase.KeyVaultSecretName!);
        await Assert.That(logs.AllText).DoesNotContain(SqlServerFixture.Password);
    }

    // ---------- Health check e falhas de conexão ----------

    [Test]
    public async Task Health_check_healthy_with_the_vault_connection() => await WithScopeAsync<CodeFirstContext>(async services =>
    {
        var report = await services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        await Assert.That(report.Status).IsEqualTo(HealthStatus.Healthy);
    });

    [Test]
    public async Task Wrong_password_becomes_connection_unavailable_without_leaking()
    {
        await SqlServerFixture.RequireAsync();
        var logs = new CapturingLoggerProvider();
        const string wrongPassword = "Senha-Errada-Que-Nao-Pode-Vazar-123";
        var wrong = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(SqlServerFixture.ConnectionString) { Password = wrongPassword };

        await using var provider = SqlServerFixture.Build<CodeFirstContext>(logs, secretOverride: wrong.ConnectionString);
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IOrmRepository<Customer, Guid>>().CountAsync();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConnectionUnavailableCode);
        await Assert.That(report.Status).IsEqualTo(HealthStatus.Unhealthy);
        await Assert.That(report.Entries["tec-orm"].Description).IsEqualTo(OrmErrors.ConnectionUnavailableCode);
        await Assert.That(logs.AllText).DoesNotContain(wrongPassword);
        await Assert.That(logs.AllText).Contains("SQL 18456");
    }

    private sealed record CustomerSummary(string Name, int Orders, decimal Total);
}
