using Microsoft.EntityFrameworkCore;
using TEC.ORM.Auditing;

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
    /// <param name="tableName">Nome da tabela (padrão: "OrmAuditRecords").</param>
    /// <param name="schema">Schema do banco de dados opcional.</param>
    public static ModelBuilder AddTecOrmHistoricalAudit(this ModelBuilder modelBuilder, string tableName = "OrmAuditRecords", string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OrmAuditRecord>(b =>
        {
            if (string.IsNullOrWhiteSpace(schema))
                b.ToTable(tableName);
            else
                b.ToTable(tableName, schema);

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
    public static ModelBuilder ApplyAuditTrailConfiguration(this ModelBuilder modelBuilder, string tableName = "OrmAuditRecords", string? schema = null)
        => modelBuilder.AddTecOrmHistoricalAudit(tableName, schema);
}
