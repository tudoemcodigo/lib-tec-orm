using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using TEC.ORM.Entities;
using TEC.ORM.Paging;
using TEC.ORM.SqlServer.SoftDelete;

namespace TEC.ORM.Tests.Aggregates;

// Modelo só dos testes unitários (EF Core InMemory): um agregado com tipos owned, dependentes em cascata com e sem exclusão
// lógica, dependente opcional e token de concorrência. Fica fora do CodeFirstContext para não mexer nas migrations.

public sealed class Sale : Entity<int>
{
    public string Name { get; set; } = string.Empty;

    [ConcurrencyCheck]
    public int Version { get; set; }

    public string? PasswordHash { get; set; }

    public ShippingAddress? Address { get; set; }

    public List<Label> Labels { get; set; } = [];

    public List<SaleItem> Items { get; set; } = [];

    public List<Installment> Installments { get; set; } = [];

    public List<Note> Notes { get; set; } = [];
}

/// <summary>Owned (OwnsOne, mesma tabela).</summary>
public sealed class ShippingAddress
{
    public string Street { get; set; } = string.Empty;
}

/// <summary>Owned (OwnsMany, tabela própria).</summary>
public sealed class Label
{
    public int Id { get; set; }

    public string Value { get; set; } = string.Empty;
}

/// <summary>Dependente em cascata sem exclusão lógica.</summary>
public sealed class SaleItem
{
    public int Id { get; set; }

    public int SaleId { get; set; }

    public string Description { get; set; } = string.Empty;
}

/// <summary>Dependente em cascata com exclusão lógica (com um nível abaixo).</summary>
public sealed class Installment : Entity<int>
{
    public int SaleId { get; set; }

    public decimal Amount { get; set; }

    public List<LedgerEntry> LedgerEntries { get; set; } = [];
}

public sealed class LedgerEntry : Entity<int>
{
    public int InstallmentId { get; set; }

    public string Memo { get; set; } = string.Empty;
}

/// <summary>Dependente opcional (SetNull) sem exclusão lógica.</summary>
public sealed class Note
{
    public int Id { get; set; }

    public int? SaleId { get; set; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>Entidade com lista branca de ordenação.</summary>
public sealed class AppUser : Entity<int>
{
    [Sortable]
    public string Name { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;
}

public sealed class AggregateContext(DbContextOptions<AggregateContext> options) : OrmDbContext(options)
{
    public DbSet<Sale> Sales => Set<Sale>();

    public DbSet<Installment> Installments => Set<Installment>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    public DbSet<SaleItem> Items => Set<SaleItem>();

    public DbSet<Note> Notes => Set<Note>();

    public DbSet<AppUser> AppUsers => Set<AppUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Sale>(e =>
        {
            e.OwnsOne(v => v.Address);
            e.OwnsMany(v => v.Labels);
            e.HasMany(v => v.Items).WithOne().HasForeignKey(i => i.SaleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(v => v.Installments).WithOne().HasForeignKey(p => p.SaleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(v => v.Notes).WithOne().HasForeignKey(n => n.SaleId).OnDelete(DeleteBehavior.ClientSetNull);
            e.Property<string>("Origem");   // propriedade sombra
        });
        modelBuilder.Entity<Installment>().HasMany(p => p.LedgerEntries).WithOne().HasForeignKey(l => l.InstallmentId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AppUser>();
    }
}
