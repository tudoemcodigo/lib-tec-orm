using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Oracle.EntityFrameworkCore.Infrastructure;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.DependencyInjection;
using TEC.ORM.Oracle.Dialect;

namespace TEC.ORM.Oracle.DependencyInjection;

/// <summary>
/// Métodos de extensão para registro do TEC.ORM com Oracle no container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o TEC.ORM com Oracle Database (EF Core com <c>Oracle.EntityFrameworkCore</c> e Dapper sobre o ODP.NET) para o
    /// contexto <typeparamref name="TContext"/>, com o mesmo pipeline do SQL Server
    /// (<see cref="OrmServiceCollectionExtensions.AddTecOrmDbContext{TContext}"/>): conexão do TEC.Vault com a política do
    /// <see cref="OracleDialect"/> (TCPS), exclusão lógica, autoria, trilha histórica, convenções, <c>CommandTimeout</c> no
    /// comando, logs sem dados sensíveis e leituras complexas em transação <c>READ ONLY</c>.
    /// </summary>
    /// <param name="services">Container.</param>
    /// <param name="configure">Opções do TEC.ORM.</param>
    /// <param name="oracle">
    /// Ajustes do provedor Oracle do EF Core (ex.: <c>UseOracleSQLCompatibility</c>). Não use <c>EnableRetryOnFailure</c>: é
    /// incompatível com as transações do <c>IUnitOfWork</c> e da trilha histórica e repetiria escritas.
    /// </param>
    /// <returns>O próprio <paramref name="services"/>.</returns>
    /// <exception cref="InvalidOperationException">O mesmo <typeparamref name="TContext"/> já foi registrado.</exception>
    public static IServiceCollection AddTecOrmOracle<TContext>(
        this IServiceCollection services,
        Action<OrmOptions> configure,
        Action<OracleDbContextOptionsBuilder>? oracle = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddTecOrmDbContext<TContext>(new OracleDialect(), configure, (builder, options) =>
            builder.UseOracle(provider =>
            {
                // Tempo limite real do comando (o Connection Timeout do segredo é o da abertura)
                provider.CommandTimeout(options.CommandTimeoutSeconds);
                oracle?.Invoke(provider);
            }));
    }
}
