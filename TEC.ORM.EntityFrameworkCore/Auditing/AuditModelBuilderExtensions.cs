using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TEC.ORM.Auditing;
using TEC.ORM.EntityFrameworkCore.Configuration;

namespace TEC.ORM.EntityFrameworkCore.Auditing;

/// <summary>
/// Extensões do <see cref="ModelBuilder"/> para configuração da tabela de auditoria histórica.
/// </summary>
public static class AuditModelBuilderExtensions
{
    /// <summary>
    /// Mapeia o registro histórico de auditoria (<see cref="OrmAuditRecord"/>) com índices para consulta e expurgo.
    /// </summary>
    /// <param name="modelBuilder">Construtor de modelos do EF Core.</param>
    /// <param name="tableName">
    /// Nome da tabela. <c>null</c> (padrão): o de <see cref="OrmOptions.AuditLogTableName"/>, aplicado pelas convenções do TEC.ORM
    /// (<c>OrmOptions.DefaultAuditLogTableName</c>, <c>OrmAuditRecords</c>, sem elas).
    /// </param>
    /// <param name="schema">Schema do banco de dados opcional.</param>
    public static ModelBuilder AddTecOrmHistoricalAudit(this ModelBuilder modelBuilder, string? tableName = null, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        if (tableName is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        modelBuilder.Entity<OrmAuditRecord>(b =>
        {
            string? effectiveSchema = string.IsNullOrWhiteSpace(schema) ? null : schema;
            if (tableName is not null)
                b.ToTable(tableName, effectiveSchema);
            else
                // Abaixo do explícito: a convenção do TEC.ORM troca pelo OrmOptions.AuditLogTableName do contexto
                ((IConventionEntityType)b.Metadata).Builder.ToTable(OrmOptions.DefaultAuditLogTableName, effectiveSchema, fromDataAnnotation: true);

            b.HasKey(r => r.Id);
            b.Property(r => r.EntityName).HasMaxLength(128).IsRequired();
            b.Property(r => r.EntityKey).HasMaxLength(128).IsRequired();
            b.Property(r => r.Action).HasMaxLength(32).IsRequired();
            b.Property(r => r.ActorId).HasMaxLength(256);
            b.Property(r => r.ActorType).HasMaxLength(32).IsRequired();
            b.Property(r => r.ActorName).HasMaxLength(256);
            b.Property(r => r.TenantId).HasMaxLength(64);
            b.Property(r => r.TraceId).HasMaxLength(128);
            b.Property(r => r.CorrelationId).HasMaxLength(128);
            b.Property(r => r.ClientIp).HasMaxLength(64);
            b.Property(r => r.Endpoint).HasMaxLength(256);

            b.HasIndex(r => new { r.EntityName, r.EntityKey });
            b.HasIndex(r => r.TimestampUtc);
            b.HasIndex(r => r.ActorId);
        });

        return modelBuilder;
    }

    /// <summary>
    /// Alias para <see cref="AddTecOrmHistoricalAudit"/>, configura a entidade <see cref="OrmAuditRecord"/> no modelo.
    /// </summary>
    public static ModelBuilder ApplyAuditTrailConfiguration(this ModelBuilder modelBuilder, string? tableName = null, string? schema = null)
        => modelBuilder.AddTecOrmHistoricalAudit(tableName, schema);
}
