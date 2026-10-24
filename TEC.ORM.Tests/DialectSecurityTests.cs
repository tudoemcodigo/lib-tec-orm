using TEC.ORM.MySql.Dialect;
using TEC.ORM.Oracle.Dialect;
using TEC.ORM.PostgreSql.Dialect;

namespace TEC.ORM.Tests;

/// <summary>Contornos da verificação de somente leitura e política de criptografia dos dialetos não SQL Server.</summary>
public class DialectSecurityTests
{
    // Cada caso esconde um segundo comando em algo que o validador tratava como literal ou comentário
    [Test]
    [Arguments("SELECT '\\'; DELETE FROM t; --'")]
    [Arguments("SELECT $a$'$a$; DELETE FROM t; --'")]
    [Arguments("SELECT $$'$$; DELETE FROM t; --'")]
    public async Task PostgreSql_guard_rejects_hidden_statements(string sql)
    {
        await Assert.That(new PostgreSqlReadOnlySqlValidator().Check(sql)).IsNotNull();
    }

    [Test]
    [Arguments("SELECT 'a\\' || 'b' FROM t")]
    [Arguments("SELECT $$ DELETE $$ FROM t")]
    [Arguments("SELECT E'a\\'b' FROM t")]
    [Arguments("SELECT \"Id\" FROM t WHERE Id = $1")]
    public async Task PostgreSql_guard_accepts_legitimate_literals(string sql)
    {
        await Assert.That(new PostgreSqlReadOnlySqlValidator().Check(sql)).IsNull();
    }

    [Test]
    [Arguments("SELECT 1--1; DELETE FROM t")]
    [Arguments("SELECT 1 /*! ; DELETE FROM t */")]
    [Arguments("SELECT 1 /*M! ; DELETE FROM t */")]
    [Arguments("SELECT 1 /* /* */ ; DELETE FROM t; -- */")]
    [Arguments("SELECT `a\\` FROM t; DELETE FROM t")]
    public async Task MySql_guard_rejects_hidden_statements(string sql)
    {
        await Assert.That(new MySqlReadOnlySqlValidator().Check(sql)).IsNotNull();
    }

    [Test]
    [Arguments("SELECT 1 -- comentário\nFROM t")]
    [Arguments("SELECT 'a\\'b' FROM t")]
    [Arguments("SELECT `Id` FROM t")]
    public async Task MySql_guard_accepts_legitimate_sql(string sql)
    {
        await Assert.That(new MySqlReadOnlySqlValidator().Check(sql)).IsNull();
    }

    [Test]
    [Arguments("SELECT 'a\\'; DELETE FROM t; --' FROM dual")]
    [Arguments("SELECT 1 /* /* */ ; DELETE FROM t; -- */ FROM dual")]
    [Arguments("SELECT q'[']'; DELETE FROM t; --' FROM dual")]
    public async Task Oracle_guard_rejects_hidden_statements(string sql)
    {
        await Assert.That(new OracleReadOnlySqlValidator().Check(sql)).IsNotNull();
    }

    [Test]
    [Arguments("SELECT q'[DELETE FROM t]' FROM dual")]
    [Arguments("SELECT 'it''s' FROM dual")]
    public async Task Oracle_guard_accepts_legitimate_sql(string sql)
    {
        await Assert.That(new OracleReadOnlySqlValidator().Check(sql)).IsNull();
    }

    [Test]
    [Arguments("Host=h;Database=d;Username=u;Password=p;SSL Mode=Prefer", false, false)]
    [Arguments("Host=h;Database=d;Username=u;Password=p;SSL Mode=Allow", false, false)]
    [Arguments("Host=h;Database=d;Username=u;Password=p;SSL Mode=Disable", true, false)]
    [Arguments("Host=h;Database=d;Username=u;Password=p;SSL Mode=Require", false, false)]
    [Arguments("Host=h;Database=d;Username=u;Password=p;SSL Mode=Require", true, true)]
    [Arguments("Host=h;Database=d;Username=u;Password=p;SSL Mode=VerifyFull", false, true)]
    public async Task PostgreSql_dialect_enforces_encryption(string connection, bool allowTrust, bool accepted)
    {
        var result = new PostgreSqlDialect().SanitizeAndValidateConnectionString(connection, false, allowTrust, "app", 30);
        await Assert.That(result.IsSuccess).IsEqualTo(accepted);
    }

