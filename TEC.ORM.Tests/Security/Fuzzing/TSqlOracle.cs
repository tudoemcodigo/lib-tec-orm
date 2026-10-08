using FsCheck;
using FsCheck.Fluent;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace TEC.ORM.Tests.Security.Fuzzing;

/// <summary>
/// Oráculo do fuzzing diferencial: o parser oficial do T-SQL (ScriptDom, o mesmo do SQL Server e do SSDT) diz quais comandos
/// um lote executa de fato. Um lote é perigoso se tem qualquer comando que não seja leitura.
/// </summary>
internal static class TSqlOracle
{
    /// <summary>Resultado: o lote é T-SQL válido e, se for, o primeiro comando que escreve (<c>null</c> = só leitura).</summary>
    public readonly record struct Verdict(bool Parsed, string? Dangerous);

    /// <summary>
    /// Comandos que não escrevem: leitura, controle de fluxo e mensagens. Todo o resto (DML, DDL, permissões, sessão,
    /// transação, Service Broker, cursores, chaves de criptografia, servidor) é perigoso.
    /// </summary>
    private static readonly HashSet<Type> ReadOnlyStatements =
    [
        typeof(SelectStatement), typeof(PrintStatement), typeof(ThrowStatement), typeof(RaiseErrorStatement),
        typeof(IfStatement), typeof(WhileStatement), typeof(ReturnStatement), typeof(BreakStatement), typeof(ContinueStatement),
        typeof(GoToStatement), typeof(LabelStatement), typeof(LineNoStatement), typeof(ReadTextStatement), typeof(FetchCursorStatement),
        typeof(BeginEndBlockStatement), typeof(TryCatchStatement),
    ];

    /// <summary>Analisa o lote com o parser do SQL Server 2022 (<c>QUOTED_IDENTIFIER ON</c>, como o SqlClient).</summary>
    public static Verdict Analyze(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out IList<ParseError> errors);
        if (errors.Count > 0 || fragment is null)
            return new Verdict(false, null);

        var visitor = new DangerVisitor();
        fragment.Accept(visitor);
        return new Verdict(true, visitor.Dangerous);
    }

    private sealed class DangerVisitor : TSqlFragmentVisitor
    {
        public string? Dangerous { get; private set; }

        public override void Visit(TSqlStatement node)
        {
            if (!ReadOnlyStatements.Contains(node.GetType()))
                Dangerous ??= node.GetType().Name;
            else if (node is SelectStatement { Into: not null })
                Dangerous ??= "SELECT ... INTO";
            base.Visit(node);
        }

        // NEXT VALUE FOR avança a sequência: altera o banco mesmo dentro de um SELECT
        public override void Visit(NextValueForExpression node)
        {
            Dangerous ??= "NEXT VALUE FOR";
            base.Visit(node);
        }
    }
}

/// <summary>Gramática dos lotes do fuzzing diferencial: leituras legítimas misturadas a comandos de escrita, colados de várias formas.</summary>
internal static class TSqlCorpus
{
    /// <summary>Leituras que a verificação precisa aceitar.</summary>
    public static readonly string[] ReadStatements =
    [
        "SELECT a FROM t", "SELECT * FROM dbo.t WHERE x = 'DROP TABLE t'", "SELECT [Update], [Delete], \"Insert\" FROM t",
        "WITH c AS (SELECT 1 AS a) SELECT a FROM c", "SELECT a FROM t ORDER BY a OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY",
        "SELECT COUNT(*) FROM t", "SELECT x.value('(/a)[1]', 'int') FROM t", "SELECT N'it''s' AS s", "SELECT a FROM t FOR JSON PATH",
        "SELECT DATEADD(day, 1, GETDATE())", "SELECT 1 /* DELETE */", "SELECT 1", "SELECT 1.5", "SELECT 1e5", "SELECT $1",
        "SELECT 0x1F", "SELECT a FROM t WHERE b = 2", "SELECT TOP (5) a FROM t", "SELECT a FROM t WITH (NOLOCK)",
        "SELECT * FROM OPENJSON(@j)", "SELECT a FROM t t1 JOIN u u1 ON t1.id = u1.id",
    ];

