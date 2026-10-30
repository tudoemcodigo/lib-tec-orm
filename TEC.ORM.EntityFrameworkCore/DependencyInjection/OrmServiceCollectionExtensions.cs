using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TEC.Core.Security;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Idempotency;
using TEC.Cqrs.Persistence;
using TEC.ORM.Abstractions;
using TEC.ORM.Auditing;
using TEC.ORM.EntityFrameworkCore.Auditing;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Diagnostics;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Idempotency;
using TEC.ORM.EntityFrameworkCore.Internal;
using TEC.ORM.EntityFrameworkCore.Security;
using TEC.ORM.EntityFrameworkCore.SoftDelete;
using TEC.ORM.EntityFrameworkCore.UnitOfWork;
using TEC.Vault.Abstractions;

namespace TEC.ORM.EntityFrameworkCore.DependencyInjection;

/// <summary>
/// Registro comum do TEC.ORM no container, usado pelos provedores (<c>AddTecOrm</c> do SQL Server, <c>AddTecOrmPostgreSql</c>,
/// <c>AddTecOrmMySql</c> e <c>AddTecOrmOracle</c>).
/// </summary>
public static class OrmServiceCollectionExtensions
{
    /// <summary>
    /// Registra o <typeparamref name="TContext"/> com o pipeline seguro do TEC.ORM, para o banco do <paramref name="dialect"/>:
    /// <list type="bullet">
    /// <item><description>o contexto (scoped), configurado por <paramref name="useDatabase"/> <b>sem</b> string de conexão (ela
    /// vem do TEC.Vault na abertura, pelo <see cref="IOrmConnectionSecurity"/>), com <c>CommandTimeout</c>, as convenções
    /// (filtro de exclusão lógica, colunas de auditoria), a identidade da operação (<c>ICurrentUser</c> do escopo),
    /// <c>EnableSensitiveDataLogging</c> desligado e os eventos de erro do EF Core que repetiriam a mensagem do banco
    /// silenciados;</description></item>
    /// <item><description>os interceptores, nesta ordem: conexão, exclusão lógica, autoria (<c>CreatedBy</c>/<c>UpdatedBy</c>/
    /// <c>DeletedBy</c>) e trilha histórica (com <see cref="OrmOptions.EnableHistoricalAudit"/>);</description></item>
    /// <item><description><see cref="IOrmRepository{TEntity, TKey}"/>, <see cref="IOrmQueryExecutor"/>, <see cref="IUnitOfWork"/>,
    /// <see cref="IOrmOperationRunner"/>, <see cref="IOrmConnectionSecurity"/>, o <see cref="ISqlDialect"/> e o
    /// <see cref="IOrmAuditContextProvider"/> padrão (com o <c>ICurrentUser</c> da operação);</description></item>
    /// <item><description>um <see cref="IExceptionErrorMapper"/> (TEC.Cqrs) por dialeto, que converte concorrência e conflito
    /// lançados fora do repositório (ex.: no commit do <see cref="IUnitOfWork"/>) em 409.</description></item>
    /// </list>
    /// Requer o TEC.Vault registrado (<c>AddTecVault</c>). Os serviços globais usam <c>TryAdd</c> (registre antes a sua
    /// implementação para substituir) e pertencem ao primeiro contexto registrado; cada contexto usa as próprias opções.
    /// </summary>
    /// <typeparam name="TContext">Contexto da aplicação.</typeparam>
    /// <param name="services">Container.</param>
    /// <param name="dialect">Dialeto do banco (substitui o <see cref="OrmOptions.Dialect"/> informado em <paramref name="configure"/>).</param>
    /// <param name="configure">Opções do TEC.ORM.</param>
    /// <param name="useDatabase">
    /// Configura o provedor do EF Core sem string de conexão (ex.: <c>(b, o) =&gt; b.UseNpgsql(n =&gt; n.CommandTimeout(o.CommandTimeoutSeconds))</c>).
    /// </param>
    /// <returns>O próprio <paramref name="services"/>.</returns>
    /// <exception cref="InvalidOperationException">O mesmo <typeparamref name="TContext"/> já foi registrado.</exception>
    public static IServiceCollection AddTecOrmDbContext<TContext>(this IServiceCollection services, ISqlDialect dialect,
        Action<OrmOptions> configure, Action<DbContextOptionsBuilder, OrmOptions> useDatabase) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(useDatabase);

