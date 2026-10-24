using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Oracle.EntityFrameworkCore.Infrastructure;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.DependencyInjection;
using TEC.ORM.EntityFrameworkCore.Security;
using TEC.ORM.Oracle.Dialect;

namespace TEC.ORM.Oracle.DependencyInjection;

/// <summary>
/// Métodos de extensão para registro do TEC.ORM com Oracle no container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o TEC.ORM com suporte completo a Oracle (EF Core e Dapper) para o contexto <typeparamref name="TContext"/>.
    /// </summary>
    public static IServiceCollection AddTecOrmOracle<TContext>(
        this IServiceCollection services,
        Action<OrmOptions> configure,
        Action<OracleDbContextOptionsBuilder>? oracle = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new OrmOptions();
        configure(options);
        options.Dialect = new OracleDialect();
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

            optionsBuilder.UseOracle(oracle);
        });

        return services;
    }
}
