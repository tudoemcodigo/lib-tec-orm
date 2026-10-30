using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using TEC.Cqrs.Idempotency;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.SoftDelete;

namespace TEC.ORM.EntityFrameworkCore.Idempotency;

/// <summary>
/// <see cref="IIdempotencyStore"/> no banco da aplicação, na tabela mapeada por
/// <see cref="IdempotencyModelBuilderExtensions.AddTecIdempotency"/>: compartilhado entre as instâncias da aplicação.
/// </summary>
/// <remarks>
/// <para>A reserva é atômica no banco, com o SQL do dialeto (<see cref="ISqlDialect.FormatAtomicIdempotencyInsert(string, IdempotencyInsertColumns)"/>):
/// <c>INSERT ... WHERE NOT EXISTS</c> com <c>UPDLOCK, HOLDLOCK</c> no SQL Server, <c>ON CONFLICT DO NOTHING</c> no PostgreSQL,
/// <c>INSERT</c> simples no MySQL e <c>MERGE</c> no Oracle, sempre com os nomes de coluna do modelo. Só a violação de chave
/// única ou primária conta como "já existe"; qualquer outro erro (coluna inexistente, permissão, sintaxe) sobe como exceção.
/// Só uma requisição recebe <see cref="IdempotencyBeginStatus.Started"/>. Reserva expirada é assumida com <c>UPDATE</c>
/// condicional ao <c>LockId</c> anterior.</para>
/// <para>Usa só comandos diretos (<c>ExecuteSql</c>, <c>ExecuteUpdate</c>, <c>ExecuteDelete</c>) e leituras sem rastreamento:
/// nunca chama <c>SaveChanges</c>, então não grava alterações pendentes da aplicação no mesmo contexto. Participa da transação
/// corrente do contexto, se houver (o middleware HTTP roda fora de transação).</para>
/// <para>Registro: <c>services.AddTecOrmIdempotency&lt;TContext&gt;()</c> (Scoped). O dialeto vem do registro do contexto
/// (<c>AddTecOrm</c>, <c>AddTecOrmPostgreSql</c>, <c>AddTecOrmMySql</c>, <c>AddTecOrmOracle</c>).</para>
/// </remarks>
/// <typeparam name="TContext">Contexto com a tabela mapeada.</typeparam>
public sealed class EfIdempotencyStore<TContext> : IIdempotencyStore where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ISqlDialect? _dialect;

    /// <summary>Cria o store com o dialeto do registro do contexto.</summary>
    /// <param name="context">Contexto com a tabela mapeada.</param>
    /// <param name="timeProvider">Relógio.</param>
    public EfIdempotencyStore(TContext context, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _context = context;
        _timeProvider = timeProvider;
    }

    /// <summary>Cria o store com um dialeto explícito (contexto montado sem o registro do TEC.ORM).</summary>
    /// <param name="context">Contexto com a tabela mapeada.</param>
    /// <param name="timeProvider">Relógio.</param>
    /// <param name="dialect">Dialeto do banco do contexto.</param>
    public EfIdempotencyStore(TContext context, TimeProvider timeProvider, ISqlDialect dialect)
        : this(context, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        _dialect = dialect;
    }

    private DbSet<IdempotencyRecord> Records => _context.Set<IdempotencyRecord>();

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">A tabela não está mapeada no modelo do contexto, ou o dialeto é desconhecido.</exception>
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

        // Removida entre o INSERT e a leitura (abandono ou limpeza): uma nova tentativa de reserva
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

        // Expirada: assume a chave, se ninguém a assumiu antes (condicional ao LockId lido)
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

    /// <summary>
    /// Remove até <paramref name="batchSize"/> chaves expiradas (respostas fora da retenção e reservas vencidas).
    /// </summary>
    /// <param name="batchSize">Máximo de linhas por chamada.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>Linhas removidas.</returns>
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

    /// <summary>Reserva atômica: insere só se a chave não existir.</summary>
    private async Task<bool> TryInsertAsync(IdempotencyRequest request, Guid lockId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var entity = EnsureMapped();
        var dialect = Dialect();
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        string target = entity.GetSchema() is { Length: > 0 } schema
            ? dialect.DelimitIdentifier(schema) + "." + dialect.DelimitIdentifier(table.Name)
            : dialect.DelimitIdentifier(table.Name);
        string Column(string property) => dialect.DelimitIdentifier(entity.FindProperty(property)!.GetColumnName(table)!);

        string sql = dialect.FormatAtomicIdempotencyInsert(target, new IdempotencyInsertColumns(
            Column(nameof(IdempotencyRecord.Scope)), Column(nameof(IdempotencyRecord.Key)), Column(nameof(IdempotencyRecord.RequestHash)),
            Column(nameof(IdempotencyRecord.LockId)), Column(nameof(IdempotencyRecord.State)), Column(nameof(IdempotencyRecord.LockedUntil)),
            Column(nameof(IdempotencyRecord.RetentionSeconds)), Column(nameof(IdempotencyRecord.ExpiresAt)),
            Column(nameof(IdempotencyRecord.CreatedAt))));

        var lockedUntil = now + request.LockTimeout;
        try
        {
#pragma warning disable EF1002 // identificadores vêm do modelo (delimitados pelo dialeto); valores sempre parametrizados
            int inserted = await _context.Database.ExecuteSqlRawAsync(sql,
                [request.Key.Scope, request.Key.Key, request.RequestHash, lockId, IdempotencyRecord.InProgress, lockedUntil,
                    RetentionSeconds(request.Retention), lockedUntil, now], cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
            return inserted == 1;
        }
        catch (Exception exception) when (dialect.ExceptionTranslator.IsUniqueViolation(exception))
        {
            // Outra requisição reservou a mesma chave ao mesmo tempo (o MERGE e o INSERT simples podem colidir): "já existe"
            return false;
        }
    }

    /// <summary>Dialeto explícito ou o do registro do contexto; sem nenhum, falha fechada (o SQL de outro banco não serve).</summary>
    private ISqlDialect Dialect() =>
        _dialect ?? _context.GetService<IDbContextOptions>().FindExtension<TecOrmOptionsExtension>()?.Dialect
        ?? throw new InvalidOperationException(
            $"O dialeto do banco de {typeof(TContext).Name} é desconhecido: registre o contexto com AddTecOrm (ou AddTecOrmPostgreSql, " +
            "AddTecOrmMySql, AddTecOrmOracle) ou crie o store com o dialeto.");

    private IEntityType EnsureMapped() => _context.Model.FindEntityType(typeof(IdempotencyRecord))
        ?? throw new InvalidOperationException(
            $"A tabela de idempotência não está mapeada em {typeof(TContext).Name}. Chame modelBuilder.AddTecIdempotency() no " +
            "OnModelCreating e gere a migration.");

    private static int RetentionSeconds(TimeSpan retention) => (int)Math.Min(int.MaxValue, Math.Ceiling(retention.TotalSeconds));

    private static IdempotentResponse ToResponse(IdempotencyRecord record)
    {
        var headers = string.IsNullOrEmpty(record.HeadersJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize(record.HeadersJson, IdempotencyJsonContext.Default.DictionaryStringString) ?? [];
        return new IdempotentResponse(record.StatusCode ?? 200, record.ContentType,
            new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase), record.Body ?? []);
    }
}
