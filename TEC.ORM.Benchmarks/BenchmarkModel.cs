using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TEC.ORM.Entities;
using TEC.ORM.Mapping;
using TEC.ORM.Paging;
using TEC.ORM.SqlServer;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.Diagnostics;
using TEC.ORM.SqlServer.SoftDelete;

namespace TEC.ORM.Benchmarks;

/// <summary>Entidade dos benchmarks.</summary>
public sealed class Person : Entity<Guid>
{
    [Sortable]
    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    [Sortable]
    public int Age { get; set; }

    public bool Active { get; set; } = true;

    public Address? Address { get; set; }
}

/// <summary>Objeto aninhado (owned).</summary>
public sealed class Address
{
    public string City { get; set; } = string.Empty;

    public string StateCode { get; set; } = string.Empty;
}

[MapFrom(typeof(Person))]
public partial class PersonDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public int Age { get; set; }

    public bool Active { get; set; }

    [MapFrom("Address.City")]
    public string? City { get; set; }
}

[MapFrom(typeof(Person))]
public partial class PersonSummaryDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class BenchmarkContext(DbContextOptions<BenchmarkContext> options) : OrmDbContext(options)
{
    public DbSet<Person> People => Set<Person>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Person>(e =>
        {
            e.OwnsOne(p => p.Address);
            e.HasSoftDelete();
        });
}

/// <summary>Banco InMemory com <paramref name="rows"/> pessoas e o ORM montado como o <c>AddTecOrm</c> monta.</summary>
public sealed class BenchmarkDatabase
{
    public BenchmarkDatabase(int rows)
    {
        string name = Guid.NewGuid().ToString("N");
        Options = new DbContextOptionsBuilder<BenchmarkContext>()
            .UseInMemoryDatabase(name)
            .AddInterceptors(new SoftDeleteInterceptor(TimeProvider.System))
            .Options;
        OrmOptions = new OrmOptions { ConnectionSecretName = "benchmark", MaxFindResults = 100_000, MaxPageSize = 1_000 };
        Observability = new OrmOperationRunner(NullLogger<OrmOperationRunner>.Instance, OrmOptions);

        using var seed = new BenchmarkContext(Options);
        seed.People.AddRange(Enumerable.Range(0, rows).Select(i => new Person
        {
            Name = $"Person {i:D6}",
            Email = $"p{i}@exemplo.com",
            Age = 18 + i % 60,
            Address = new Address { City = $"City {i % 100}", StateCode = "SP" }
        }));
        seed.SaveChanges();
        Ids = seed.People.AsNoTracking().Select(p => p.Id).ToArray();
    }

    public DbContextOptions<BenchmarkContext> Options { get; }

    public OrmOptions OrmOptions { get; }

    public OrmOperationRunner Observability { get; }

    public Guid[] Ids { get; }

    public BenchmarkContext NewContext() => new(Options);

    public OrmRepository<Person, Guid> Orm(BenchmarkContext context) => new(context, Observability, OrmOptions);
}