    [Test]
    [Arguments("Server=h;Database=d;User Id=u;Password=p;SSL Mode=Preferred", false, false)]
    [Arguments("Server=h;Database=d;User Id=u;Password=p;SSL Mode=Required", false, false)]
    [Arguments("Server=h;Database=d;User Id=u;Password=p;SSL Mode=None", true, false)]
    [Arguments("Server=h;Database=d;User Id=u;Password=p;SSL Mode=Required", true, true)]
    [Arguments("Server=h;Database=d;User Id=u;Password=p;SSL Mode=VerifyCA", false, true)]
    public async Task MySql_dialect_enforces_encryption(string connection, bool allowTrust, bool accepted)
    {
        var result = new MySqlDialect().SanitizeAndValidateConnectionString(connection, false, allowTrust, "app", 30);
        await Assert.That(result.IsSuccess).IsEqualTo(accepted);
    }

    [Test]
    [Arguments("Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=s)));User Id=u;Password=p", false, false)]
    [Arguments("Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=s)));User Id=u;Password=p", true, true)]
    [Arguments("Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=h)(PORT=2484))(CONNECT_DATA=(SERVICE_NAME=s)));User Id=u;Password=p", false, true)]
    public async Task Oracle_dialect_rejects_plain_tcp(string connection, bool allowTrust, bool accepted)
    {
        var result = new OracleDialect().SanitizeAndValidateConnectionString(connection, false, allowTrust, "app", 30);
        await Assert.That(result.IsSuccess).IsEqualTo(accepted);
    }

    [Test]
    [Arguments("SELECT pg_sleep(5) FROM t")]
    [Arguments("SELECT pg_read_file('foo') FROM t")]
    [Arguments("SELECT lo_import('/etc/passwd') FROM t")]
    [Arguments("SELECT dblink_exec('conn', 'drop table t') FROM t")]
    public async Task PostgreSql_guard_rejects_dangerous_functions(string sql)
    {
        await Assert.That(new PostgreSqlReadOnlySqlValidator().Check(sql)).IsNotNull();
    }

    [Test]
    [Arguments("SELECT LOAD_FILE('/etc/passwd') FROM t")]
    [Arguments("SELECT SLEEP(5) FROM t")]
    public async Task MySql_guard_rejects_dangerous_functions(string sql)
    {
        await Assert.That(new MySqlReadOnlySqlValidator().Check(sql)).IsNotNull();
    }

    [Test]
    [Arguments("SELECT UTL_HTTP.REQUEST('http://evil.com') FROM dual")]
    [Arguments("SELECT UTL_FILE.FOPEN('dir', 'file', 'r') FROM dual")]
    public async Task Oracle_guard_rejects_dangerous_packages(string sql)
    {
        await Assert.That(new OracleReadOnlySqlValidator().Check(sql)).IsNotNull();
    }

    [Test]
    public async Task Read_only_connections_opt_out_of_ambient_transactions()
    {
        var pg = new PostgreSqlDialect().SanitizeAndValidateConnectionString(
            "Host=h;Database=d;Username=u;Password=p;SSL Mode=VerifyFull", true, false, "app", 30);
        var my = new MySqlDialect().SanitizeAndValidateConnectionString(
            "Server=h;Database=d;User Id=u;Password=p;SSL Mode=VerifyCA", true, false, "app", 30);

        await Assert.That(pg.Value.Reveal()).Contains("Enlist=False");
        await Assert.That(my.Value.Reveal().ToLowerInvariant()).Contains("auto enlist=false");
    }
}
