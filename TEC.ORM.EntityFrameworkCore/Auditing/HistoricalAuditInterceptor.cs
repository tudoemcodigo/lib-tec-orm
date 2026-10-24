using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TEC.Core.Security;
using TEC.ORM.Auditing;
using TEC.ORM.Entities;

namespace TEC.ORM.EntityFrameworkCore.Auditing;

/// <summary>
/// Interceptor transacional do EF Core responsável por capturar alterações em entidades anotadas com [Auditable]
/// e persistir os registros de trilha histórica (OrmAuditRecord) na mesma transação atômica.
/// </summary>
public sealed class HistoricalAuditInterceptor : SaveChangesInterceptor
{
    private readonly IOrmAuditContextProvider _contextProvider;
    private readonly TimeProvider _timeProvider;

    /// <summary>Cria uma nova instância do interceptor de auditoria histórica.</summary>
    public HistoricalAuditInterceptor(IOrmAuditContextProvider contextProvider, TimeProvider timeProvider)
    {
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ApplyAudit(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ApplyAudit(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void ApplyAudit(DbContext? context)
    {
        if (context == null) return;

        var entries = context.ChangeTracker.Entries()
            .Where(e => e.Entity is not OrmAuditRecord
                        && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                        && IsAuditable(e.Entity.GetType()))
            .ToList();

        if (entries.Count == 0) return;

        var actorContext = _contextProvider.GetCurrentContext();
        var now = _timeProvider.GetUtcNow();

        var auditRecords = new List<OrmAuditRecord>(entries.Count);

        foreach (var entry in entries)
        {
            var entityType = entry.Entity.GetType();
            string entityName = entityType.Name;
            string key = GetPrimaryKey(entry);

            string action;
            if (entry.State == EntityState.Added)
            {
                action = "Created";
            }
            else if (entry.State == EntityState.Deleted)
            {
                action = "Deleted";
            }
            else
            {
                // Verifica se foi SoftDelete
                if (entry.Entity is ISoftDelete && entry.Property(nameof(ISoftDelete.IsDeleted)).CurrentValue is true
                    && (bool)(entry.Property(nameof(ISoftDelete.IsDeleted)).OriginalValue ?? false) == false)
                {
                    action = "SoftDeleted";
                }
                else
                {
                    action = "Updated";
                }
            }

            var oldValues = new Dictionary<string, object?>();
            var newValues = new Dictionary<string, object?>();
            var affectedColumns = new List<string>();

            foreach (var prop in entry.Properties)
            {
                string propName = prop.Metadata.Name;

                // Verifica se tem [AuditIgnore]
                var clrProperty = entityType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (clrProperty != null && clrProperty.GetCustomAttribute<AuditIgnoreAttribute>() != null)
                {
                    continue;
                }

                bool isSensitive = clrProperty?.GetCustomAttribute<SensitiveDataAttribute>() != null;

                if (entry.State == EntityState.Added)
                {
                    affectedColumns.Add(propName);
                    newValues[propName] = FormatValue(prop.CurrentValue, isSensitive);
                }
                else if (entry.State == EntityState.Deleted)
                {
                    affectedColumns.Add(propName);
                    oldValues[propName] = FormatValue(prop.OriginalValue, isSensitive);
                }
                else if (entry.State == EntityState.Modified)
                {
                    if (prop.IsModified && !Equals(prop.OriginalValue, prop.CurrentValue))
                    {
                        affectedColumns.Add(propName);
                        oldValues[propName] = FormatValue(prop.OriginalValue, isSensitive);
                        newValues[propName] = FormatValue(prop.CurrentValue, isSensitive);
                    }
                }
            }

            if (affectedColumns.Count == 0 && entry.State == EntityState.Modified)
            {
                continue;
            }

            var record = new OrmAuditRecord
            {
                EntityName = entityName,
                EntityKey = key,
                Action = action,
                OldValues = oldValues.Count > 0 ? JsonSerializer.Serialize(oldValues) : null,
                NewValues = newValues.Count > 0 ? JsonSerializer.Serialize(newValues) : null,
                AffectedColumns = affectedColumns.Count > 0 ? JsonSerializer.Serialize(affectedColumns) : null,
                ActorId = actorContext.ActorId,
                ActorType = actorContext.ActorType,
                ActorName = actorContext.ActorName,
                TenantId = actorContext.TenantId,
                TraceId = actorContext.TraceId,
                CorrelationId = actorContext.CorrelationId,
                ClientIp = actorContext.ClientIp,
                Endpoint = actorContext.Endpoint,
                TimestampUtc = now
            };

            auditRecords.Add(record);
        }

        if (auditRecords.Count > 0)
        {
            context.Set<OrmAuditRecord>().AddRange(auditRecords);
        }
    }

    private static bool IsAuditable(Type type) =>
        type.GetCustomAttribute<AuditableAttribute>(inherit: true) != null;

    private static string GetPrimaryKey(EntityEntry entry)
    {
        var key = entry.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey());
        return key?.CurrentValue?.ToString() ?? key?.OriginalValue?.ToString() ?? "0";
    }

    private static object? FormatValue(object? value, bool isSensitive)
    {
        if (value == null) return null;
        if (isSensitive) return "***";
        return value;
    }
}