    /// <summary>Comandos que escrevem (ou mexem em sessão, transação, permissões, servidor) e alguns inofensivos.</summary>
    public static readonly string[] Commands =
    [
        "DELETE FROM t", "DELETE t", "UPDATE t SET a = 1", "INSERT INTO t VALUES (1)", "INSERT t VALUES (1)", "SELECT a INTO t2 FROM t",
        "MERGE t USING s ON 1 = 0 WHEN NOT MATCHED THEN INSERT (a) VALUES (1);", "TRUNCATE TABLE t", "DROP TABLE t",
        "CREATE TABLE x (a int)", "ALTER TABLE t ADD c int", "GRANT SELECT ON t TO u", "REVOKE SELECT ON t FROM u", "DENY SELECT ON t TO u",
        "EXEC sp_who", "EXECUTE ('SELECT 1')", "EXEC sp_executesql N'SELECT 1'", "DECLARE @x int", "SET NOCOUNT ON", "USE master",
        "BEGIN TRAN", "COMMIT", "ROLLBACK", "SAVE TRAN s", "WAITFOR DELAY '00:00:05'", "SHUTDOWN", "KILL 55", "RECONFIGURE",
        "BACKUP DATABASE d TO DISK = 'x'", "RESTORE DATABASE d FROM DISK = 'x'", "DBCC FREEPROCCACHE", "BULK INSERT t FROM 'f'",
        "UPDATE STATISTICS t", "DISABLE TRIGGER tr ON t", "ENABLE TRIGGER ALL ON DATABASE", "ADD SIGNATURE TO dbo.p BY CERTIFICATE c",
        "ADD SENSITIVITY CLASSIFICATION TO t.c WITH (LABEL = 'x')", "OPEN SYMMETRIC KEY k DECRYPTION BY CERTIFICATE c",
        "CLOSE ALL SYMMETRIC KEYS", "OPEN MASTER KEY DECRYPTION BY PASSWORD = 'x'", "SEND ON CONVERSATION @h MESSAGE TYPE m (0x01)",
        "END CONVERSATION @h", "MOVE CONVERSATION @h TO @g", "GET CONVERSATION GROUP @g FROM q", "RECEIVE * FROM q",
        "SELECT NEXT VALUE FOR dbo.seq", "CHECKPOINT", "SETUSER", "REVERT", "WRITETEXT t.c @p 'x'", "UPDATETEXT t.c @p 0 0 'x'",
        "OPEN c", "CLOSE c", "DEALLOCATE c", "FETCH NEXT FROM c",
        "PRINT 'x'", "THROW 50000, 'x', 1", "RAISERROR ('x', 16, 1)", "IF 1 = 1 SELECT 1", "WHILE 1 = 0 SELECT 1", "RETURN",
    ];

    /// <summary>Palavras de escrita isoladas (para os testes de "palavra em qualquer posição").</summary>
    public static readonly string[] WriteKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "INTO", "CREATE", "ALTER", "DROP", "GRANT", "REVOKE", "DENY", "EXEC",
        "EXECUTE", "DECLARE", "SET", "USE", "BEGIN", "COMMIT", "ROLLBACK", "SAVE", "WAITFOR", "SHUTDOWN", "KILL", "BACKUP",
        "RESTORE", "DBCC", "BULK", "DISABLE", "ENABLE", "ADD", "OPEN", "CLOSE", "DEALLOCATE", "CONVERSATION", "RECEIVE",
    ];

    /// <summary>Separadores entre comandos, inclusive nenhum (colado: <c>SELECT 1DROP TABLE t</c>).</summary>
    private static readonly string[] Separators =
        [" ", "\n", "\r", "\t", "\r\n", ";", "; ", " /* x */ ", "/**/", "", "--x\n", "--x\r", ")", "'x'", "[x]"];

    /// <summary>Lote: uma leitura seguida de 0 a 3 pares (separador, comando ou leitura), com maiúsculas sorteadas.</summary>
    public static Gen<string> Batches { get; } =
        from first in Gen.Elements(ReadStatements)
        from count in Gen.Choose(0, 3)
        from parts in Gen.Zip(Gen.Elements(Separators), Statement).ArrayOf(count)
        select first + string.Concat(parts.Select(p => p.Item1 + p.Item2));

    private static Gen<string> Statement =>
        Gen.OneOf(
            Gen.Elements(Commands),
            Gen.Elements(Commands).SelectMany(Hostile.RandomCase),
            Gen.Elements(ReadStatements));
}
