using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Security;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Persistence;
using TEC.ORM.Abstractions;
using TEC.ORM.Auditing;
using TEC.ORM.Entities;
using TEC.ORM.EntityFrameworkCore.Auditing;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Security;
using TEC.ORM.EntityFrameworkCore.SoftDelete;
using TEC.ORM.MySql.DependencyInjection;
using TEC.ORM.MySql.Dialect;
using TEC.ORM.Oracle.DependencyInjection;
using TEC.ORM.Oracle.Dialect;
using TEC.ORM.PostgreSql.DependencyInjection;
using TEC.ORM.PostgreSql.Dialect;
using TEC.ORM.SqlServer;
using TEC.ORM.SqlServer.DependencyInjection;
using TEC.ORM.Tests.Fakes;
using TEC.Vault.DependencyInjection;
using TEC.Vault.InMemory;

namespace TEC.ORM.Tests;

/// <summary>Nota auditada (trilha histórica e autoria) para os testes de registro dos provedores.</summary>
[Auditable]
public sealed class RegisteredNote : AuditableEntity<Guid>
{
    public string Text { get; set; } = string.Empty;
}

/// <summary>Contexto registrado por cada provedor (<c>AddTecOrm</c>, <c>AddTecOrmPostgreSql</c>, <c>AddTecOrmMySql</c>, <c>AddTecOrmOracle</c>).</summary>
public sealed class RegistrationContext(DbContextOptions<RegistrationContext> options) : OrmDbContext(options)
{
    public DbSet<RegisteredNote> Notes => Set<RegisteredNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddTecOrmHistoricalAudit();
}

/// <summary>
/// Registro de cada provedor no container real: o mesmo pipeline do SQL Server para PostgreSQL, MySQL e Oracle (identidade da
/// auditoria, ordem dos interceptores, convenções, logs sem dados sensíveis, <c>CommandTimeout</c>, mapeador de exceções do
/// TEC.Cqrs e provedor de contexto de auditoria com o <c>ICurrentUser</c>). O salvamento usa os interceptores registrados sobre
/// o provedor InMemory (sem banco).
/// </summary>
public class ProviderRegistrationTests
{
    private const string SecretName = "registro";

    private static readonly Dictionary<string, string> Secrets = new()
    {
        ["SqlServer"] = "Server=tcp:db,1433;Database=app;User ID=u;Password=p;Encrypt=True",
        ["PostgreSql"] = "Host=db;Database=app;Username=u;Password=p;SSL Mode=VerifyFull",
        ["MySql"] = "Server=db;Database=app;User ID=u;Password=p;SSL Mode=VerifyFull",
        ["Oracle"] = "Data Source=tcps://db:2484/app;User Id=u;Password=p"
    };

    private sealed class TestUser(string id, string tenant) : ICurrentUser
    {
        public PrincipalKind Kind => PrincipalKind.User;

        public string? Id => id;

        public string? TenantId => tenant;
    }

