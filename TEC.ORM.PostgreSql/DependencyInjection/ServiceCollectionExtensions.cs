using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.DependencyInjection;
using TEC.ORM.EntityFrameworkCore.Security;
using TEC.ORM.PostgreSql.Dialect;

namespace TEC.ORM.PostgreSql.DependencyInjection;

/// <summary>
/// Métodos de extensão para registro do TEC.ORM com PostgreSQL no container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o TEC.ORM com suporte completo a PostgreSQL (EF Core e Dapper) para o contexto <typeparamref name="TContext"/>.
    /// </summary>
    public static IServiceCollection AddTecOrmPostgreSql<TContext>(
        this IServiceCollection services,
        Action<OrmOptions> configure,
        Action<NpgsqlDbContextOptionsBuilder>? postgreSql = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new OrmOptions();
        configure(options);
        options.Dialect = new PostgreSqlDialect();
        options.Validate();

        services.AddTecOrmCoreServices<TContext>(options);

        services.AddDbContext<TContext>((serviceProvider, optionsBuilder) =>
        {
            var interceptor = serviceProvider.GetRequiredService<SecretConnectionInterceptor>();
            optionsBuilder.AddInterceptors(interceptor);

            if (options.EnableHistoricalAudit)
            {
                var auditInterceptor = serviceProvider.GetRequiredService<TEC.ORM.EntityFrameworkCore.Auditing.HistoricalAuditInterceptor>();
                optionsBuilder.AddInterceptors(auditInterceptor);
            }

            var baseInterceptor = serviceProvider.GetRequiredService<TEC.ORM.EntityFrameworkCore.Auditing.AuditInterceptor>();
            var softDeleteInterceptor = serviceProvider.GetRequiredService<TEC.ORM.EntityFrameworkCore.SoftDelete.SoftDeleteInterceptor>();
            optionsBuilder.AddInterceptors(baseInterceptor, softDeleteInterceptor);

            optionsBuilder.UseNpgsql(postgreSql);
        });

        return services;
    }
}
