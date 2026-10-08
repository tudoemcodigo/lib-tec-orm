using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.EntityFrameworkCore;
using TEC.ORM.Paging;
using TEC.ORM.Specifications;
using TEC.ORM.SqlServer;

namespace TEC.ORM.Benchmarks;

/// <summary>
/// Custo do <c>OrmRepository</c> em relação ao EF Core puro fazendo o mesmo trabalho (provedor InMemory: mede o código do ORM,
/// não o banco). Em cada categoria, a linha de base é o EF Core; a razão mostra quanto o ORM acrescenta (observabilidade,
/// <c>Result</c>, validações, lista branca da ordenação, desempate pelo Id).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class RepositoryBenchmarks
{
    private BenchmarkDatabase _database = null!;
    private BenchmarkContext _context = null!;
    private OrmRepository<Person, Guid> _orm = null!;
    private Guid _id;

    [Params(1_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _database = new BenchmarkDatabase(Rows);
        _context = _database.NewContext();
        _orm = _database.Orm(_context);
        _id = _database.Ids[Rows / 2];
    }

    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    // ---------- Ler por Id ----------

    [Benchmark(Baseline = true), BenchmarkCategory("GetById")]
    public Task<Person?> GetById_EfCore() => _context.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == _id);

    [Benchmark, BenchmarkCategory("GetById")]
    public async Task<Person> GetById_Orm() => (await _orm.GetByIdAsync(_id)).Value;

    [Benchmark, BenchmarkCategory("GetById")]
    public async Task<PersonDto> GetById_OrmDto() => (await _orm.GetByIdAsync<PersonDto>(_id)).Value;

    // ---------- Buscar por critério ----------

    [Benchmark(Baseline = true), BenchmarkCategory("Find")]
    public Task<List<Person>> Find_EfCore() =>
        _context.People.AsNoTracking().Where(p => p.Age == 30).OrderBy(p => p.Name).ThenBy(p => p.Id).Take(100_001).ToListAsync();

    [Benchmark, BenchmarkCategory("Find")]
    public async Task<int> Find_Orm() =>
        (await _orm.FindAsync(new Specification<Person>(p => p.Age == 30).OrderByAscending(p => p.Name))).Value.Count;

    // ---------- Página ordenada (SortBy vindo da requisição) ----------

    [Benchmark(Baseline = true), BenchmarkCategory("List")]
    public async Task<int> List_EfCore()
    {
        long total = await _context.People.AsNoTracking().LongCountAsync();
        var page = await _context.People.AsNoTracking().OrderBy(p => p.Name).ThenBy(p => p.Id).Skip(40).Take(20).ToListAsync();
        return page.Count + (int)total;
    }

    [Benchmark, BenchmarkCategory("List")]
    public async Task<int> List_Orm() => (await _orm.ListAsync(new PageRequest(3, 20, "name"))).Value.Items.Count;

    [Benchmark, BenchmarkCategory("List")]
    public async Task<int> List_OrmDto() => (await _orm.ListAsync<PersonSummaryDto>(new PageRequest(3, 20, "name"))).Value.Items.Count;

    // ---------- Contagem e existência ----------

    [Benchmark(Baseline = true), BenchmarkCategory("Count")]
    public Task<long> Count_EfCore() => _context.People.AsNoTracking().LongCountAsync(p => p.Active);

    [Benchmark, BenchmarkCategory("Count")]
    public async Task<long> Count_Orm() => (await _orm.CountAsync(new Specification<Person>(p => p.Active))).Value;

    [Benchmark, BenchmarkCategory("Count")]
    public async Task<bool> Exists_Orm() => (await _orm.ExistsAsync(_id)).Value;

    // ---------- Escrita (cria e exclui logicamente no mesmo contexto) ----------

    [Benchmark(Baseline = true), BenchmarkCategory("Write")]
    public async Task Write_EfCore()
    {
        var person = new Person { Name = "Nova", Email = "nova@exemplo.com", Age = 30 };
        _context.People.Add(person);
        await _context.SaveChangesAsync();
        _context.People.Remove(person);   // o interceptor converte em exclusão lógica
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [Benchmark, BenchmarkCategory("Write")]
    public async Task Write_Orm()
    {
        var created = await _orm.CreateAsync(new Person { Name = "Nova", Email = "nova@exemplo.com", Age = 30 });
        await _orm.DeleteAsync(created.Value.Id);
        _context.ChangeTracker.Clear();
    }
}
