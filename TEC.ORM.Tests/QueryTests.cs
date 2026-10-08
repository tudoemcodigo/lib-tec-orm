using TEC.ORM.Common;
using TEC.ORM.Paging;
using TEC.ORM.Queries;
using TEC.ORM.Specifications;
using TEC.ORM.SqlServer.Internal;
using TEC.ORM.Tests.Database.CodeFirst;

namespace TEC.ORM.Tests;

/// <summary>SqlQuery (parametrização), verificação de somente leitura, especificações e paginação.</summary>
public class QueryTests
{
    // ---------- SqlQuery: nada de valor no texto do SQL ----------

    [Test]
    public async Task Interpolation_becomes_parameters_and_the_value_never_enters_the_sql()
    {
        string attack = "' OR 1=1; DROP TABLE Customers --";
        int minimum = 10;

        var query = SqlQuery.Interpolated("clientes.por-nome", $"SELECT * FROM Customers WHERE Name = {attack} AND Total > {minimum}");

        await Assert.That(query.Sql).IsEqualTo("SELECT * FROM Customers WHERE Name = @p0 AND Total > @p1");
        await Assert.That(query.Sql).DoesNotContain("DROP");
        await Assert.That(query.Parameters["p0"]).IsEqualTo(attack);
        await Assert.That(query.Parameters["p1"]).IsEqualTo(minimum);
    }

    [Test]
    public async Task Escaped_braces_in_interpolation_are_preserved()
    {
        var query = SqlQuery.Interpolated("json", $"SELECT '{{\"a\":1}}' AS J WHERE Id = {5}");
        await Assert.That(query.Sql).IsEqualTo("SELECT '{\"a\":1}' AS J WHERE Id = @p0");
    }

    [Test]
    public async Task Create_with_object_reads_the_public_properties()
    {
        var query = SqlQuery.Create("vendas.totais", "SELECT SUM(Amount) FROM Orders WHERE CreatedOn >= @From AND CustomerId = @Customer",
            new { From = new DateTime(2026, 1, 1), Customer = Guid.Empty });

        await Assert.That(query.Parameters.Count).IsEqualTo(2);
        await Assert.That(query.Parameters["from"]).IsEqualTo(new DateTime(2026, 1, 1));
    }

    [Test]
    public async Task Create_with_concrete_dictionary_uses_the_keys_not_the_dictionary_properties()
    {
        var parameters = new Dictionary<string, object?> { ["Id"] = 7, ["Name"] = "Ana" };

        var query = SqlQuery.Create("clientes.por-id", "SELECT * FROM Customers WHERE Id = @Id AND Name = @Name", parameters);

        await Assert.That(query.Parameters.Count).IsEqualTo(2);
        await Assert.That(query.Parameters["id"]).IsEqualTo(7);
        await Assert.That(query.Parameters.ContainsKey("Count")).IsFalse();
    }

    [Test]
    public async Task ToString_shows_neither_sql_nor_values()
    {
        var query = SqlQuery.Interpolated("clientes.por-email", $"SELECT * FROM Customers WHERE Email = {"ana@exemplo.com"}");
        string text = query.ToString();

        await Assert.That(text).IsEqualTo("SqlQuery { Name = clientes.por-email, Parameters = 1 }");
        await Assert.That(text).DoesNotContain("ana@exemplo.com");
        await Assert.That(text).DoesNotContain("SELECT");
    }

