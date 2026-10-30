using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using TEC.ORM.Auditing;
using TEC.ORM.Entities;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.EntityFrameworkCore.Auditing;

/// <summary>
/// Convenção que, ao finalizar o modelo:
/// <list type="bullet">
/// <item><description>define o tamanho das colunas de <see cref="IAuditable"/> sem tamanho explícito: "por" (<c>CreatedBy</c>,
/// <c>UpdatedBy</c>, <c>DeletedBy</c>) com até 256 caracteres e "por tenant" (<c>CreatedByTenant</c>, <c>UpdatedByTenant</c>,
/// <c>DeletedByTenant</c>) com até 64;</description></item>
/// <item><description>dá à tabela da trilha histórica (<see cref="OrmAuditRecord"/>) o nome de <c>OrmOptions.AuditLogTableName</c>,
/// quando ele não foi informado explicitamente ao <c>AddTecOrmHistoricalAudit</c>;</description></item>
/// <item><description>no Oracle, grava os valores da trilha (<c>OldValues</c>, <c>NewValues</c>, <c>AffectedColumns</c>) em
/// <c>NCLOB</c>: sem tamanho, o provedor do Oracle usa <c>NVARCHAR2(2000)</c>, e uma alteração com mais texto que isso faria o
/// salvamento inteiro falhar (a trilha é gravada na mesma transação).</description></item>
/// </list>
/// </summary>
/// <remarks>Não cria filtro nem token de concorrência: os registros são globais e o tenant é só auditoria.</remarks>
/// <param name="auditTableName">Nome da tabela da trilha (das opções do contexto); <c>null</c> mantém o do modelo.</param>
/// <param name="database">Banco do provedor do EF Core do contexto.</param>
internal sealed class AuditModelConvention(string? auditTableName = null, OrmDatabase database = OrmDatabase.Other) : IModelFinalizingConvention
{
    private static readonly string[] TrailValueColumns =
        [nameof(OrmAuditRecord.OldValues), nameof(OrmAuditRecord.NewValues), nameof(OrmAuditRecord.AffectedColumns)];

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

        if (modelBuilder.Metadata.FindEntityType(typeof(OrmAuditRecord)) is not { } audit)
            return;

        // Tabela da trilha sem nome explícito: o das opções (o nome passado ao AddTecOrmHistoricalAudit sempre vence)
        if (auditTableName is not null && audit.GetTableNameConfigurationSource() != ConfigurationSource.Explicit)
            audit.Builder.ToTable(auditTableName, audit.GetSchema(), fromDataAnnotation: true);

        // Oracle: texto sem tamanho viraria NVARCHAR2(2000). Tipo escolhido pela aplicação (HasColumnType) não é alterado.
        if (database == OrmDatabase.Oracle)
        {
            foreach (string name in TrailValueColumns)
            {
                if (audit.FindProperty(name) is { } property && property.GetMaxLength() is null)
                    property.Builder.HasColumnType(OracleLargeText);
            }
        }
    }

    /// <summary>Tipo de texto longo do Oracle (Unicode, sem limite prático).</summary>
    internal const string OracleLargeText = "NCLOB";
}
