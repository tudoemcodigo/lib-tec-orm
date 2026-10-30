using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Security;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Persistence;
using TEC.ORM.Abstractions;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.DependencyInjection;
using TEC.ORM.EntityFrameworkCore.Diagnostics;
using TEC.ORM.EntityFrameworkCore.Security;
using TEC.ORM.EntityFrameworkCore.SoftDelete;
using CoreRegistration = TEC.ORM.EntityFrameworkCore.DependencyInjection.OrmServiceCollectionExtensions;

namespace TEC.ORM.SqlServer.DependencyInjection;

/// <summary>Registro do TEC.ORM (SQL Server) no container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o TEC.ORM com suporte a SQL Server para o <typeparamref name="TContext"/>.
    /// Alias para <see cref="AddTecOrm{TContext}"/>.
    /// </summary>
    public static IServiceCollection AddTecOrmSqlServer<TContext>(this IServiceCollection services, Action<OrmOptions> configure,
        Action<SqlServerDbContextOptionsBuilder>? sqlServer = null) where TContext : DbContext
        => services.AddTecOrm<TContext>(configure, sqlServer);

    /// <summary>
    /// Registra o TEC.ORM com o <typeparamref name="TContext"/>:
    /// <list type="bullet">
    /// <item><description><c>TContext</c> (scoped) com <c>UseSqlServer()</c> <b>sem</b> string de conexão: ela vem do TEC.Vault
    /// na abertura (<see cref="IOrmConnectionSecurity"/>), com exclusão lógica ao salvar, autoria, trilha histórica,
    /// <c>CommandTimeout</c> e <c>EnableSensitiveDataLogging</c> desligado;</description></item>
    /// <item><description><see cref="IOrmRepository{TEntity, TKey}"/> para qualquer entidade do contexto (genérico aberto);</description></item>
    /// <item><description><see cref="IOrmQueryExecutor"/> (Dapper, conexão de leitura);</description></item>
    /// <item><description><see cref="IUnitOfWork"/> (TEC.Cqrs), <see cref="IOrmOperationRunner"/> e <see cref="IOrmConnectionSecurity"/>.</description></item>
    /// <item><description>Um <see cref="IExceptionErrorMapper"/> (TEC.Cqrs) que converte concorrência otimista e deadlock em <c>ORM_CONCORRENCIA</c> e violação de chave em <c>ORM_CONFLITO</c> (409), inclusive no commit do <see cref="IUnitOfWork"/>.</description></item>
    /// </list>
    /// Requer o TEC.Vault registrado (<c>AddTecVault</c>), que fornece o <c>ISecretReader</c>. Os serviços usam <c>TryAdd</c>:
    /// registre antes a sua implementação para substituir. O pipeline é o comum do <c>TEC.ORM.EntityFrameworkCore</c>
    /// (<c>AddTecOrmDbContext</c>), com o <see cref="SqlServerDialect"/>.
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
    /// incompatível com as transações do <see cref="IUnitOfWork"/> e da trilha histórica e repetiria escritas; as leituras já
    /// têm novas tentativas seguras (<see cref="OrmOptions.TransientRetryCount"/>).
    /// </param>
    /// <returns>O próprio <paramref name="services"/>.</returns>
    public static IServiceCollection AddTecOrm<TContext>(this IServiceCollection services, Action<OrmOptions> configure,
        Action<SqlServerDbContextOptionsBuilder>? sqlServer = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddTecOrmDbContext<TContext>(new SqlServerDialect(), configure, UseSqlServer(sqlServer));
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
        return CoreRegistration.UseTecOrmPipeline(builder, provider, UseSqlServer(sqlServer));
    }

    /// <summary><c>UseSqlServer()</c> sem string de conexão, com o tempo limite das opções e os ajustes da aplicação.</summary>
    private static Action<DbContextOptionsBuilder, OrmOptions> UseSqlServer(Action<SqlServerDbContextOptionsBuilder>? sqlServer) =>
        (builder, options) => builder.UseSqlServer(sql =>
        {
            sql.CommandTimeout(options.CommandTimeoutSeconds);
            sqlServer?.Invoke(sql);
        });
}
