using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TEC.Core.Common.Results;
using TEC.ORM.Mapping;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.Diagnostics;
using TEC.ORM.SqlServer.Security;
using TEC.Vault.Abstractions;
using TEC.Vault.DependencyInjection;
using TEC.Vault.InMemory;

namespace TEC.ORM.Benchmarks;

/// <summary>
/// Peças que rodam em toda operação: observabilidade (Activity, métrica, log), identificador no log (comum e com HMAC),
/// montagem da string de conexão a partir do cofre (com cache) e mapeamento gerado em memória.
/// </summary>
[MemoryDiagnoser]
public class InfrastructureBenchmarks
{
    private static readonly Func<CancellationToken, Task<Result<int>>> Completed = static _ => Task.FromResult(Result<int>.Success(1));

    private OrmOperationRunner _plain = null!;
    private OrmOperationRunner _hashed = null!;
    private OrmConnectionSecurity _security = null!;
    private ServiceProvider _vault = null!;
    private readonly OrmOperation _operation = new(OrmDiagnostics.EntityFrameworkProvider, "get", "Person", isWrite: false) { Identifier = 42 };
    private Person _person = null!;
    private List<Person> _people = [];

    [GlobalSetup]
    public void Setup()
    {
        var options = new OrmOptions { ConnectionSecretName = "benchmark-sql" };
        _plain = new OrmOperationRunner(NullLogger<OrmOperationRunner>.Instance, options);
        _hashed = new OrmOperationRunner(NullLogger<OrmOperationRunner>.Instance,
            new OrmOptions { ConnectionSecretName = "benchmark-sql", IdentifierLogMode = IdentifierLogMode.Hashed });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecVault(vault => vault
            .UseInMemory(o =>
            {
                o.AllowOutsideDevelopment = true;
                o.InitialSecrets["benchmark-sql"] = "Server=tcp:db,1433;Database=dbBench;User ID=app;Password=Senha!123;Encrypt=True";
            })
            .EnableSecretCache(TimeSpan.FromHours(1)));
        _vault = services.BuildServiceProvider();
        _security = new OrmConnectionSecurity(_vault.GetRequiredService<ISecretReader>(), options, NullLogger<OrmConnectionSecurity>.Instance);

        _person = new Person { Id = Guid.NewGuid(), Name = "Ana", Email = "ana@exemplo.com", Age = 30, Address = new Address { City = "São Paulo", StateCode = "SP" } };
        _people = Enumerable.Range(0, 1_000).Select(i => new Person { Id = Guid.NewGuid(), Name = $"P{i}", Email = $"p{i}@x", Age = i % 80 }).ToList();
    }

    [GlobalCleanup]
    public void Cleanup() => _vault.Dispose();

    // ---------- Observabilidade ----------

    [Benchmark]
    public Task<Result<int>> Operation_WithoutObservability() => Completed(CancellationToken.None);

    [Benchmark]
    public Task<Result<int>> Operation_WithObservability() => _plain.ExecuteAsync(_operation, Completed, CancellationToken.None);

    [Benchmark]
    public string Identifier_Plain() => _plain.FormatIdentifier("cliente-0042");

    [Benchmark]
    public string Identifier_Hashed() => _hashed.FormatIdentifier("123.456.789-09");

    // ---------- Conexão (segredo do cofre em cache) ----------

    [Benchmark]
    public async Task<string> ConnectionString_FromVault() =>
        (await _security.BuildConnectionStringAsync(OrmConnectionKind.ReadWrite, CancellationToken.None)).Value.ToString();

    // ---------- Mapeamento gerado ----------

    [Benchmark]
    public PersonDto Map_FromEntity() => PersonDto.FromEntity(_person);

    [Benchmark]
    public PersonDto Map_Manual() => new()
    {
        Id = _person.Id,
        Name = _person.Name,
        Email = _person.Email,
        Age = _person.Age,
        Active = _person.Active,
        City = _person.Address?.City
    };

    [Benchmark]
    public List<PersonSummaryDto> Map_ToDtoList1000() => _people.ToDtoList<Person, PersonSummaryDto>();
}