    [Test]
    [Arguments("")]
    [Arguments("nome com espaço")]
    [Arguments("nome\ncom-quebra")]
    [Arguments(".comeca-com-ponto")]
    public async Task Invalid_name_is_rejected(string name)
    {
        await Assert.That(() => SqlQuery.Create(name, "SELECT 1")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Invalid_parameter_name_is_rejected()
    {
        var parameters = new Dictionary<string, object?> { ["id; DROP"] = 1 };
        await Assert.That(() => SqlQuery.Create("x", "SELECT 1", parameters)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Empty_or_too_large_sql_is_rejected()
    {
        await Assert.That(() => SqlQuery.Create("x", "  ")).Throws<ArgumentException>();
        await Assert.That(() => SqlQuery.Create("x", "SELECT " + new string('1', SqlQuery.MaxSqlLength))).Throws<ArgumentException>();
    }

    // ---------- Somente leitura (defesa em profundidade) ----------

    [Test]
    [Arguments("SELECT * FROM Customers")]
    [Arguments("  -- comentário\n select Id from Customers")]
    [Arguments("/* bloco /* aninhado */ */ SELECT 1")]
    [Arguments("WITH t AS (SELECT 1 AS X) SELECT X FROM t")]
    [Arguments("SELECT * FROM Customers WHERE Status = 'DELETE' AND Note = 'it''s; DROP'")]
    [Arguments("SELECT [Update], [Delete] FROM Auditoria")]
    [Arguments("SELECT 1;")]
    [Arguments("SELECT c.Name, SUM(p.Amount) FROM Customers c JOIN Orders p ON p.CustomerId = c.Id GROUP BY c.Name")]
    [Arguments("SELECT 1 -- comentário no fim, sem quebra de linha")]
    [Arguments("SELECT 1 -- comentário\r\nFROM (SELECT 2 AS X) t")]
    [Arguments("SELECT N'DROP TABLE x' AS Text, \"Delete\" AS Coluna FROM [Tabela]]Update]")]
    [Arguments("SELECT 1 /* externo /* -- DROP */ ainda comentário DELETE */ AS X")]
    [Arguments("SELECT Id FROM Orders ORDER BY Id OFFSET 10 ROWS FETCH NEXT 10 ROWS ONLY")]
    [Arguments("SELECT 1e5 AS a, 0x1F AS b, $1 AS c, 1.5 AS d, DATEADD(day, 1, SYSDATETIME()) AS e")]
    [Arguments("SELECT x.value('(/a)[1]', 'int') FROM Documents FOR JSON PATH")]
    public async Task Read_is_accepted(string sql)
    {
        await Assert.That(ReadOnlySqlGuard.Check(sql)).IsNull();
    }

    [Test]
    [Arguments("DELETE FROM Customers")]
    [Arguments("UPDATE Customers SET Name = 'x'")]
    [Arguments("INSERT INTO Customers (Name) VALUES ('x')")]
    [Arguments("SELECT 1 DROP TABLE Customers")]
    [Arguments("SELECT 1; SELECT 2")]
    [Arguments("SELECT * INTO Copia FROM Customers")]
    [Arguments("WITH t AS (SELECT 1 AS X) DELETE FROM Customers")]
    [Arguments("EXEC sp_who")]
    [Arguments("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1')")]
    [Arguments("SELECT 1 WAITFOR DELAY '00:01:00'")]
    [Arguments("SELECT 'sem fechar")]
    [Arguments("SELECT 1 /* sem fechar")]
    [Arguments("TRUNCATE TABLE Customers")]
    [Arguments("SELECT 1 AS a --x\rDELETE FROM dbo.Customers")]
    [Arguments("SELECT 1 AS a --x\r\nDELETE FROM dbo.Customers")]
    [Arguments("SELECT 1 AS a --x\nDELETE FROM dbo.Customers")]
    [Arguments("SELECT 1 /* a /* b */ c */ DELETE FROM Customers")]
    [Arguments("SELECT 1 /* a /* b */ DELETE FROM Customers")]   // aninhado sem o segundo fechamento: comentário aberto
    [Arguments("SELECT 'it''s' AS a DELETE FROM Customers")]
    [Arguments("SELECT N'x''' AS a; DROP TABLE Customers")]
    [Arguments("SELECT [a]]b] AS a DELETE FROM Customers")]
    [Arguments("SELECT \"a\"\"b\" AS a DELETE FROM Customers")]
    [Arguments("SELECT [sem fechar")]
    [Arguments("SELECT 1 UPDATETEXT Customers.Note @ptr 0 NULL 'x'")]
    // Regressões do fuzzing diferencial (Security/Fuzzing): comando colado a um literal numérico, que o SQL Server separa
    [Arguments("SELECT 1DROP TABLE Customers")]
    [Arguments("SELECT 1.5DELETE FROM Customers")]
    [Arguments("SELECT 1e5EXEC sp_who")]
    [Arguments("SELECT 1.e5DROP TABLE Customers")]
    [Arguments("SELECT 1.E+5EXEC sp_who")]
    [Arguments("SELECT $1TRUNCATE TABLE Customers")]
    [Arguments("SELECT 0x1FRECONFIGURE")]
    [Arguments("SELECT [x]=1DROP TABLE Customers")]
    // ... e comandos de escrita que não estavam na lista
    [Arguments("SELECT 1 DISABLE TRIGGER ALL ON Customers")]
    [Arguments("SELECT 1 ENABLE TRIGGER ALL ON DATABASE")]
    [Arguments("SELECT 1 ADD SIGNATURE TO dbo.p BY CERTIFICATE c")]
    [Arguments("SELECT 1 ADD SENSITIVITY CLASSIFICATION TO Customers.Name WITH (LABEL = 'x')")]
    [Arguments("SELECT 1 OPEN SYMMETRIC KEY k DECRYPTION BY CERTIFICATE c")]
    [Arguments("SELECT 1 CLOSE ALL SYMMETRIC KEYS")]
    [Arguments("SELECT 1 DEALLOCATE c")]
    [Arguments("SELECT 1 SEND ON CONVERSATION @h (0x01)")]
    [Arguments("SELECT 1 END CONVERSATION @h")]
    [Arguments("SELECT NEXT VALUE FOR dbo.Sequencia")]
    [Arguments("SELECT Id FROM Customers WHERE Id = next  value\nfor dbo.Sequencia")]
    public async Task Write_or_malformed_sql_is_rejected(string sql)
    {
        await Assert.That(ReadOnlySqlGuard.Check(sql)).IsNotNull();
    }

    // ---------- Especificações ----------

    [Test]
    public async Task Where_combines_filters_with_and()
    {
        var spec = new Specification<Customer>(c => c.Active).Where(c => c.Name.StartsWith("A"));
        var compiled = spec.Criteria!.Compile();

        await Assert.That(compiled(new Customer { Name = "Ana", Active = true })).IsTrue();
        await Assert.That(compiled(new Customer { Name = "Ana", Active = false })).IsFalse();
        await Assert.That(compiled(new Customer { Name = "Bia", Active = true })).IsFalse();
    }

    [Test]
    public async Task Ordering_and_includes_keep_the_given_order()
    {
        var spec = new Specification<Order>()
            .OrderByDescending(p => p.Amount)
            .OrderByAscending(p => p.Id)
            .Include(nameof(Order.Customer));

        await Assert.That(spec.OrderBy.Count).IsEqualTo(2);
        await Assert.That(spec.OrderBy[0].Descending).IsTrue();
        await Assert.That(spec.Includes[0]).IsEqualTo("Customer");
        await Assert.That(() => spec.Include(" ")).Throws<ArgumentException>();
    }

    // ---------- Paginação ----------

    [Test]
    [Arguments(0, 10, "page")]
    [Arguments(1, 0, "pageSize")]
    [Arguments(1, 101, "pageSize")]
    [Arguments(int.MaxValue, 100, "page")]
    public async Task Invalid_page_is_rejected(int page, int size, string field)
    {
        var error = new PageRequest(page, size).Validate(maxPageSize: 100);

        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Code).IsEqualTo(OrmErrors.InvalidInputCode);
        await Assert.That(error.Field).IsEqualTo(field);
    }

    [Test]
    public async Task Valid_page_computes_the_offset()
    {
        var page = new PageRequest(3, 20);
        await Assert.That(page.Validate(100)).IsNull();
        await Assert.That(page.Offset).IsEqualTo(40);
    }
}
