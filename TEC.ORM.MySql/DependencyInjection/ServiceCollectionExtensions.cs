using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySql.EntityFrameworkCore.Infrastructure;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.DependencyInjection;
using TEC.ORM.MySql.Dialect;

namespace TEC.ORM.MySql.DependencyInjection;

/// <summary>
/// Métodos de extensão para registro do TEC.ORM com MySQL no container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o TEC.ORM com MySQL / MariaDB (EF Core com <c>MySql.EntityFrameworkCore</c> e Dapper, ambos sobre o MySql.Data)
    /// para o contexto <typeparamref name="TContext"/>, com o mesmo pipeline do SQL Server
    /// (<see cref="OrmServiceCollectionExtensions.AddTecOrmDbContext{TContext}"/>): conexão do TEC.Vault com a política do
    /// <see cref="MySqlDialect"/>, exclusão lógica, autoria, trilha histórica, convenções, <c>CommandTimeout</c>, logs sem dados
    /// sensíveis e leituras complexas em transação <c>READ ONLY</c>.
    /// </summary>
    /// <remarks>
    /// O <c>MySql.EntityFrameworkCore</c> não tem opção de versão do servidor (<c>ServerVersion</c>): não há versão a configurar,
    /// e o registro não abre conexão nenhuma.
    /// </remarks>
    /// <param name="services">Container.</param>
    /// <param name="configure">Opções do TEC.ORM.</param>
    /// <param name="mySql">
    /// Ajustes do provedor do EF Core. Não use <c>EnableRetryOnFailure</c>: é incompatível com as transações do
    /// <c>IUnitOfWork</c> e da trilha histórica e repetiria escritas.
    /// </param>
    /// <returns>O próprio <paramref name="services"/>.</returns>
    /// <exception cref="InvalidOperationException">O mesmo <typeparamref name="TContext"/> já foi registrado.</exception>
    public static IServiceCollection AddTecOrmMySql<TContext>(
        this IServiceCollection services,
        Action<OrmOptions> configure,
        Action<MySQLDbContextOptionsBuilder>? mySql = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddTecOrmDbContext<TContext>(new MySqlDialect(), configure, (builder, options) =>
            builder.UseMySQL(provider =>
            {
                provider.CommandTimeout(options.CommandTimeoutSeconds);
                mySql?.Invoke(provider);
            }));
    }
}
