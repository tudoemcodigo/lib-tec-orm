using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using TEC.ORM.Auditing;
using TEC.ORM.Entities;
using TEC.ORM.EntityFrameworkCore.Configuration;

namespace TEC.ORM.EntityFrameworkCore.Auditing;

/// <summary>
/// Interceptor transacional do EF Core que captura as alterações de entidades anotadas com <c>[Auditable]</c> e grava os
/// registros da trilha histórica (<see cref="OrmAuditRecord"/>) na mesma transação.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>Ator</b>: do <see cref="IOrmAuditContextProvider"/>. O padrão registrado pelo TEC.ORM
/// (<see cref="DefaultOrmAuditContextProvider"/>) lê, durante o salvamento, o mesmo <c>ICurrentUser</c> do contexto que
/// preenche <c>CreatedBy</c>/<c>UpdatedBy</c>/<c>DeletedBy</c>.</description></item>
/// <item><description><b>Chave gerada pelo banco</b> (identidade, sequência): o registro da criação é gravado logo depois do
/// <c>INSERT</c>, já com o <c>Id</c> real, na mesma transação (aberta aqui se o salvamento não estiver numa).</description></item>
/// <item><description><b>Tamanho</b>: cada valor de <c>OldValues</c>/<c>NewValues</c> é cortado em <c>MaxAuditValueLength</c>
/// caracteres; <c>byte[]</c> vira só o tamanho; os campos de texto do registro são ajustados ao tamanho das colunas.</description></item>
/// <item><description><c>[SensitiveData]</c> vira <c>***</c>; <c>[AuditIgnore]</c> fica fora.</description></item>
/// </list>
/// Registrado depois dos interceptores de exclusão lógica e de autoria: a exclusão lógica aparece como <c>SoftDeleted</c> e os
/// valores gravados são os já carimbados (o que o chamador mandou em <c>CreatedBy</c>, por exemplo, não entra na trilha).
/// </remarks>
public sealed class HistoricalAuditInterceptor : SaveChangesInterceptor
{
    /// <summary>Ação da trilha: criação.</summary>
    internal const string CreatedAction = "Created";

    /// <summary>Ação da trilha: alteração.</summary>
    internal const string UpdatedAction = "Updated";

    /// <summary>Ação da trilha: exclusão física (pelo rastreador ou por <c>HardDeleteAsync</c>).</summary>
    internal const string DeletedAction = "Deleted";

    /// <summary>Ação da trilha: exclusão lógica.</summary>
    internal const string SoftDeletedAction = "SoftDeleted";

    private static readonly ConcurrentDictionary<Type, AuditedType> Types = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly IOrmAuditContextProvider _contextProvider;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxValueLength;

    /// <summary>Criações com chave gerada pelo banco, gravadas depois do <c>INSERT</c> (uma entrada por salvamento em curso).</summary>
    private readonly ConditionalWeakTable<DbContext, PendingCreations> _pending = new();

    /// <summary>Cria uma nova instância do interceptor de auditoria histórica.</summary>
    public HistoricalAuditInterceptor(IOrmAuditContextProvider contextProvider, TimeProvider timeProvider)
        : this(contextProvider, timeProvider, OrmOptions.DefaultMaxAuditValueLength)
    {
    }

