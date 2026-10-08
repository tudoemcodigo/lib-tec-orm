using Microsoft.EntityFrameworkCore;
using TEC.ORM.Entities;
using TEC.ORM.SqlServer.SoftDelete;

namespace TEC.ORM.Tests.Database.DbFirst;

// Liga o modelo gerado pelo scaffolding ao TEC.ORM sem tocar nos arquivos gerados: as propriedades Id, IsDeleted e DeletedAt
// já existem nas classes geradas e satisfazem as interfaces.

public partial class Customer : IEntity<Guid>, ISoftDelete
{
}

public partial class Order : IEntity<long>, ISoftDelete
{
}

/// <summary>
/// Opção 2 — database first: o schema vem do banco (dbfirst.sql) e o contexto é gerado (Generated\). Sem migrations
/// (<c>ExcludeFromMigrations</c>): quem muda o schema é o script, e o código é gerado de novo com <c>scaffold.ps1</c>.
/// </summary>
public partial class DbFirstContext
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        // Filtro global de exclusão lógica (o contexto gerado não herda de OrmDbContext)
        configurationBuilder.AddTecOrmConventions();
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>().ToTable(t => t.ExcludeFromMigrations());
        modelBuilder.Entity<Order>().ToTable(t => t.ExcludeFromMigrations());
    }
}
