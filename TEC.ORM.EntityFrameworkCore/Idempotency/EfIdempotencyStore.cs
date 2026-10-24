using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using TEC.Cqrs.Idempotency;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.EntityFrameworkCore.Idempotency;

/// <summary>
/// <see cref="IIdempotencyStore"/> baseado em EF Core, agnóstico de dialeto de banco de dados.
/// </summary>
public sealed class EfIdempotencyStore<TContext> : IIdempotencyStore where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;

    /// <summary>Cria uma nova instância do store de idempotência.</summary>
    public EfIdempotencyStore(TContext context, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _context = context;
        _timeProvider = timeProvider;
    }

    private DbSet<IdempotencyRecord> Records => _context.Set<IdempotencyRecord>();

    /// <inheritdoc />
    public async ValueTask<IdempotencyBeginResult> TryBeginAsync(IdempotencyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var now = _timeProvider.GetUtcNow();
        var lockId = Guid.NewGuid();
        if (await TryInsertAsync(request, lockId, now, cancellationToken).ConfigureAwait(false))
            return IdempotencyBeginResult.Started(lockId);

        string scope = request.Key.Scope, key = request.Key.Key;
        var existing = await Records.AsNoTracking()
            .Where(r => r.Scope == scope && r.Key == key)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            return await TryInsertAsync(request, lockId, now, cancellationToken).ConfigureAwait(false)
                ? IdempotencyBeginResult.Started(lockId)
                : IdempotencyBeginResult.InProgress;
        }

        bool alive = existing.State == IdempotencyRecord.Completed ? existing.ExpiresAt > now : existing.LockedUntil > now;
        if (alive)
        {
            if (!string.Equals(existing.RequestHash, request.RequestHash, StringComparison.Ordinal))
                return IdempotencyBeginResult.Mismatch;

            return existing.State == IdempotencyRecord.Completed
                ? IdempotencyBeginResult.Completed(ToResponse(existing))
                : IdempotencyBeginResult.InProgress;
        }

        var previousLock = existing.LockId;
        var lockedUntil = now + request.LockTimeout;
        int retention = RetentionSeconds(request.Retention);
        int updated = await Records
            .Where(r => r.Scope == scope && r.Key == key && r.LockId == previousLock)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.LockId, lockId)
                .SetProperty(r => r.State, IdempotencyRecord.InProgress)
                .SetProperty(r => r.RequestHash, request.RequestHash)
                .SetProperty(r => r.LockedUntil, lockedUntil)
                .SetProperty(r => r.RetentionSeconds, retention)
                .SetProperty(r => r.ExpiresAt, lockedUntil)
                .SetProperty(r => r.StatusCode, (int?)null)
                .SetProperty(r => r.ContentType, (string?)null)
                .SetProperty(r => r.HeadersJson, (string?)null)
                .SetProperty(r => r.Body, (byte[]?)null)
                .SetProperty(r => r.CreatedAt, now), cancellationToken)
            .ConfigureAwait(false);

        return updated == 1 ? IdempotencyBeginResult.Started(lockId) : IdempotencyBeginResult.InProgress;
    }

    /// <inheritdoc />
    public async ValueTask<bool> CompleteAsync(IdempotencyKey key, Guid lockId, IdempotentResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        EnsureMapped();

        string scope = key.Scope, value = key.Key;
        var retention = await Records.AsNoTracking()
            .Where(r => r.Scope == scope && r.Key == value && r.LockId == lockId && r.State == IdempotencyRecord.InProgress)
            .Select(r => (int?)r.RetentionSeconds)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (retention is null)
            return false;

        var expiresAt = _timeProvider.GetUtcNow().AddSeconds(retention.Value);
        string headers = JsonSerializer.Serialize(new Dictionary<string, string>(response.Headers), IdempotencyJsonContext.Default.DictionaryStringString);
        var body = response.Body.ToArray();

        int updated = await Records
            .Where(r => r.Scope == scope && r.Key == value && r.LockId == lockId && r.State == IdempotencyRecord.InProgress)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.State, IdempotencyRecord.Completed)
                .SetProperty(r => r.StatusCode, response.StatusCode)
                .SetProperty(r => r.ContentType, response.ContentType)
                .SetProperty(r => r.HeadersJson, headers)
                .SetProperty(r => r.Body, body)
                .SetProperty(r => r.ExpiresAt, expiresAt), cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    /// <inheritdoc />
    public async ValueTask<bool> AbandonAsync(IdempotencyKey key, Guid lockId, CancellationToken cancellationToken)
    {
        EnsureMapped();
        string scope = key.Scope, value = key.Key;
        int deleted = await Records
            .Where(r => r.Scope == scope && r.Key == value && r.LockId == lockId && r.State == IdempotencyRecord.InProgress)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return deleted == 1;
    }

    /// <summary>Remove chaves de idempotência expiradas em lotes.</summary>
    public Task<int> PurgeExpiredAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        EnsureMapped();

        var now = _timeProvider.GetUtcNow();
        return Records
            .Where(r => (r.State == IdempotencyRecord.Completed && r.ExpiresAt <= now) || (r.State == IdempotencyRecord.InProgress && r.LockedUntil <= now))
            .Take(batchSize)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<bool> TryInsertAsync(IdempotencyRequest request, Guid lockId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var entity = EnsureMapped();
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        string tableName = entity.GetTableName()!;
        var dialect = _context.GetService<OrmOptions>()?.Dialect;

        string target = dialect != null
            ? (entity.GetSchema() != null ? $"{dialect.DelimitIdentifier(entity.GetSchema()!)}.{dialect.DelimitIdentifier(tableName)}" : dialect.DelimitIdentifier(tableName))
            : Quote(entity.GetSchema()) is { } schema ? $"{schema}.{Quote(tableName)}" : Quote(tableName)!;

        string Column(string property) => dialect != null
            ? dialect.DelimitIdentifier(entity.FindProperty(property)!.GetColumnName(table)!)
            : Quote(entity.FindProperty(property)!.GetColumnName(table))!;

        var lockedUntil = now + request.LockTimeout;
        string sql = dialect != null
            ? dialect.FormatAtomicIdempotencyInsert(target)
            : $"INSERT INTO {target} ({Column(nameof(IdempotencyRecord.Scope))}, {Column(nameof(IdempotencyRecord.Key))}, " +
              $"{Column(nameof(IdempotencyRecord.RequestHash))}, {Column(nameof(IdempotencyRecord.LockId))}, {Column(nameof(IdempotencyRecord.State))}, " +
              $"{Column(nameof(IdempotencyRecord.LockedUntil))}, {Column(nameof(IdempotencyRecord.RetentionSeconds))}, " +
              $"{Column(nameof(IdempotencyRecord.ExpiresAt))}, {Column(nameof(IdempotencyRecord.CreatedAt))}) " +
              "SELECT {0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8} " +
              $"WHERE NOT EXISTS (SELECT 1 FROM {target} " +
              $"WHERE {Column(nameof(IdempotencyRecord.Scope))} = {{0}} AND {Column(nameof(IdempotencyRecord.Key))} = {{1}})";

        try
        {
            int inserted = await _context.Database.ExecuteSqlRawAsync(sql,
                [request.Key.Scope, request.Key.Key, request.RequestHash, lockId, IdempotencyRecord.InProgress, lockedUntil,
                    RetentionSeconds(request.Retention), lockedUntil, now], cancellationToken).ConfigureAwait(false);
            return inserted == 1;
        }
        catch (DbUpdateException)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (dialect?.ExceptionTranslator.Translate(ex).Error.Code == OrmErrors.ConflictCode)
                return false;

            if (ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("violation", StringComparison.OrdinalIgnoreCase))
                return false;

            return false;
        }
    }

    private IEntityType EnsureMapped() => _context.Model.FindEntityType(typeof(IdempotencyRecord))
        ?? throw new InvalidOperationException(
            $"A tabela de idempotência não está mapeada em {typeof(TContext).Name}. Chame modelBuilder.AddTecIdempotency() no OnModelCreating e gere a migration.");

    private static int RetentionSeconds(TimeSpan retention) => (int)Math.Min(int.MaxValue, Math.Ceiling(retention.TotalSeconds));

    private static string? Quote(string? identifier) => identifier is null ? null : "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static IdempotentResponse ToResponse(IdempotencyRecord record)
    {
        var headers = string.IsNullOrEmpty(record.HeadersJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize(record.HeadersJson, IdempotencyJsonContext.Default.DictionaryStringString) ?? [];
        return new IdempotentResponse(record.StatusCode ?? 200, record.ContentType,
            new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase), record.Body ?? []);
    }
}
