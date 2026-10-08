using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TEC.Vault.Abstractions;
using TEC.Cqrs.Persistence;
using TEC.ORM.Abstractions;
using TEC.Core.Security;
using TEC.ORM.SqlServer.Auditing;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.Diagnostics;
using TEC.ORM.SqlServer.Security;
using TEC.ORM.SqlServer.SoftDelete;
using TEC.ORM.SqlServer.UnitOfWork;

namespace TEC.ORM.SqlServer.DependencyInjection;

/// <summary>Registro do TEC.ORM (SQL Server) no container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o TEC.ORM com o <typeparamref name="TContext"/>:
    /// <list type="bullet">
    /// <item><description><c>TContext</c> (scoped) com <c>UseSqlServer()</c> <b>sem</b> string de conexão: ela vem do TEC.Vault
    /// na abertura (<see cref="IOrmConnectionSecurity"/>), com exclusão lógica ao salvar e <c>EnableSensitiveDataLogging</c> desligado;</description></item>
    /// <item><description><see cref="IOrmRepository{TEntity, TKey}"/> para qualquer entidade do contexto (genérico aberto);</description></item>
    /// <item><description><see cref="IOrmQueryExecutor"/> (Dapper, conexão de leitura);</description></item>
    /// <item><description><see cref="IUnitOfWork"/> (TEC.Cqrs), <see cref="IOrmOperationRunner"/> e <see cref="IOrmConnectionSecurity"/>.</description></item>
    /// </list>
    /// Requer o TEC.Vault registrado (<c>AddTecVault</c>), que fornece o <c>ISecretReader</c>. Os serviços usam <c>TryAdd</c>:
    /// registre antes a sua implementação para substituir.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddTecVault(vault => vault.UseAzureKeyVault(...).EnableSecretCache(TimeSpan.FromMinutes(5)));
    /// builder.Services.AddTecOrm&lt;VendasContext&gt;(orm =>
    /// {
    ///     orm.ConnectionSecretName = "vendas-sql-escrita";       // nome do segredo, não a conexão
    ///     orm.ReadOnlyConnectionSecretName = "vendas-sql-leitura";
    /// });
    /// builder.Services.AddHealthChecks().AddTecOrm();
    /// </code>
    /// </example>
    /// <remarks>
    /// <para>O filtro global de exclusão lógica é aplicado ao modelo do contexto mesmo que ele não herde de
    /// <see cref="OrmDbContext"/> nem chame <c>AddTecOrmConventions</c> (as duas formas continuam valendo, sem duplicar o filtro).</para>
    /// <para>Vários contextos: chame uma vez para cada um; cada contexto usa as <b>próprias</b> opções (segredo da conexão,
    /// política, tempo limite). O primeiro registrado é o principal: <c>DbContext</c>, <see cref="IOrmRepository{TEntity, TKey}"/>,
    /// <see cref="IOrmQueryExecutor"/>, <see cref="IUnitOfWork"/>, <see cref="OrmOptions"/> e <see cref="IOrmConnectionSecurity"/> do container
    /// são dele. Um <see cref="IOrmConnectionSecurity"/> próprio da aplicação atende a todos os contextos.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">O mesmo <typeparamref name="TContext"/> já foi registrado.</exception>
    /// <param name="services">Container.</param>
    /// <param name="configure">Opções do TEC.ORM.</param>
    /// <param name="sqlServer">
    /// Ajustes do provedor SQL Server do EF Core (ex.: <c>MigrationsHistoryTable</c>). Não use <c>EnableRetryOnFailure</c>: é
    /// incompatível com as transações do <see cref="IUnitOfWork"/> e repetiria escritas; as leituras já têm novas tentativas
    /// seguras (<see cref="OrmOptions.TransientRetryCount"/>).
    /// </param>
    /// <returns>O próprio <paramref name="services"/>.</returns>
    public static IServiceCollection AddTecOrm<TContext>(this IServiceCollection services, Action<OrmOptions> configure,
        Action<SqlServerDbContextOptionsBuilder>? sqlServer = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(d => d.IsKeyedService && d.ServiceType == typeof(OrmOptions) && Equals(d.ServiceKey, typeof(TContext))))
            throw new InvalidOperationException(
                $"AddTecOrm<{typeof(TContext).Name}> já foi chamado: registre cada contexto uma única vez (as opções de uma segunda " +
                "chamada não seriam usadas).");

        var options = new OrmOptions();
        configure(options);
        options.Validate();

        // O primeiro contexto registrado é o principal: dono dos serviços globais (OrmOptions, IOrmConnectionSecurity, DbContext,
        // IOrmRepository, IOrmQueryExecutor, IUnitOfWork). Os demais têm opções e conexão próprias (por tipo do contexto).
        bool primary = !services.Any(d => !d.IsKeyedService && d.ServiceType == typeof(OrmOptions));

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOrmConnectionSecurity, OrmConnectionSecurity>();
        services.TryAddSingleton<IOrmOperationRunner, OrmOperationRunner>();
        services.TryAddSingleton<SecretConnectionInterceptor>();
        services.TryAddSingleton<SoftDeleteInterceptor>();
        services.TryAddSingleton<AuditInterceptor>();

        services.AddKeyedSingleton<OrmOptions>(typeof(TContext), options);
        services.AddKeyedSingleton<SecretConnectionInterceptor>(typeof(TContext), (provider, _) => primary
            ? provider.GetRequiredService<SecretConnectionInterceptor>()
            : new SecretConnectionInterceptor(SecurityFor(provider, options)));

        services.AddDbContext<TContext>((provider, builder) => ConfigureDbContext(provider, builder, options,
            provider.GetRequiredKeyedService<SecretConnectionInterceptor>(typeof(TContext)), sqlServer,
            // Opções do AddDbContext são scoped: provider é o escopo do contexto
            () => provider.GetService<ICurrentUser>()));

        services.TryAddScoped<DbContext>(provider => provider.GetRequiredService<TContext>());
        services.TryAddScoped(typeof(IOrmRepository<,>), typeof(OrmRepository<,>));
        services.TryAddScoped<IOrmQueryExecutor, OrmQueryExecutor>();
        services.TryAddScoped<IUnitOfWork, OrmUnitOfWork>();
        return services;
    }

    /// <summary>
    /// Configura um <c>DbContextOptionsBuilder</c> com o pipeline seguro do TEC.ORM (útil para <c>AddDbContextFactory</c> ou para
    /// um contexto montado à mão). Usa as opções e a conexão do contexto <b>principal</b> (o primeiro registrado com
    /// <see cref="AddTecOrm{TContext}"/>); para outro segredo ou outras opções, registre o contexto com
    /// <see cref="AddTecOrm{TContext}"/>.
    /// </summary>
    /// <remarks>
    /// Com <c>AddDbContextFactory</c> o <paramref name="provider"/> é o raiz, de onde um <see cref="ICurrentUser"/> scoped não pode
    /// ser lido (seria o mesmo para todas as requisições). Por isso o <see cref="ICurrentUser"/> da auditoria é resolvido num escopo
    /// próprio a cada gravação e copiado (id, tenant e tipo) antes de o escopo ser descartado, então a auditoria nunca usa uma
    /// instância já descartada: implementações ambientais (as do TEC.Security, que leem o usuário do <c>HttpContext</c> ou do
    /// <c>AsyncLocal</c>) dão o usuário da operação. Para um <see cref="ICurrentUser"/> que dependa de estado do escopo da
    /// requisição, informe-o com <c>UseTecOrmIdentity</c> depois desta chamada.
    /// </remarks>
    public static DbContextOptionsBuilder UseTecOrm(this DbContextOptionsBuilder builder, IServiceProvider provider,
        Action<SqlServerDbContextOptionsBuilder>? sqlServer = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(provider);
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        ConfigureDbContext(provider, builder, provider.GetRequiredService<OrmOptions>(), provider.GetRequiredService<SecretConnectionInterceptor>(),
            sqlServer, () => SnapshotCurrentUser(scopes));
        return builder;
    }

    /// <summary>
    /// Resolve o <see cref="ICurrentUser"/> num escopo próprio desta gravação e devolve uma cópia imutável dos dados (o escopo, e
    /// com ele uma implementação descartável, é encerrado aqui). <c>null</c> sem <see cref="ICurrentUser"/> registrado.
    /// </summary>
    internal static ICurrentUser? SnapshotCurrentUser(IServiceScopeFactory scopes)
    {
        using var scope = scopes.CreateScope();
        var user = scope.ServiceProvider.GetService<ICurrentUser>();
        return user is null ? null : new CurrentUserSnapshot(user.Kind, user.Id, user.TenantId);
    }

    /// <summary>Cópia imutável da identidade de uma operação.</summary>
    private sealed record CurrentUserSnapshot(PrincipalKind Kind, string? Id, string? TenantId) : ICurrentUser;

    /// <summary>
    /// Conexão de um contexto secundário: com o <see cref="OrmConnectionSecurity"/> padrão, uma instância com as opções do próprio
    /// contexto (segredo, política, aplicação); um <see cref="IOrmConnectionSecurity"/> substituído pela aplicação é usado por todos.
    /// </summary>
    private static IOrmConnectionSecurity SecurityFor(IServiceProvider provider, OrmOptions options)
    {
        var shared = provider.GetRequiredService<IOrmConnectionSecurity>();
        return shared is OrmConnectionSecurity
            ? new OrmConnectionSecurity(provider.GetRequiredService<ISecretReader>(), options, provider.GetRequiredService<ILogger<OrmConnectionSecurity>>())
            : shared;
    }

    private static void ConfigureDbContext(IServiceProvider provider, DbContextOptionsBuilder builder, OrmOptions options,
        SecretConnectionInterceptor connection, Action<SqlServerDbContextOptionsBuilder>? sqlServer, Func<ICurrentUser?> currentUser)
    {
        // Sem string de conexão aqui: o SecretConnectionInterceptor aplica a do TEC.Vault ao abrir
        builder.UseSqlServer(sql =>
        {
            sql.CommandTimeout(options.CommandTimeoutSeconds);
            sqlServer?.Invoke(sql);
        });
        // Ordem: a exclusão lógica vira alteração de IsDeleted antes da auditoria preencher DeletedBy
        builder.AddInterceptors(connection, provider.GetRequiredService<SoftDeleteInterceptor>(), provider.GetRequiredService<AuditInterceptor>());
        // Identidade da operação (auditoria): o ICurrentUser (TEC.Core), lido a cada uso
        builder.UseTecOrmIdentity(currentUser);
        // Filtro global de exclusão lógica mesmo sem OrmDbContext/AddTecOrmConventions (idempotente com eles)
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(
            builder.Options.FindExtension<TecOrmOptionsExtension>() ?? new TecOrmOptionsExtension());
        // Valores de parâmetros nunca nos logs do EF Core
        builder.EnableSensitiveDataLogging(false);
        // Nem as exceções do banco: os eventos de erro do EF Core registram a SqlException, cuja mensagem repete valores (chave
        // duplicada, conversão) e o login. O OrmOperationRunner já registra a falha sem eles (tipo, número do erro SQL e código).
        builder.ConfigureWarnings(warnings => warnings.Ignore(
            CoreEventId.SaveChangesFailed, CoreEventId.QueryIterationFailed, CoreEventId.OptimisticConcurrencyException,
            RelationalEventId.CommandError, RelationalEventId.ConnectionError, RelationalEventId.TransactionError));
    }
}