    private static ServiceProvider Build(string provider, Action<OrmOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecVault(vault => vault.UseInMemory(o =>
        {
            o.AllowOutsideDevelopment = true;
            o.InitialSecrets[SecretName] = Secrets[provider];
        }));
        services.AddScoped<ICurrentUser>(_ => new TestUser("oid-ana", "contoso"));

        void Configure(OrmOptions o)
        {
            o.ConnectionSecretName = SecretName;
            o.CommandTimeoutSeconds = 45;
            configure?.Invoke(o);
        }

        switch (provider)
        {
            case "SqlServer": services.AddTecOrm<RegistrationContext>(Configure); break;
            case "PostgreSql": services.AddTecOrmPostgreSql<RegistrationContext>(Configure); break;
            case "MySql": services.AddTecOrmMySql<RegistrationContext>(Configure); break;
            case "Oracle": services.AddTecOrmOracle<RegistrationContext>(Configure); break;
            default: throw new ArgumentOutOfRangeException(nameof(provider));
        }
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static Type DialectType(string provider) => provider switch
    {
        "SqlServer" => typeof(SqlServerDialect),
        "PostgreSql" => typeof(PostgreSqlDialect),
        "MySql" => typeof(MySqlDialect),
        _ => typeof(OracleDialect)
    };

    [Test]
    [Arguments("SqlServer")]
    [Arguments("PostgreSql")]
    [Arguments("MySql")]
    [Arguments("Oracle")]
    public async Task Context_is_configured_with_the_secure_pipeline(string provider)
    {
        await using var services = Build(provider);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistrationContext>();
        var options = context.GetService<IDbContextOptions>();
        var core = options.FindExtension<CoreOptionsExtension>()!;

        // Sem string de conexão nas opções: ela vem do cofre na abertura
        await Assert.That(string.IsNullOrEmpty(context.Database.GetConnectionString())).IsTrue();
        await Assert.That(core.IsSensitiveDataLoggingEnabled).IsFalse();
        foreach (var eventId in new[] { CoreEventId.SaveChangesFailed, CoreEventId.QueryIterationFailed, RelationalEventId.CommandError,
                     RelationalEventId.ConnectionError })
            await Assert.That(core.WarningsConfiguration.GetBehavior(eventId)).IsEqualTo(WarningBehavior.Ignore);

        // Ordem: conexão, exclusão lógica, autoria, trilha histórica
        string interceptors = string.Join(",", core.Interceptors!.Select(i => i.GetType().Name));
        await Assert.That(interceptors).IsEqualTo(string.Join(",", nameof(SecretConnectionInterceptor), nameof(SoftDeleteInterceptor),
            nameof(AuditInterceptor), nameof(HistoricalAuditInterceptor)));

        // Identidade da auditoria: o ICurrentUser do escopo
        await Assert.That(OrmIdentity.Current(context)!.Id).IsEqualTo("oid-ana");
        // Convenções e dialeto do contexto, e o tempo limite real dos comandos
        await Assert.That(options.FindExtension<TecOrmOptionsExtension>()!.Dialect!.GetType()).IsEqualTo(DialectType(provider));
        await Assert.That(context.Database.GetCommandTimeout()).IsEqualTo(45);
    }

    [Test]
    [Arguments("SqlServer")]
    [Arguments("PostgreSql")]
    [Arguments("MySql")]
    [Arguments("Oracle")]
    public async Task Common_services_resolve_from_the_container(string provider)
    {
        await using var services = Build(provider);
        await using var scope = services.CreateAsyncScope();

        await Assert.That(scope.ServiceProvider.GetRequiredService<ISqlDialect>().GetType()).IsEqualTo(DialectType(provider));
        await Assert.That(scope.ServiceProvider.GetService<IOrmRepository<RegisteredNote, Guid>>()).IsNotNull();
        await Assert.That(scope.ServiceProvider.GetService<IOrmQueryExecutor>()).IsNotNull();
        await Assert.That(scope.ServiceProvider.GetService<IUnitOfWork>()).IsNotNull();
        await Assert.That(scope.ServiceProvider.GetService<IOrmConnectionSecurity>()).IsNotNull();
        await Assert.That(scope.ServiceProvider.GetRequiredService<IOrmAuditContextProvider>()).IsTypeOf<DefaultOrmAuditContextProvider>();

        // Concorrência e conflito fora do repositório viram 409 no TEC.Cqrs (um mapeador do dialeto)
        var mappers = scope.ServiceProvider.GetServices<IExceptionErrorMapper>().OfType<OrmExceptionErrorMapper>().ToList();
        await Assert.That(mappers.Count).IsEqualTo(1);
        await Assert.That(mappers[0].Translator.GetType()).IsEqualTo(scope.ServiceProvider.GetRequiredService<ISqlDialect>().ExceptionTranslator.GetType());
        await Assert.That(mappers[0].TryMap(new DbUpdateConcurrencyException("linha alterada"), out var errors)).IsTrue();
        await Assert.That(errors![0].Code).IsEqualTo(TEC.ORM.Common.OrmErrors.ConcurrencyCode);
    }

    [Test]
    [Arguments("SqlServer")]
    [Arguments("PostgreSql")]
    [Arguments("MySql")]
    [Arguments("Oracle")]
    public async Task Registered_pipeline_stamps_authorship_and_records_the_trail_with_the_current_user(string provider)
    {
        await using var services = Build(provider);
        await using var scope = services.CreateAsyncScope();
        var registered = scope.ServiceProvider.GetRequiredService<RegistrationContext>().GetService<IDbContextOptions>();
        var interceptors = registered.FindExtension<CoreOptionsExtension>()!.Interceptors!.Where(i => i is not SecretConnectionInterceptor);
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        // Os mesmos interceptores (e o IOrmAuditContextProvider) do container, sobre o InMemory
        string database = Guid.NewGuid().ToString("N");
        RegistrationContext Context()
        {
            var builder = new DbContextOptionsBuilder<RegistrationContext>().UseInMemoryDatabase(database).AddInterceptors(interceptors);
            builder.UseTecOrmIdentity(() => user);
            return new RegistrationContext(builder.Options);
        }

        Guid id;
        await using (var context = Context())
        {
            var created = await TestOrm.Orm<RegisteredNote, Guid>(context).CreateAsync(new RegisteredNote { Text = "a" });
            await Assert.That(created.IsSuccess).IsTrue();
            id = created.Value.Id;
            await Assert.That(created.Value.CreatedBy).IsEqualTo("oid-ana");
        }

        await using (var context = Context())
            await Assert.That((await TestOrm.Orm<RegisteredNote, Guid>(context).DeleteAsync(id)).IsSuccess).IsTrue();

        await using var reading = Context();
        var note = await reading.Notes.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        await Assert.That(note.IsDeleted).IsTrue();
        await Assert.That(note.DeletedBy).IsEqualTo("oid-ana");
        await Assert.That(note.DeletedByTenant).IsEqualTo("contoso");

        var trail = await reading.Set<OrmAuditRecord>().AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        await Assert.That(string.Join(",", trail.Select(r => r.Action))).IsEqualTo("Created,SoftDeleted");
        // Ator da trilha = o mesmo ICurrentUser da autoria (antes o padrão gravava Anonymous)
        await Assert.That(trail.All(r => r.ActorId == "oid-ana" && r.ActorType == "User" && r.TenantId == "contoso")).IsTrue();
        await Assert.That(trail[0].EntityKey).IsEqualTo(id.ToString());
        // A trilha registra os valores carimbados (DeletedBy), não os enviados pelo chamador
        await Assert.That(trail[1].NewValues).Contains("oid-ana");
    }

    [Test]
    [Arguments("SqlServer")]
    [Arguments("PostgreSql")]
    [Arguments("MySql")]
    [Arguments("Oracle")]
    public async Task Contexts_of_every_scope_share_the_internal_ef_service_provider(string provider)
    {
        await using var services = Build(provider);

        // As opções do contexto são montadas a cada escopo (identidade, interceptores, extensão de opções): se alguma entrasse
        // na chave do provedor interno do EF (um ISingletonInterceptor ou uma extensão com hash por instância), cada requisição
        // criaria um provedor novo, com modelo e caches próprios, até o EF lançar ManyServiceProvidersCreatedWarning.
        var singletons = new List<IModelSource>();
        for (int i = 0; i < 25; i++)
        {
            await using var scope = services.CreateAsyncScope();
            singletons.Add(scope.ServiceProvider.GetRequiredService<RegistrationContext>().GetService<IModelSource>());
        }

        await Assert.That(singletons.Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Each_dialect_gets_its_own_exception_mapper_when_contexts_use_different_databases()
    {
        var services = new ServiceCollection();
        services.AddTecOrm<RegistrationContext>(o => o.ConnectionSecretName = SecretName);
        services.AddTecOrmPostgreSql<IdempotencyOnlyContext>(o => o.ConnectionSecretName = SecretName);

        string mappers = string.Join(",", services.Where(d => d.ServiceType == typeof(IExceptionErrorMapper)).Select(d => d.ImplementationInstance)
            .OfType<OrmExceptionErrorMapper>().Select(m => m.Translator.GetType().Name).Order(StringComparer.Ordinal));

        await Assert.That(mappers).IsEqualTo(nameof(PostgreSqlExceptionTranslator) + "," + nameof(SqlServerExceptionTranslator));
    }

    [Test]
    [Arguments("PostgreSql")]
    [Arguments("MySql")]
    [Arguments("Oracle")]
    public async Task Same_context_twice_is_rejected(string provider)
    {
        var services = new ServiceCollection();
        services.AddTecOrm<RegistrationContext>(o => o.ConnectionSecretName = SecretName);

        await Assert.That(() =>
        {
            switch (provider)
            {
                case "PostgreSql": services.AddTecOrmPostgreSql<RegistrationContext>(o => o.ConnectionSecretName = SecretName); break;
                case "MySql": services.AddTecOrmMySql<RegistrationContext>(o => o.ConnectionSecretName = SecretName); break;
                default: services.AddTecOrmOracle<RegistrationContext>(o => o.ConnectionSecretName = SecretName); break;
            }
        }).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Dialect_set_in_configure_is_replaced_by_the_provider_dialect()
    {
        await using var services = Build("PostgreSql", o => o.Dialect = new SqlServerDialect());

        await Assert.That(services.GetRequiredService<OrmOptions>().Dialect).IsTypeOf<PostgreSqlDialect>();
    }
}

/// <summary>Segundo contexto (só idempotência), para os testes de vários contextos.</summary>
public sealed class IdempotencyOnlyContext(DbContextOptions<IdempotencyOnlyContext> options) : DbContext(options);
