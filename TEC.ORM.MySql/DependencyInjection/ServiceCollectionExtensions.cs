using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.DependencyInjection;
using TEC.ORM.EntityFrameworkCore.Security;
using TEC.ORM.MySql.Dialect;

namespace TEC.ORM.MySql.DependencyInjection;

/// <summary>
/// Métodos de extensão para registro do TEC.ORM com MySQL no container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o TEC.ORM com suporte completo a MySQL (EF Core e Dapper) para o contexto <typeparamref name="TContext"/>.
    /// </summary>
    public static IServiceCollection AddTecOrmMySql<TContext>(
        this IServiceCollection services,
        Action<OrmOptions> configure) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new OrmOptions();
        configure(options);
        options.Dialect = new MySqlDialect();
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

#if NET8_0
            optionsBuilder.UseMySql(ServerVersion.AutoDetect("Server=localhost;Database=dummy;"));
#else
            optionsBuilder.UseMySQL();
#endif
        });

        return services;
    }
}