    /// <summary>Cria uma nova instância do interceptor com o limite de tamanho de cada valor da trilha.</summary>
    /// <param name="contextProvider">Ator e ambiente da operação.</param>
    /// <param name="timeProvider">Relógio do <c>TimestampUtc</c>.</param>
    /// <param name="maxValueLength">Tamanho máximo de cada valor em <c>OldValues</c>/<c>NewValues</c> (mínimo 16).</param>
    public HistoricalAuditInterceptor(IOrmAuditContextProvider contextProvider, TimeProvider timeProvider, int maxValueLength)
    {
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxValueLength, 16);
        _maxValueLength = maxValueLength;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        PrepareAsync(eventData.Context, async: false, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await PrepareAsync(eventData.Context, async: true, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        CompleteAsync(eventData.Context, async: false, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        await CompleteAsync(eventData.Context, async: true, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Abort(eventData.Context);

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Abort(eventData.Context);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void SaveChangesCanceled(DbContextEventData eventData) => Abort(eventData.Context);

    /// <inheritdoc />
    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Abort(eventData.Context);
        return Task.CompletedTask;
    }

    /// <summary>O interceptor configurado no contexto (<c>null</c> sem trilha histórica, ex.: <c>EnableHistoricalAudit = false</c>).</summary>
    internal static HistoricalAuditInterceptor? Find(DbContext context) =>
        context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors?
            .OfType<HistoricalAuditInterceptor>().FirstOrDefault();

    /// <summary>A entidade tem <see cref="AuditableAttribute"/> (na classe ou numa base).</summary>
    internal static bool IsAuditable(Type type) => Describe(type).IsAuditable;

    /// <summary>
    /// Grava a trilha da exclusão física (<c>HardDeleteAsync</c>, que não passa pelo rastreador): um registro <c>Deleted</c> por
    /// linha removida, com os valores lidos antes do <c>DELETE</c>. Roda na transação corrente do contexto.
    /// </summary>
    internal async Task WriteHardDeletedAsync(DbContext context, IEntityType entityType, IReadOnlyList<object> deleted,
        CancellationToken cancellationToken)
    {
        if (deleted.Count == 0)
            return;
        var actor = ResolveActor(context);
        var now = _timeProvider.GetUtcNow();
        var described = Describe(entityType.ClrType);
        var records = new List<OrmAuditRecord>(deleted.Count);
        foreach (var entity in deleted)
        {
            var oldValues = new Dictionary<string, object?>();
            var affected = new List<string>();
            foreach (var property in entityType.GetProperties())
            {
                if (property.IsShadowProperty() || described.IsIgnored(property.Name))
                    continue;
                affected.Add(property.Name);
                oldValues[property.Name] = FormatValue(ReadClrValue(property, entity), described.IsSensitive(property.Name));
            }
            string key = string.Join(",", (entityType.FindPrimaryKey()?.Properties ?? []).Select(p => FormatKey(ReadClrValue(p, entity))));
            records.Add(NewRecord(context, entityType.ClrType.Name, key, DeletedAction, oldValues, null, affected, actor, now));
        }
        await OrmAuditRecordWriter.InsertAsync(context, records, async: true, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PrepareAsync(DbContext? context, bool async, CancellationToken cancellationToken)
    {
        if (context is null)
            return;

        // Sobra de um salvamento anterior que não chegou ao fim (não deveria acontecer: Failed/Canceled limpam)
        Abort(context);

        var entries = context.ChangeTracker.Entries()
            .Where(e => e.Entity is not OrmAuditRecord
                        && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                        && Describe(e.Metadata.ClrType).IsAuditable)
            .ToList();
        if (entries.Count == 0)
            return;

        var actor = ResolveActor(context);
        var now = _timeProvider.GetUtcNow();
        bool relational = context.Database.IsRelational();

        var records = new List<OrmAuditRecord>(entries.Count);
        List<EntityEntry>? deferred = null;
        foreach (var entry in entries)
        {
            // Chave gerada pelo banco: o valor atual é temporário; o registro sai depois do INSERT, com o Id real
            if (entry.State == EntityState.Added && relational && HasTemporaryKey(entry))
            {
                (deferred ??= []).Add(entry);
                continue;
            }
            if (CreateRecord(context, entry, entry.State, actor, now) is { } record)
                records.Add(record);
        }

        if (records.Count > 0)
            context.Set<OrmAuditRecord>().AddRange(records);
        if (deferred is null)
            return;

        // Sem transação (nem do contexto nem ambiente): abre uma, para os registros adiados entrarem junto com os dados
        IDbContextTransaction? transaction = null;
        if (context.Database.CurrentTransaction is null && System.Transactions.Transaction.Current is null)
        {
            transaction = async
                ? await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                : context.Database.BeginTransaction();
        }
        _pending.AddOrUpdate(context, new PendingCreations(deferred, actor, now, transaction));
    }

    private async ValueTask CompleteAsync(DbContext? context, bool async, CancellationToken cancellationToken)
    {
        if (context is null || !_pending.TryGetValue(context, out var pending))
            return;
        _pending.Remove(context);

        try
        {
            var records = new List<OrmAuditRecord>(pending.Entries.Count);
            foreach (var entry in pending.Entries)
            {
                if (CreateRecord(context, entry, EntityState.Added, pending.Actor, pending.Now) is { } record)
                    records.Add(record);
            }
            await OrmAuditRecordWriter.InsertAsync(context, records, async, cancellationToken).ConfigureAwait(false);

            if (pending.Transaction is { } transaction)
            {
                if (async)
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                else
                    transaction.Commit();
            }
        }
        catch
        {
            // Sem a trilha, os dados também não ficam: a transação aberta aqui é desfeita (a do chamador fica com ele)
            pending.Rollback();
            throw;
        }
        finally
        {
            pending.Transaction?.Dispose();
        }
    }

    /// <summary>Salvamento que falhou ou foi cancelado: desfaz a transação aberta para os registros adiados.</summary>
    private void Abort(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var pending))
            return;
        _pending.Remove(context);
        pending.Rollback();
        pending.Transaction?.Dispose();
    }

    private OrmAuditActorContext ResolveActor(DbContext context)
    {
        using (OrmAuditIdentity.Enter(context))
            return _contextProvider.GetCurrentContext();
    }

    private OrmAuditRecord? CreateRecord(DbContext context, EntityEntry entry, EntityState state, OrmAuditActorContext actor, DateTimeOffset now)
    {
        var described = Describe(entry.Metadata.ClrType);
        string action = state switch
        {
            EntityState.Added => CreatedAction,
            EntityState.Deleted => DeletedAction,
            _ when entry.Entity is ISoftDelete && entry.Property(nameof(ISoftDelete.IsDeleted)) is { CurrentValue: true } isDeleted
                   && isDeleted.OriginalValue is not true => SoftDeletedAction,
            _ => UpdatedAction
        };

        var oldValues = new Dictionary<string, object?>();
        var newValues = new Dictionary<string, object?>();
        var affectedColumns = new List<string>();
        foreach (var property in entry.Properties)
        {
            string name = property.Metadata.Name;
            if (described.IsIgnored(name))
                continue;
            bool sensitive = described.IsSensitive(name);

            switch (state)
            {
                case EntityState.Added:
                    affectedColumns.Add(name);
                    newValues[name] = FormatValue(property.CurrentValue, sensitive);
                    break;
                case EntityState.Deleted:
                    affectedColumns.Add(name);
                    oldValues[name] = FormatValue(property.OriginalValue, sensitive);
                    break;
                case EntityState.Modified when property.IsModified && !Equals(property.OriginalValue, property.CurrentValue):
                    affectedColumns.Add(name);
                    oldValues[name] = FormatValue(property.OriginalValue, sensitive);
                    newValues[name] = FormatValue(property.CurrentValue, sensitive);
                    break;
            }
        }

        if (affectedColumns.Count == 0 && state == EntityState.Modified)
            return null;

        return NewRecord(context, entry.Metadata.ClrType.Name, GetPrimaryKey(entry), action, oldValues, newValues, affectedColumns, actor, now);
    }

    private static OrmAuditRecord NewRecord(DbContext context, string entityName, string key, string action,
        Dictionary<string, object?>? oldValues, Dictionary<string, object?>? newValues, List<string> affectedColumns,
        OrmAuditActorContext actor, DateTimeOffset now)
    {
        // Texto além do tamanho da coluna derrubaria o salvamento inteiro (dado controlado por quem chama ou pelo token)
        var model = context.Model.FindEntityType(typeof(OrmAuditRecord));
        return new OrmAuditRecord
        {
            EntityName = Fit(model, nameof(OrmAuditRecord.EntityName), entityName) ?? string.Empty,
            EntityKey = Fit(model, nameof(OrmAuditRecord.EntityKey), key) ?? string.Empty,
            Action = action,
            OldValues = oldValues is { Count: > 0 } ? JsonSerializer.Serialize(oldValues, Json) : null,
            NewValues = newValues is { Count: > 0 } ? JsonSerializer.Serialize(newValues, Json) : null,
            AffectedColumns = affectedColumns.Count > 0 ? JsonSerializer.Serialize(affectedColumns, Json) : null,
            ActorId = Fit(model, nameof(OrmAuditRecord.ActorId), actor.ActorId),
            ActorType = Fit(model, nameof(OrmAuditRecord.ActorType), actor.ActorType) ?? "Anonymous",
            ActorName = Fit(model, nameof(OrmAuditRecord.ActorName), actor.ActorName),
            TenantId = Fit(model, nameof(OrmAuditRecord.TenantId), actor.TenantId),
            TraceId = Fit(model, nameof(OrmAuditRecord.TraceId), actor.TraceId),
            CorrelationId = Fit(model, nameof(OrmAuditRecord.CorrelationId), actor.CorrelationId),
            ClientIp = Fit(model, nameof(OrmAuditRecord.ClientIp), actor.ClientIp),
            Endpoint = Fit(model, nameof(OrmAuditRecord.Endpoint), actor.Endpoint),
            TimestampUtc = now
        };
    }

    /// <summary>Corta o texto no tamanho máximo da coluna do modelo (sem tamanho definido: como está).</summary>
    private static string? Fit(IEntityType? model, string property, string? value) =>
        value is not null && model?.FindProperty(property)?.GetMaxLength() is int max && value.Length > max ? value[..max] : value;

    private static bool HasTemporaryKey(EntityEntry entry) =>
        entry.Metadata.FindPrimaryKey()?.Properties.Any(p => entry.Property(p.Name).IsTemporary) == true;

    private static string GetPrimaryKey(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
            return "0";
        return string.Join(",", key.Properties.Select(p =>
        {
            var value = entry.Property(p.Name);
            return FormatKey(value.CurrentValue ?? value.OriginalValue);
        }));
    }

    private static string FormatKey(object? value) => value switch
    {
        null => "0",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "0"
    };

    private static object? ReadClrValue(IProperty property, object entity) =>
        property.PropertyInfo is { } info ? info.GetValue(entity) : property.FieldInfo?.GetValue(entity);

    /// <summary>
    /// Valor para o diff: sensível vira <c>***</c>; texto é cortado em <c>MaxAuditValueLength</c>; binário vira só o tamanho;
    /// tipos simples ficam como estão; o resto vira texto (cortado), para nunca falhar a serialização.
    /// </summary>
    private object? FormatValue(object? value, bool isSensitive)
    {
        if (value is null)
            return null;
        if (isSensitive)
            return "***";
        return value switch
        {
            string text => Truncate(text),
            byte[] bytes => $"<{bytes.Length} bytes>",
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or char
                or Guid or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan or Enum => value,
            _ => Truncate(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
        };
    }

    private string Truncate(string text) =>
        text.Length <= _maxValueLength
            ? text
            : string.Concat(text.AsSpan(0, _maxValueLength), "…(+", (text.Length - _maxValueLength).ToString(CultureInfo.InvariantCulture), ")");

    private static AuditedType Describe(Type type) => Types.GetOrAdd(type, static t => new AuditedType(t));

    /// <summary>Atributos de auditoria de um tipo, lidos uma vez (antes: reflexão por propriedade a cada salvamento).</summary>
    private sealed class AuditedType
    {
        private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sensitive = new(StringComparer.Ordinal);

        public AuditedType(Type type)
        {
            IsAuditable = type.GetCustomAttribute<AuditableAttribute>(inherit: true) is not null;
            // Pela propriedade mais derivada (uma propriedade "new" esconde a da base sem gerar AmbiguousMatchException)
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .GroupBy(p => p.Name, StringComparer.Ordinal)
                         .Select(g => g.OrderByDescending(p => Depth(p.DeclaringType)).First()))
            {
                if (property.GetCustomAttribute<AuditIgnoreAttribute>() is not null)
                    _ignored.Add(property.Name);
                if (property.GetCustomAttribute<SensitiveDataAttribute>() is not null)
                    _sensitive.Add(property.Name);
            }
        }

        public bool IsAuditable { get; }

        public bool IsIgnored(string property) => _ignored.Contains(property);

        public bool IsSensitive(string property) => _sensitive.Contains(property);

        private static int Depth(Type? type)
        {
            int depth = 0;
            for (var current = type; current is not null; current = current.BaseType)
                depth++;
            return depth;
        }
    }

    /// <summary>Criações com chave gerada pelo banco de um salvamento em curso.</summary>
    private sealed class PendingCreations(List<EntityEntry> entries, OrmAuditActorContext actor, DateTimeOffset now,
        IDbContextTransaction? transaction)
    {
        public List<EntityEntry> Entries { get; } = entries;

        public OrmAuditActorContext Actor { get; } = actor;

        public DateTimeOffset Now { get; } = now;

        /// <summary>Transação aberta pelo interceptor (<c>null</c> quando o salvamento já estava numa).</summary>
        public IDbContextTransaction? Transaction { get; } = transaction;

        public void Rollback()
        {
            try
            {
                Transaction?.Rollback();
            }
#pragma warning disable CA1031 // desfazer é o melhor esforço: a exceção original do salvamento é a que importa
            catch (Exception)
#pragma warning restore CA1031
            {
                // Conexão perdida: o banco já desfez a transação ao fechar a sessão
            }
        }
    }
}
