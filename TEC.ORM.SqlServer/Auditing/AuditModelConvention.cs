using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using TEC.ORM.Entities;

namespace TEC.ORM.SqlServer.Auditing;

/// <summary>
/// Convenção que, ao finalizar o modelo, define o tamanho das colunas de <see cref="IAuditable"/> sem tamanho explícito:
/// "por" (<c>CreatedBy</c>, <c>UpdatedBy</c>, <c>DeletedBy</c>) com até 256 caracteres e "por tenant" (<c>CreatedByTenant</c>,
/// <c>UpdatedByTenant</c>, <c>DeletedByTenant</c>) com até 64.
/// </summary>
/// <remarks>Não cria filtro nem token de concorrência: os registros são globais e o tenant é só auditoria.</remarks>
internal sealed class AuditModelConvention : IModelFinalizingConvention
{
    private static readonly (string Name, int MaxLength)[] Columns =
    [
        (nameof(IAuditable.CreatedBy), 256),
        (nameof(IAuditable.UpdatedBy), 256),
        (nameof(IAuditable.DeletedBy), 256),
        (nameof(IAuditable.CreatedByTenant), 64),
        (nameof(IAuditable.UpdatedByTenant), 64),
        (nameof(IAuditable.DeletedByTenant), 64)
    ];

    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> conventionContext)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            if (entityType.IsOwned() || !typeof(IAuditable).IsAssignableFrom(entityType.ClrType))
                continue;

            foreach (var (name, maxLength) in Columns)
            {
                if (entityType.FindProperty(name) is { } property && property.GetMaxLength() is null)
                    property.Builder.HasMaxLength(maxLength);
            }
        }
    }
}
