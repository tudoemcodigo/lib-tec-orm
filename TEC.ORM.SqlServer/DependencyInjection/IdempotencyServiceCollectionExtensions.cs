using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TEC.Cqrs.Idempotency;
using TEC.ORM.SqlServer.Idempotency;

namespace TEC.ORM.SqlServer.DependencyInjection;

/// <summary>Registro do store de idempotência no banco.</summary>
public static class IdempotencyServiceCollectionExtensions
{
    /// <summary>
    /// Registra o <see cref="EfIdempotencyStore{TContext}"/> como <see cref="IIdempotencyStore"/> (Scoped), usado pela
    /// idempotência HTTP do <c>TEC.Cqrs.AspNetCore</c> (<c>AddIdempotency</c>/<c>UseTecIdempotency</c>), e a limpeza periódica
    /// das chaves expiradas. O contexto precisa mapear a tabela com
    /// <see cref="IdempotencyModelBuilderExtensions.AddTecIdempotency"/> (e a migration aplicada).
    /// </summary>
    /// <typeparam name="TContext">Contexto registrado com <c>AddTecOrm&lt;TContext&gt;</c>.</typeparam>
    /// <param name="services">Container.</param>
    /// <param name="configure">Opções da limpeza.</param>
    /// <returns>O próprio <paramref name="services"/>.</returns>
    /// <exception cref="InvalidOperationException">Chamado mais de uma vez, ou outro <see cref="IIdempotencyStore"/> já registrado.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Opções fora dos limites.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddTecOrm&lt;AppDbContext&gt;(o => o.ConnectionSecretName = "sql-app");
    /// builder.Services.AddTecOrmIdempotency&lt;AppDbContext&gt;(o => o.CleanupInterval = TimeSpan.FromMinutes(30));
    /// </code>
    /// </example>
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
        services.TryAddScoped<EfIdempotencyStore<TContext>>();
        services.AddScoped<IIdempotencyStore>(sp => sp.GetRequiredService<EfIdempotencyStore<TContext>>());
        if (options.EnableCleanup)
        {
            services.AddSingleton(options);
            services.AddHostedService<IdempotencyCleanupService<TContext>>();
        }

        return services;
    }
}
