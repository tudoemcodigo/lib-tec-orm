using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TEC.Cqrs.Persistence;
using TEC.ORM.Abstractions;
using TEC.ORM.Auditing;
using TEC.ORM.EntityFrameworkCore.Auditing;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Diagnostics;
using TEC.ORM.EntityFrameworkCore.Idempotency;
using TEC.ORM.EntityFrameworkCore.Internal;
using TEC.ORM.EntityFrameworkCore.Security;
using TEC.ORM.EntityFrameworkCore.SoftDelete;
using TEC.ORM.EntityFrameworkCore.UnitOfWork;

namespace TEC.ORM.EntityFrameworkCore.DependencyInjection;

/// <summary>
/// Extensões base para registro de serviços comuns do TEC.ORM no container.
/// </summary>
public static class OrmServiceCollectionExtensions
{
    /// <summary>
    /// Registra os serviços comuns do TEC.ORM no container (repositório genérico, executor Dapper, Unit of Work, interceptors).
    /// </summary>
    public static IServiceCollection AddTecOrmCoreServices<TContext>(this IServiceCollection services, OrmOptions options)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOrmAuditContextProvider, DefaultOrmAuditContextProvider>();
        services.TryAddSingleton<IOrmConnectionSecurity, OrmConnectionSecurity>();
        services.TryAddSingleton<IOrmOperationRunner, OrmOperationRunner>();

        services.TryAddSingleton(provider =>
        {
            var sec = provider.GetRequiredService<IOrmConnectionSecurity>();
            var opts = provider.GetRequiredService<OrmOptions>();
            var logger = provider.GetRequiredService<ILogger<OrmConnectionCircuit>>();
            var circuit = OrmConnectionCircuit.Create(opts, OrmConnectionKind.ReadWrite, provider.GetService<TimeProvider>(), logger, opts.Dialect);
            return new SecretConnectionInterceptor(sec, circuit);
        });

        services.TryAddSingleton(provider => new SoftDeleteInterceptor(
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<OrmOptions>().MaxSoftDeletePropagation));

        services.TryAddSingleton<AuditInterceptor>();
        services.TryAddSingleton<HistoricalAuditInterceptor>();

        // Registros de serviços abertos e comuns
        services.TryAddScoped(typeof(IOrmRepository<,>), typeof(OrmRepository<,>));
        services.TryAddScoped<IOrmQueryExecutor, OrmQueryExecutor>();
        services.TryAddScoped<IUnitOfWork, OrmUnitOfWork>();

        return services;
    }

    /// <summary>
    /// Configura o store de idempotência compartilhado no banco de dados.
    /// </summary>
    public static IServiceCollection AddTecOrmIdempotency<TContext>(this IServiceCollection services, Action<OrmIdempotencyOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new OrmIdempotencyOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddScoped<EfIdempotencyStore<TContext>>();
        services.TryAddScoped<TEC.Cqrs.Idempotency.IIdempotencyStore>(sp => sp.GetRequiredService<EfIdempotencyStore<TContext>>());

        if (options.EnableCleanup)
            services.AddHostedService<IdempotencyCleanupService<TContext>>();

        return services;
    }
}
