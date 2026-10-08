using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TEC.ORM.SqlServer.SoftDelete;

namespace TEC.ORM.Tests.Database.CodeFirst;

/// <summary>
/// Opção 1 — code first: o modelo é a fonte da verdade e o schema "codefirst" do banco de testes é criado/atualizado por migrations
/// (pasta Migrations). Gerar uma nova migration (na pasta TEC.ORM):
/// <code>
/// dotnet tool restore
/// dotnet ef migrations add NomeDaMigration --project TEC.ORM.Tests --context CodeFirstContext --output-dir Database/CodeFirst/Migrations --framework net10.0
/// dotnet ef migrations script --idempotent --project TEC.ORM.Tests --context CodeFirstContext --framework net10.0 --output Database/CodeFirst/codefirst.sql
/// </code>
/// Aplicar: em tempo de execução (<c>Database.MigrateAsync()</c>, como a fixture de integração faz, com a conexão do TEC.Vault)
/// ou entregando o script idempotente ao DBA. Nenhum dos dois caminhos precisa da string de conexão em arquivo.
/// </summary>
public sealed class CodeFirstContext(DbContextOptions<CodeFirstContext> options) : OrmDbContext(options)
{
    public const string Schema = "codefirst";

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Order> Orders => Set<Order>();

    /// <summary>Ajustes do provedor usados pela aplicação e pelo design time (histórico de migrations no próprio schema).</summary>
    public static void ConfigureSqlServer(SqlServerDbContextOptionsBuilder sql) =>
        sql.MigrationsHistoryTable("__EFMigrationsHistory", Schema);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Category>(e =>
        {
            e.ToTable("Categories");
            e.Property(c => c.Id).HasMaxLength(20);
            e.Property(c => c.Name).HasMaxLength(100).IsRequired();
            e.HasSoftDelete();
        });

        modelBuilder.Entity<Product>(e =>
        {
            e.ToTable("Products");
            e.Property(p => p.Name).HasMaxLength(150).IsRequired();
            e.Property(p => p.Price).HasPrecision(18, 2);
            e.Property(p => p.CategoryId).HasMaxLength(20);
            e.HasOne(p => p.Category).WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasSoftDelete();
        });

        modelBuilder.Entity<Customer>(e =>
        {
            e.ToTable("Customers");
            e.Property(c => c.Name).HasMaxLength(150).IsRequired();
            e.Property(c => c.Email).HasMaxLength(254).IsRequired();
            // Único só entre os não excluídos: o e-mail de um cliente excluído pode ser reaproveitado
            e.HasIndex(c => c.Email).IsUnique().HasFilter("[IsDeleted] = 0");
            e.HasSoftDelete();
        });

        modelBuilder.Entity<Order>(e =>
        {
            e.ToTable("Orders");
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.HasOne(p => p.Customer).WithMany(c => c.Orders).HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.Product).WithMany().HasForeignKey(p => p.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasSoftDelete();
        });
    }
}

/// <summary>
/// Fábrica de design time do <c>dotnet ef</c>. Sem string de conexão: <c>migrations add</c> e <c>migrations script</c> não
/// conectam ao banco, só precisam do provedor.
/// </summary>
public sealed class CodeFirstDesignTimeFactory : IDesignTimeDbContextFactory<CodeFirstContext>
{
    public CodeFirstContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CodeFirstContext>().UseSqlServer(CodeFirstContext.ConfigureSqlServer).Options);
}