        if (services.Any(d => d.IsKeyedService && d.ServiceType == typeof(OrmOptions) && Equals(d.ServiceKey, typeof(TContext))))
            throw new InvalidOperationException(
                $"O TEC.ORM já foi registrado para {typeof(TContext).Name}: registre cada contexto uma única vez (as opções de uma " +
                "segunda chamada não seriam usadas).");

        var options = new OrmOptions();
        configure(options);
        options.Dialect = dialect;
        options.Validate();

        // O primeiro contexto registrado é o principal: dono dos serviços globais (OrmOptions, IOrmConnectionSecurity, DbContext,
        // IOrmRepository, IOrmQueryExecutor, IUnitOfWork). Os demais têm opções e conexão próprias (por tipo do contexto).
        bool primary = !services.Any(d => !d.IsKeyedService && d.ServiceType == typeof(OrmOptions));
        services.AddTecOrmCoreServices<TContext>(options);

        services.AddKeyedSingleton<OrmOptions>(typeof(TContext), options);
        services.AddKeyedSingleton<ISqlDialect>(typeof(TContext), dialect);
        services.AddKeyedSingleton<SecretConnectionInterceptor>(typeof(TContext), (provider, _) => primary
            ? provider.GetRequiredService<SecretConnectionInterceptor>()
            : CreateConnectionInterceptor(provider, options, SecurityFor(provider, options)));
        // Teto da propagação da exclusão lógica (OrmOptions.MaxSoftDeletePropagation) de cada contexto
        services.AddKeyedSingleton<SoftDeleteInterceptor>(typeof(TContext), (provider, _) => primary
            ? provider.GetRequiredService<SoftDeleteInterceptor>()
            : new SoftDeleteInterceptor(provider.GetRequiredService<TimeProvider>(), options.MaxSoftDeletePropagation));
        // Limite de tamanho da trilha (OrmOptions.MaxAuditValueLength) de cada contexto
        services.AddKeyedSingleton<HistoricalAuditInterceptor>(typeof(TContext), (provider, _) => primary
            ? provider.GetRequiredService<HistoricalAuditInterceptor>()
            : new HistoricalAuditInterceptor(provider.GetRequiredService<IOrmAuditContextProvider>(),
                provider.GetRequiredService<TimeProvider>(), options.MaxAuditValueLength));

        services.AddDbContext<TContext>((provider, builder) => ConfigureDbContext(provider, builder, options,
            provider.GetRequiredKeyedService<SecretConnectionInterceptor>(typeof(TContext)),
            provider.GetRequiredKeyedService<SoftDeleteInterceptor>(typeof(TContext)),
            options.EnableHistoricalAudit ? provider.GetRequiredKeyedService<HistoricalAuditInterceptor>(typeof(TContext)) : null,
            useDatabase,
            // Opções do AddDbContext são scoped: provider é o escopo do contexto
            () => provider.GetService<ICurrentUser>()));

        services.TryAddScoped<DbContext>(provider => provider.GetRequiredService<TContext>());

