using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TEC.ORM.EntityFrameworkCore.Internal;

namespace TEC.ORM.EntityFrameworkCore.Idempotency;

internal sealed class IdempotencyCleanupService<TContext>(
    IServiceScopeFactory scopes,
    OrmIdempotencyOptions options,
    TimeProvider timeProvider,
    ILogger<IdempotencyCleanupService<TContext>> logger)
    : BackgroundService where TContext : DbContext
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
            catch (Exception ex)
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
