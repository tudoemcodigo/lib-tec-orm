using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TEC.ORM.SqlServer.Internal;

namespace TEC.ORM.SqlServer.Idempotency;

/// <summary>Remove periodicamente as chaves de idempotência expiradas (lotes curtos, um escopo por ciclo).</summary>
internal sealed class IdempotencyCleanupService<TContext>(IServiceScopeFactory scopes, OrmIdempotencyOptions options, TimeProvider timeProvider,
    ILogger<IdempotencyCleanupService<TContext>> logger) : BackgroundService where TContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.CleanupInterval, timeProvider, stoppingToken).ConfigureAwait(false);
                await CleanupAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // falha de limpeza não pode derrubar o host: registra e tenta no próximo ciclo
            catch (Exception ex)
#pragma warning restore CA1031
            {
                OrmLog.IdempotencyPurgeFailed(logger, ex, typeof(TContext).Name);
            }
        }
    }

    internal async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var store = scope.ServiceProvider.GetRequiredService<EfIdempotencyStore<TContext>>();
            int total = 0;
            for (int batch = 0; batch < options.MaxBatchesPerCleanup; batch++)
            {
                int removed = await store.PurgeExpiredAsync(options.CleanupBatchSize, cancellationToken).ConfigureAwait(false);
                total += removed;
                if (removed < options.CleanupBatchSize)
                    break;
            }

            if (total > 0)
                OrmLog.IdempotencyPurged(logger, total, typeof(TContext).Name);
        }
    }
}
