using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.DependencyInjection;
using TEC.ORM.PostgreSql.Dialect;

namespace TEC.ORM.PostgreSql.DependencyInjection;

/// <summary>
/// Métodos de extensão para registro do TEC.ORM com PostgreSQL no container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o TEC.ORM com PostgreSQL (EF Core com Npgsql e Dapper) para o contexto <typeparamref name="TContext"/>, com o
    /// mesmo pipeline do SQL Server (<see cref="OrmServiceCollectionExtensions.AddTecOrmDbContext{TContext}"/>): conexão do
    /// TEC.Vault com a política do <see cref="PostgreSqlDialect"/>, exclusão lógica, autoria, trilha histórica, convenções,
    /// <c>CommandTimeout</c>, logs sem dados sensíveis e leituras complexas em transação <c>READ ONLY</c>.
    /// </summary>
    /// <param name="services">Container.</param>
    /// <param name="configure">Opções do TEC.ORM.</param>
    /// <param name="postgreSql">
    /// Ajustes do provedor Npgsql do EF Core (ex.: <c>MigrationsHistoryTable</c>). Não use <c>EnableRetryOnFailure</c>: é
    /// incompatível com as transações do <c>IUnitOfWork</c> e da trilha histórica e repetiria escritas.
    /// </param>
    /// <returns>O próprio <paramref name="services"/>.</returns>
    /// <exception cref="InvalidOperationException">O mesmo <typeparamref name="TContext"/> já foi registrado.</exception>
    public static IServiceCollection AddTecOrmPostgreSql<TContext>(
        this IServiceCollection services,
        Action<OrmOptions> configure,
        Action<NpgsqlDbContextOptionsBuilder>? postgreSql = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddTecOrmDbContext<TContext>(new PostgreSqlDialect(), configure, (builder, options) =>
            builder.UseNpgsql(npgsql =>
            {
                npgsql.CommandTimeout(options.CommandTimeoutSeconds);
                postgreSql?.Invoke(npgsql);
            }));
    }
}
