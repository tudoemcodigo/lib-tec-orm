using Microsoft.EntityFrameworkCore;
using TEC.Core.Security;
using TEC.ORM.Entities;
using TEC.ORM.Mapping;
using TEC.ORM.Paging;
using TEC.ORM.SqlServer.SoftDelete;

namespace TEC.ORM.LoadTests.Infrastructure;

// Modelo dos testes de carga (schema "loadtest", criado por loadtest.sql). Conta é auditada e tem rowversion (concorrência otimista
// de verdade sob disputa); Lancamento tem chave identity e chave estrangeira restrita para Conta.

/// <summary>Conta auditada, com e-mail único entre as não excluídas e <c>rowversion</c>.</summary>
public sealed class Account : AuditableEntity<Guid>
{
    /// <summary>Lote do teste que gravou a linha (isolamento entre testes e limpeza no fim).</summary>
    public Guid Batch { get; set; }

    [Sortable]
    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    [Sortable]
    public int Category { get; set; }

    [Sortable]
    public decimal Balance { get; set; }

    public byte[] Version { get; set; } = [];

    public List<LedgerEntry> LedgerEntries { get; set; } = [];
}

/// <summary>Lançamento de uma conta (chave identity).</summary>
public sealed class LedgerEntry : Entity<long>
{
    public Guid Batch { get; set; }

    public Guid AccountId { get; set; }

    public Account? Account { get; set; }

    public decimal Amount { get; set; }

    public DateTimeOffset PostedAt { get; set; }

    public string Description { get; set; } = string.Empty;
}

/// <summary>Projeção usada na listagem paginada (SELECT só destas colunas).</summary>
[MapFrom(typeof(Account))]
public partial class AccountSummaryDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Category { get; set; }

    public decimal Balance { get; set; }
}

/// <summary>Contexto dos testes de carga.</summary>
public sealed class LoadContext(DbContextOptions<LoadContext> options) : OrmDbContext(options)
{
    public const string Schema = "loadtest";

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Account>(e =>
        {
            e.ToTable("Accounts");
            e.Property(c => c.Name).HasMaxLength(150).IsRequired();
            e.Property(c => c.Email).HasMaxLength(254).IsRequired();
            e.Property(c => c.Balance).HasPrecision(18, 2);
            e.Property(c => c.Version).IsRowVersion();
            e.HasSoftDelete(index: false);
        });

        modelBuilder.Entity<LedgerEntry>(e =>
        {
            e.ToTable("LedgerEntries");
            e.Property(l => l.Amount).HasPrecision(18, 2);
            e.Property(l => l.Description).HasMaxLength(200).IsRequired();
            e.HasOne(l => l.Account).WithMany(c => c.LedgerEntries).HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasSoftDelete(index: false);
        });
    }
}

/// <summary>Usuário do escopo (cada worker de carga tem o seu). Registrado como <see cref="ICurrentUser"/> scoped.</summary>
public sealed class TestUser : ICurrentUser
{
    public PrincipalKind Kind => Id is null ? PrincipalKind.Anonymous : PrincipalKind.User;

    public string? Id { get; set; }

    public string? TenantId { get; set; }

    /// <summary>Identidade do worker <paramref name="worker"/>.</summary>
    public TestUser As(int worker)
    {
        Id = $"oid-worker-{worker:D3}";
        TenantId = $"tenant-{worker % 4}";
        return this;
    }
}
