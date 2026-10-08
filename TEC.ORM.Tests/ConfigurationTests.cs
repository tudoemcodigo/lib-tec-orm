using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using TEC.Vault.DependencyInjection;
using TEC.Vault.InMemory;
using TEC.ORM.Abstractions;
using TEC.ORM.Entities;
using TEC.ORM.SqlServer.DependencyInjection;
using TEC.ORM.SqlServer.Security;
using TEC.ORM.SqlServer.SoftDelete;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests;

/// <summary>Contexto que não herda de OrmDbContext nem chama AddTecOrmConventions.</summary>
public sealed class NoConventionsContext(DbContextOptions<NoConventionsContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Customer>().Ignore(c => c.Orders);
}

/// <summary>Documento com filtro de inquilino (tenant), para a combinação com o filtro da exclusão lógica.</summary>
public sealed class Document : Entity<int>
{
    public int Tenant { get; set; }
}

/// <summary>Filtro do inquilino: nomeado (EF Core 10) ou anônimo (EF Core 8 e 10).</summary>
public sealed class TenantContext(DbContextOptions<TenantContext> options, bool named) : OrmDbContext(options)
{
    public bool Named { get; } = named;

    public DbSet<Document> Documents => Set<Document>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
#if NET10_0_OR_GREATER
        if (Named)
        {
            modelBuilder.Entity<Document>().HasQueryFilter("Tenant", d => d.Tenant == 1);
            return;
        }
#endif
        modelBuilder.Entity<Document>().HasQueryFilter(d => d.Tenant == 1);
    }
}

/// <summary>Filtro global obrigatório, registro automático da convenção, filtros nomeados (EF 10) e vários contextos no DI.</summary>
public class ConfigurationTests
{
    private const string SecretA = "Server=tcp:servidor-a,1433;Database=dbA;User ID=app;Password=x;Encrypt=True";
    private const string SecretB = "Server=tcp:servidor-b,1433;Database=dbB;User ID=app;Password=y;Encrypt=True";

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecVault(c => c.UseInMemory(o =>
        {
            o.AllowOutsideDevelopment = true;
            o.InitialSecrets["segredo-a"] = SecretA;
            o.InitialSecrets["segredo-b"] = SecretB;
        }));
        return services;
    }

    /// <summary>Quantas vezes os filtros da entidade testam IsDeleted.</summary>
    private static int SoftDeleteChecks(IReadOnlyEntityType entityType)
    {
#if NET10_0_OR_GREATER
        return entityType.GetDeclaredQueryFilters().Sum(f => Count(f.Expression!.ToString()));
#else
        return entityType.GetQueryFilter() is { } filter ? Count(filter.ToString()) : 0;
#endif
        static int Count(string text) => text.Split(nameof(ISoftDelete.IsDeleted)).Length - 1;
    }

    // ---------- Filtro global obrigatório ----------

    [Test]
    public async Task Repository_rejects_context_without_the_soft_delete_filter()
    {
        await using var context = new NoConventionsContext(new DbContextOptionsBuilder<NoConventionsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

        var exception = await Assert.That(() => TestOrm.Orm<Customer, Guid>(context)).Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("AddTecOrm");
        await Assert.That(exception.Message).Contains(nameof(Customer));
    }

    [Test]
    public async Task AddTecOrm_applies_the_filter_even_without_OrmDbContext()
    {
        var services = Services();
        services.AddTecOrm<NoConventionsContext>(o => o.ConnectionSecretName = "segredo-a");
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<NoConventionsContext>();

        await Assert.That(SoftDeleteChecks(context.Model.FindEntityType(typeof(Customer))!)).IsEqualTo(1);
        await Assert.That(scope.ServiceProvider.GetRequiredService<IOrmRepository<Customer, Guid>>()).IsNotNull();
    }

    [Test]
    public async Task OrmDbContext_with_AddTecOrm_does_not_duplicate_the_filter()
    {
        var services = Services();
        services.AddTecOrm<CodeFirstContext>(o => o.ConnectionSecretName = "segredo-a");
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();

        var model = scope.ServiceProvider.GetRequiredService<CodeFirstContext>().Model;

        foreach (var type in new[] { typeof(Customer), typeof(Order), typeof(Product), typeof(Category) })
            await Assert.That(SoftDeleteChecks(model.FindEntityType(type)!)).IsEqualTo(1);
    }

    // ---------- Filtros nomeados (EF Core 10) e anônimos ----------

    private static TenantContext Tenant(string database, bool named) =>
        new(new DbContextOptionsBuilder<TenantContext>()
            .UseInMemoryDatabase(database)
            .AddInterceptors(new SoftDeleteInterceptor(TimeProvider.System))
            .ReplaceService<IModelCacheKeyFactory, NamedFilterCacheKeyFactory>()
            .Options, named);

    private sealed class NamedFilterCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            context is TenantContext tenant ? (context.GetType(), tenant.Named, designTime) : (object)(context.GetType(), designTime);
    }

    private static async Task<int[]> VisibleAsync(string database, bool named, bool ignoreSoftDelete = false)
    {
        await using var context = Tenant(database, named);
        IQueryable<Document> query = context.Documents.AsNoTracking();
#if NET10_0_OR_GREATER
        if (ignoreSoftDelete)
            query = query.IgnoreQueryFilters([OrmModelConfigurationExtensions.SoftDeleteFilterName]);
#endif
        return [.. (await query.Select(d => d.Id).ToListAsync()).Order()];
    }

    private static async Task SeedAsync(string database, bool named)
    {
        await using var context = Tenant(database, named);
        context.Documents.AddRange(new Document { Id = 1, Tenant = 1 }, new Document { Id = 2, Tenant = 2 }, new Document { Id = 3, Tenant = 1 });
        await context.SaveChangesAsync();
        await Assert.That((await TestOrm.Orm<Document, int>(context).DeleteAsync(3)).IsSuccess).IsTrue();
    }

    [Test]
    public async Task Application_anonymous_filter_is_combined_with_soft_delete()
    {
        string database = Guid.NewGuid().ToString("N");
        await SeedAsync(database, named: false);

        await Assert.That(await VisibleAsync(database, named: false)).IsEquivalentTo([1]);
    }

