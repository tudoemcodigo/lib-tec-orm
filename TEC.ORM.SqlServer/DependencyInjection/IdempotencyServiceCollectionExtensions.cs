using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TEC.Cqrs.Idempotency;
using TEC.ORM.EntityFrameworkCore.Idempotency;
using CoreRegistration = TEC.ORM.EntityFrameworkCore.DependencyInjection.OrmServiceCollectionExtensions;

namespace TEC.ORM.SqlServer.DependencyInjection;

/// <summary>Registro do store de idempotência no banco (mesmo namespace do <c>AddTecOrm</c>).</summary>
public static class IdempotencyServiceCollectionExtensions
{
    /// <summary>
    /// Registra o <see cref="EfIdempotencyStore{TContext}"/> como <see cref="IIdempotencyStore"/> (Scoped) e a limpeza periódica
    /// das chaves expiradas. Encaminha para o registro comum
    /// (<see cref="CoreRegistration.AddTecOrmIdempotency{TContext}(IServiceCollection, Action{OrmIdempotencyOptions}?)"/>).
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
        where TContext : DbContext =>
        CoreRegistration.AddTecOrmIdempotency<TContext>(services, configure);
}
