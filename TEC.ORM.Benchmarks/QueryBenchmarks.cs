using BenchmarkDotNet.Attributes;
using TEC.ORM.Paging;
using TEC.ORM.Queries;
using TEC.ORM.SqlServer.Internal;

namespace TEC.ORM.Benchmarks;

/// <summary>
/// Preparação das leituras complexas, antes de abrir a conexão: montagem do <see cref="SqlQuery"/> (parametrização) e
/// verificação de somente leitura (custo por consulta, inclusive no tamanho máximo de 32 KB), e validação da página.
/// </summary>
[MemoryDiagnoser]
public class QueryBenchmarks
{
    private const string Typical =
        "SELECT c.Id, c.Name, SUM(p.Amount) AS Total FROM Customers c JOIN Orders p ON p.CustomerId = c.Id " +
        "WHERE c.Active = 1 AND p.CreatedOn >= @From /* relatório mensal */ GROUP BY c.Id, c.Name ORDER BY Total DESC";

    private readonly Guid _customer = Guid.NewGuid();
    private readonly DateTime _inicio = new(2026, 1, 1);
    private string _large = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        // Consulta no limite: muitas colunas, literais e comentários (pior caso realista da verificação)
        var builder = new System.Text.StringBuilder("SELECT ");
        int i = 0;
        while (builder.Length < SqlQuery.MaxSqlLength - 200)
            builder.Append("Coluna").Append(i).Append(" = N'texto ''").Append(i++).Append("''' /* c */, ");
        builder.Append("1 AS Fim FROM Tabela WHERE Id = @Id");
        _large = builder.ToString();
    }

    [Benchmark]
    public SqlQuery Interpolated() =>
        SqlQuery.Interpolated("clientes.resumo", $"SELECT Id, Name FROM Customers WHERE Id = {_customer} AND CreatedOn >= {_inicio} AND Active = {true}");

    [Benchmark]
    public SqlQuery CreateWithObject() =>
        SqlQuery.Create("vendas.totais", Typical, new { From = _inicio, Customer = _customer });

    [Benchmark]
    public string? ReadOnlyGuard_Typical() => ReadOnlySqlGuard.Check(Typical);

    [Benchmark]
    public string? ReadOnlyGuard_32KB() => ReadOnlySqlGuard.Check(_large);

    [Benchmark]
    public bool PageRequest_Validate() => new PageRequest(5, 50, "Name", Descending: true).Validate(100) is null;
}
