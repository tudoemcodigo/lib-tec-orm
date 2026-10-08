using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TEC.Core.Common.Results;
using TEC.Core.Security;
using TEC.ORM.Common;
using TEC.ORM.Entities;

namespace TEC.ORM.SqlServer.Auditing;

/// <summary>Gravação recusada pela auditoria. Convertida no erro <see cref="Error"/> pelo repositório.</summary>
internal sealed class OrmAuditException(Error error) : InvalidOperationException("Gravação recusada: " + error.Code)
{
    public Error Error { get; } = error;
}

/// <summary>
/// Preenche e protege os campos de <see cref="IAuditable"/> ao salvar, com o <see cref="ICurrentUser"/> do contexto
/// (<see cref="OrmIdentity"/>). Roda <b>depois</b> do interceptor de exclusão lógica (a exclusão já virou alteração de
/// <c>IsDeleted</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>Sem identidade autenticada</b>: a gravação de qualquer entidade auditada é recusada
/// (<see cref="OrmErrors.AuditIdentityRequired"/>); nada é gravado.</description></item>
/// <item><description><b>Criação</b>: <c>CreatedAt</c>/<c>CreatedBy</c>/<c>CreatedByTenant</c> do relógio e da identidade (o que
/// vier no objeto é ignorado).</description></item>
/// <item><description><b>Alteração</b>: <c>UpdatedAt</c>/<c>UpdatedBy</c>/<c>UpdatedByTenant</c>; os campos de criação e de
/// exclusão ficam fora do <c>UPDATE</c>.</description></item>
/// <item><description><b>Exclusão lógica</b>: <c>DeletedBy</c>/<c>DeletedByTenant</c> (o instante já está em <c>DeletedAt</c>);
/// os campos de alteração ficam fora do <c>UPDATE</c>.</description></item>
/// </list>
/// O tenant é só informativo (<c>null</c> se a identidade não tem tenant): os registros são globais e não há restrição por tenant.
/// </remarks>
internal sealed class AuditInterceptor(TimeProvider time) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return ValueTask.FromResult(result);
    }

    internal void Apply(DbContext? context)
    {
        if (context is null)
            return;

        var entries = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && e.Entity is IAuditable)
            .ToList();
        if (entries.Count == 0)
            return;

        var user = OrmIdentity.Current(context);
        if (user is not { IsAuthenticated: true, Id: { Length: > 0 } userId })
            throw new OrmAuditException(OrmErrors.AuditIdentityRequired());

        string? tenantId = user.TenantId is { Length: > 0 } t ? t : null;
        var now = time.GetUtcNow();

        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    OnAdded(entry, userId, tenantId, now);
                    break;
                case EntityState.Modified:
                    OnModified(entry, userId, tenantId, now);
                    break;
            }
        }
    }

    private static void OnAdded(EntityEntry entry, string userId, string? tenantId, DateTimeOffset now)
    {
        entry.Property(nameof(IAuditable.CreatedAt)).CurrentValue = now;
        entry.Property(nameof(IAuditable.CreatedBy)).CurrentValue = userId;
        entry.Property(nameof(IAuditable.CreatedByTenant)).CurrentValue = tenantId;
        entry.Property(nameof(IAuditable.UpdatedAt)).CurrentValue = null;
        entry.Property(nameof(IAuditable.UpdatedBy)).CurrentValue = null;
        entry.Property(nameof(IAuditable.UpdatedByTenant)).CurrentValue = null;
        entry.Property(nameof(IAuditable.DeletedBy)).CurrentValue = null;
        entry.Property(nameof(IAuditable.DeletedByTenant)).CurrentValue = null;
    }

    private static void OnModified(EntityEntry entry, string userId, string? tenantId, DateTimeOffset now)
    {
        Keep(entry, nameof(IAuditable.CreatedAt));
        Keep(entry, nameof(IAuditable.CreatedBy));
        Keep(entry, nameof(IAuditable.CreatedByTenant));

        var isDeleted = entry.Property(nameof(ISoftDelete.IsDeleted));
        bool deleting = isDeleted.IsModified && isDeleted.CurrentValue is true && isDeleted.OriginalValue is not true;
        if (deleting)
        {
            entry.Property(nameof(IAuditable.DeletedBy)).CurrentValue = userId;
            entry.Property(nameof(IAuditable.DeletedByTenant)).CurrentValue = tenantId;
            Keep(entry, nameof(IAuditable.UpdatedAt));
            Keep(entry, nameof(IAuditable.UpdatedBy));
            Keep(entry, nameof(IAuditable.UpdatedByTenant));
        }
        else
        {
            Keep(entry, nameof(IAuditable.DeletedBy));
            Keep(entry, nameof(IAuditable.DeletedByTenant));
            entry.Property(nameof(IAuditable.UpdatedAt)).CurrentValue = now;
            entry.Property(nameof(IAuditable.UpdatedBy)).CurrentValue = userId;
            entry.Property(nameof(IAuditable.UpdatedByTenant)).CurrentValue = tenantId;
        }
    }

    /// <summary>Coluna fora do <c>UPDATE</c> (o valor do objeto, se alterado pela aplicação, não é gravado).</summary>
    private static void Keep(EntityEntry entry, string property)
    {
        var value = entry.Property(property);
        if (value.IsModified)
        {
            value.CurrentValue = value.OriginalValue;
            value.IsModified = false;
        }
    }
}