        // Concorrência, deadlock e violação de chave lançados fora do repositório (ex.: no commit do IUnitOfWork) viram 409
        var translatorType = dialect.ExceptionTranslator.GetType();
        if (!services.Any(d => d.ServiceType == typeof(IExceptionErrorMapper) && d.ImplementationInstance is OrmExceptionErrorMapper mapper
                               && mapper.Translator.GetType() == translatorType))
            services.AddSingleton<IExceptionErrorMapper>(new OrmExceptionErrorMapper(dialect.ExceptionTranslator));
        return services;
    }

    /// <summary>
    /// Registra os serviços comuns do TEC.ORM (repositório genérico, executor Dapper, Unit of Work, interceptores, segurança da
    /// conexão, dialeto e provedor de contexto de auditoria) com <c>TryAdd</c>, sem o contexto. Prefira
    /// <see cref="AddTecOrmDbContext{TContext}"/>, que chama este método.
    /// </summary>
    public static IServiceCollection AddTecOrmCoreServices<TContext>(this IServiceCollection services, OrmOptions options)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        if (options.Dialect is { } dialect)
            services.TryAddSingleton(dialect);
        // Ator da trilha histórica: o ICurrentUser da operação (durante o salvamento, o mesmo do contexto que preenche CreatedBy)
        services.TryAddSingleton<IOrmAuditContextProvider>(provider =>
            new DefaultOrmAuditContextProvider(OrmAuditIdentity.CreateAccessor(provider.GetRequiredService<IServiceScopeFactory>())));
        services.TryAddSingleton<IOrmConnectionSecurity, OrmConnectionSecurity>();
        services.TryAddSingleton<IOrmOperationRunner, OrmOperationRunner>();
        services.TryAddSingleton(provider => CreateConnectionInterceptor(provider, provider.GetRequiredService<OrmOptions>(),
            provider.GetRequiredService<IOrmConnectionSecurity>()));
        services.TryAddSingleton(provider => new SoftDeleteInterceptor(provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<OrmOptions>().MaxSoftDeletePropagation));
        services.TryAddSingleton<AuditInterceptor>();
        services.TryAddSingleton(provider => new HistoricalAuditInterceptor(provider.GetRequiredService<IOrmAuditContextProvider>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<OrmOptions>().MaxAuditValueLength));

        services.TryAddScoped(typeof(IOrmRepository<,>), typeof(OrmRepository<,>));
        services.TryAddScoped<IOrmQueryExecutor, OrmQueryExecutor>();
        services.TryAddScoped<IUnitOfWork, OrmUnitOfWork>();
        return services;
    }

    /// <summary>
    /// Registra o <see cref="EfIdempotencyStore{TContext}"/> como <see cref="IIdempotencyStore"/> (Scoped), usado pela
    /// idempotência HTTP do <c>TEC.Cqrs.AspNetCore</c> (<c>AddIdempotency</c>/<c>UseTecIdempotency</c>), e a limpeza periódica
    /// das chaves expiradas. O contexto precisa mapear a tabela com
    /// <see cref="IdempotencyModelBuilderExtensions.AddTecIdempotency"/> (e a migration aplicada) e ser registrado pelo TEC.ORM
    /// (de onde vem o dialeto).
    /// </summary>
    /// <typeparam name="TContext">Contexto registrado com <c>AddTecOrm...&lt;TContext&gt;</c>.</typeparam>
    /// <param name="services">Container.</param>
    /// <param name="configure">Opções da limpeza.</param>
    /// <returns>O próprio <paramref name="services"/>.</returns>
    /// <exception cref="InvalidOperationException">Chamado mais de uma vez, ou outro <see cref="IIdempotencyStore"/> já registrado.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Opções fora dos limites.</exception>
    public static IServiceCollection AddTecOrmIdempotency<TContext>(this IServiceCollection services, Action<OrmIdempotencyOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        var existing = services.FirstOrDefault(d => !d.IsKeyedService && d.ServiceType == typeof(IIdempotencyStore));
        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"Já existe um IIdempotencyStore registrado ({existing.ImplementationType?.Name ?? "factory/instância"}). " +
                "Registre um único store: AddTecOrmIdempotency<TContext>() ou AddInMemoryIdempotencyStore().");
        }

        var options = new OrmIdempotencyOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        // Fábrica explícita: o dialeto vem do registro do próprio contexto (um ISqlDialect do container seria o do principal)
        services.TryAddScoped(provider => new EfIdempotencyStore<TContext>(provider.GetRequiredService<TContext>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<IIdempotencyStore>(sp => sp.GetRequiredService<EfIdempotencyStore<TContext>>());
        if (options.EnableCleanup)
        {
            services.AddSingleton(options);
            services.AddHostedService<IdempotencyCleanupService<TContext>>();
        }

        return services;
    }

    /// <summary>
    /// Configura um <c>DbContextOptionsBuilder</c> com o pipeline do contexto <b>principal</b> (opções, conexão e interceptores),
    /// para <c>AddDbContextFactory</c> ou um contexto montado à mão. A identidade da operação é resolvida num escopo próprio a
    /// cada gravação (<see cref="OrmIdentity.SnapshotCurrentUser"/>).
    /// </summary>
    internal static DbContextOptionsBuilder UseTecOrmPipeline(DbContextOptionsBuilder builder, IServiceProvider provider,
        Action<DbContextOptionsBuilder, OrmOptions> useDatabase)
    {
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var options = provider.GetRequiredService<OrmOptions>();
        ConfigureDbContext(provider, builder, options, provider.GetRequiredService<SecretConnectionInterceptor>(),
            provider.GetRequiredService<SoftDeleteInterceptor>(),
            options.EnableHistoricalAudit ? provider.GetRequiredService<HistoricalAuditInterceptor>() : null,
            useDatabase, () => OrmIdentity.SnapshotCurrentUser(scopes));
        return builder;
    }

    /// <summary>
    /// Conexão de um contexto secundário: com o <see cref="OrmConnectionSecurity"/> padrão, uma instância com as opções do próprio
    /// contexto (segredo, política, aplicação, dialeto); um <see cref="IOrmConnectionSecurity"/> substituído pela aplicação é usado
    /// por todos.
    /// </summary>
    private static IOrmConnectionSecurity SecurityFor(IServiceProvider provider, OrmOptions options)
    {
        var shared = provider.GetRequiredService<IOrmConnectionSecurity>();
        return shared is OrmConnectionSecurity
            ? new OrmConnectionSecurity(provider.GetRequiredService<ISecretReader>(), options,
                provider.GetRequiredService<ILogger<OrmConnectionSecurity>>(), options.Dialect, provider.GetService<TimeProvider>())
            : shared;
    }

    /// <summary>Interceptor da conexão de um contexto, com o circuito próprio do contexto (<see cref="OrmOptions.CircuitBreaker"/>).</summary>
    private static SecretConnectionInterceptor CreateConnectionInterceptor(IServiceProvider provider, OrmOptions options,
        IOrmConnectionSecurity security) =>
        new(security, OrmConnectionCircuit.Create(options, OrmConnectionKind.ReadWrite, provider.GetService<TimeProvider>(),
            (ILogger?)provider.GetService<ILogger<SecretConnectionInterceptor>>() ?? NullLogger.Instance, options.Dialect), options.Dialect);

    private static void ConfigureDbContext(IServiceProvider provider, DbContextOptionsBuilder builder, OrmOptions options,
        SecretConnectionInterceptor connection, SoftDeleteInterceptor softDelete, HistoricalAuditInterceptor? history,
        Action<DbContextOptionsBuilder, OrmOptions> useDatabase, Func<ICurrentUser?> currentUser)
    {
        // Sem string de conexão aqui: o SecretConnectionInterceptor aplica a do TEC.Vault ao abrir
        useDatabase(builder, options);

        // Ordem: a exclusão lógica vira alteração de IsDeleted; a autoria preenche DeletedBy/UpdatedBy (e recusa sem identidade);
        // a trilha histórica registra os valores já carimbados, com a exclusão lógica como SoftDeleted
        var audit = provider.GetRequiredService<AuditInterceptor>();
        if (history is null)
            builder.AddInterceptors(connection, softDelete, audit);
        else
            builder.AddInterceptors(connection, softDelete, audit, history);

        // Identidade da operação (auditoria): o ICurrentUser (TEC.Core), lido a cada uso
        builder.UseTecOrmIdentity(currentUser);
        // Convenções (filtro de exclusão lógica, auditoria, idempotência) mesmo sem OrmDbContext/AddTecOrmConventions, e as
        // opções e o dialeto do contexto para elas
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new TecOrmOptionsExtension(options));
        // Valores de parâmetros nunca nos logs do EF Core
        builder.EnableSensitiveDataLogging(false);
        // Nem as exceções do banco: os eventos de erro do EF Core registram a exceção do driver, cuja mensagem repete valores
        // (chave duplicada, conversão) e o login. O OrmOperationRunner já registra a falha sem eles (tipo, código e erro do banco).
        builder.ConfigureWarnings(warnings => warnings.Ignore(
            CoreEventId.SaveChangesFailed, CoreEventId.QueryIterationFailed, CoreEventId.OptimisticConcurrencyException,
            RelationalEventId.CommandError, RelationalEventId.ConnectionError, RelationalEventId.TransactionError));
    }
}