#if NET10_0_OR_GREATER
    [Test]
    public async Task EF10_application_named_filter_coexists_with_the_soft_delete_named_filter()
    {
        string database = Guid.NewGuid().ToString("N");
        await SeedAsync(database, named: true);

        await using (var context = Tenant(database, named: true))
        {
            var filters = context.Model.FindEntityType(typeof(Document))!.GetDeclaredQueryFilters().Select(f => f.Key!).Order().ToList();
            await Assert.That(filters).IsEquivalentTo(["Tenant", OrmModelConfigurationExtensions.SoftDeleteFilterName]);
        }

        await Assert.That(await VisibleAsync(database, named: true)).IsEquivalentTo([1]);
        // Desliga só a exclusão lógica: o filtro do inquilino continua valendo
        await Assert.That(await VisibleAsync(database, named: true, ignoreSoftDelete: true)).IsEquivalentTo([1, 3]);
    }
#endif

    /// <summary>
    /// Base da exclusão física: desliga só a exclusão lógica (enxerga o excluído 3) e mantém o filtro do inquilino (esconde o 2),
    /// com filtro anônimo (combinado pela convenção) ou nomeado.
    /// </summary>
    [Test]
    [Arguments(false)]
#if NET10_0_OR_GREATER
    [Arguments(true)]
#endif
    public async Task Ignoring_only_soft_delete_keeps_the_application_filter(bool named)
    {
        string database = Guid.NewGuid().ToString("N");
        await SeedAsync(database, named);

        await using var context = Tenant(database, named);
        var query = SoftDeleteQueryFilterConvention.IgnoreSoftDeleteFilter(context.Documents.AsNoTracking(), context.Documents.EntityType, context);
        int[] visible = [.. (await query.Select(d => d.Id).ToListAsync()).Order()];

        await Assert.That(visible).IsEquivalentTo([1, 3]);
    }

    [Test]
    public async Task Ignoring_only_soft_delete_without_application_filter_sees_all()
    {
        await using var context = TestOrm.InMemoryContext();
        var customers = TestOrm.Orm<Customer, Guid>(context);
        var id = (await customers.CreateAsync(new Customer { Name = "Hugo", Email = "hugo@exemplo.com" })).Value.Id;
        await customers.DeleteAsync(id);

        var query = SoftDeleteQueryFilterConvention.IgnoreSoftDeleteFilter(context.Customers.AsNoTracking(), context.Customers.EntityType, context);

        await Assert.That(await query.Select(c => c.Id).ToListAsync()).IsEquivalentTo([id]);
    }

    // ---------- Vários contextos ----------

    [Test]
    public async Task AddTecOrm_twice_with_the_same_context_is_rejected()
    {
        var services = Services();
        services.AddTecOrm<CodeFirstContext>(o => o.ConnectionSecretName = "segredo-a");

        var exception = await Assert.That(() => services.AddTecOrm<CodeFirstContext>(o => o.ConnectionSecretName = "segredo-b"))
            .Throws<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains(nameof(CodeFirstContext));
    }

    [Test]
    public async Task Each_context_uses_its_own_secret_and_options()
    {
        var services = Services();
        services.AddTecOrm<CodeFirstContext>(o => o.ConnectionSecretName = "segredo-a");
        services.AddTecOrm<NoConventionsContext>(o =>
        {
            o.ConnectionSecretName = "segredo-b";
            o.CommandTimeoutSeconds = 77;
            o.ApplicationName = "Segundo";
        });
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();

        var first = await AppliedConnectionAsync(scope.ServiceProvider.GetRequiredService<CodeFirstContext>());
        var second = await AppliedConnectionAsync(scope.ServiceProvider.GetRequiredService<NoConventionsContext>());

        await Assert.That(first.DataSource).IsEqualTo("tcp:servidor-a,1433");
        await Assert.That(first.ApplicationName).IsEqualTo("TEC.ORM");
        await Assert.That(second.DataSource).IsEqualTo("tcp:servidor-b,1433");
        await Assert.That(second.ApplicationName).IsEqualTo("Segundo");
        await Assert.That(second.CommandTimeout).IsEqualTo(77);
        await Assert.That(scope.ServiceProvider.GetRequiredService<NoConventionsContext>().Database.GetCommandTimeout()).IsEqualTo(77);
        // Serviços globais são do contexto principal (o primeiro registrado)
        await Assert.That(scope.ServiceProvider.GetRequiredService<DbContext>()).IsTypeOf<CodeFirstContext>();
    }

    /// <summary>Conexão que o interceptor do contexto entrega ao abrir (sem abrir de fato).</summary>
    private static async Task<SqlConnectionStringBuilder> AppliedConnectionAsync(DbContext context)
    {
        var interceptor = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!.Interceptors!
            .OfType<SecretConnectionInterceptor>().Single();
        await using var connection = new SqlConnection();
        await interceptor.ConnectionOpeningAsync(connection, null!, default);
        return new SqlConnectionStringBuilder(connection.ConnectionString);
    }
}
