using Microsoft.EntityFrameworkCore;
using TEC.ORM.Auditing;
using TEC.ORM.Entities;
using TEC.ORM.EntityFrameworkCore.Auditing;
using TEC.ORM.EntityFrameworkCore.Idempotency;
using TEC.ORM.EntityFrameworkCore.SoftDelete;

namespace TEC.ORM.Tests.Integration;

/// <summary>Nota auditada com chave gerada pelo banco (identity), usada nos testes de integração de cada banco.</summary>
[Auditable]
public sealed class ProviderNote : AuditableEntity<long>
{
    /// <summary>Código único (índice único: a duplicata é a violação de unicidade de cada banco).</summary>
    public string Code { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Contexto das integrações por banco (SQL Server, PostgreSQL, MySQL e Oracle): nota auditada com exclusão lógica, trilha
/// histórica com o nome de <c>OrmOptions.AuditLogTableName</c> e tabela de idempotência. As tabelas têm prefixo próprio, ficam
/// no schema padrão do usuário da conexão e são recriadas a cada execução.
/// </summary>
public sealed class ProviderIntegrationContext(DbContextOptions<ProviderIntegrationContext> options) : OrmDbContext(options)
{
    public const string NotesTable = "TecOrmIntNotas";
    public const string AuditTable = "TecOrmIntTrilha";
    public const string IdempotencyTable = "TecOrmIntIdempotencia";

    public DbSet<ProviderNote> Notes => Set<ProviderNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProviderNote>(note =>
        {
            note.ToTable(NotesTable);
            note.Property(n => n.Code).HasMaxLength(64).IsRequired();
            note.Property(n => n.Text).HasMaxLength(200).IsRequired();
            note.HasIndex(n => n.Code).IsUnique();
            note.HasSoftDelete();
        });
        // Sem nome explícito: a tabela da trilha vem de OrmOptions.AuditLogTableName (AuditTable, no registro dos testes)
        modelBuilder.AddTecOrmHistoricalAudit();
        modelBuilder.AddTecIdempotency(tableName: IdempotencyTable);
    }
}
