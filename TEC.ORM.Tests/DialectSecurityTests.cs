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
    [Arguments("SELECT \"set\", \"do\" FROM t")]
    [Arguments("SELECT pg_size_pretty(pg_database_size(current_database()))")]
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
    [Arguments("SELECT o.Id, o.Total FROM app.Orders o WHERE o.Id = :p0")]
    [Arguments("SELECT 'DBMS_LOCK.SLEEP' AS texto FROM dual")]
    public async Task Oracle_guard_accepts_legitimate_sql(string sql)
    {
        await Assert.That(new OracleReadOnlySqlValidator().Check(sql)).IsNull();
    }

    // ---------- Política de criptografia ----------

    [Test]
    [Arguments("Host=h;Database=d;Username=u;Password=p;SSL Mode=Prefer", false, false)]
    [Arguments("Host=h;Database=d;Username=u;Password=p;SSL Mode=Allow", false, false)]
    [Arguments("Host=h;Database=d;Username=u;Password=p;SSL Mode=Disable", true, false)]
    [Arguments("Host=h;Database=d;Username=u;Password=p;SSL Mode=Prefer", true, false)]
    [Arguments("Host=h;Database=d;Username=u;Password=p;SSL Mode=Require", false, false)]
    [Arguments("Host=h;Database=d;Username=u;Password=p;SSL Mode=Require", true, true)]
    [Arguments("Host=h;Database=d;Username=u;Password=p;SSL Mode=VerifyFull", false, true)]
    [Arguments("Host=h;Database=d;Username=u;Password=p;SSL Mode=VerifyCA", false, true)]
    public async Task PostgreSql_dialect_enforces_encryption(string connection, bool allowTrust, bool accepted)
    {
        var result = new PostgreSqlDialect().SanitizeAndValidateConnectionString(connection, false, allowTrust, "app", 30);
        await Assert.That(result.IsSuccess).IsEqualTo(accepted);
    }

    [Test]
    public async Task PostgreSql_dialect_imposes_the_policy_options()
    {
        var result = new PostgreSqlDialect().SanitizeAndValidateConnectionString(
            "Host=h;Database=d;Username=u;Password=p;SSL Mode=VerifyFull;Persist Security Info=true;Include Error Detail=true", true, false, "Vendas", 17);
        var applied = new Npgsql.NpgsqlConnectionStringBuilder(result.Value.Reveal());

        await Assert.That(applied.PersistSecurityInfo).IsFalse();
        await Assert.That(applied.IncludeErrorDetail).IsFalse();
        await Assert.That(applied.ApplicationName).IsEqualTo("Vendas");
        await Assert.That(applied.CommandTimeout).IsEqualTo(17);
        await Assert.That(applied.Enlist).IsFalse();
    }

    [Test]
    [Arguments("Server=h;Database=d;User Id=u;Password=p;SSL Mode=Preferred", false, false)]
    [Arguments("Server=h;Database=d;User Id=u;Password=p;SSL Mode=Required", false, false)]
    [Arguments("Server=h;Database=d;User Id=u;Password=p;SSL Mode=None", true, false)]
    [Arguments("Server=h;Database=d;User Id=u;Password=p;SSL Mode=Disabled", true, false)]
    [Arguments("Server=h;Database=d;User Id=u;Password=p", false, false)]
    [Arguments("Server=h;Database=d;User Id=u;Password=p;SSL Mode=Required", true, true)]
    [Arguments("Server=h;Database=d;User Id=u;Password=p;SSL Mode=VerifyCA", false, true)]
    [Arguments("Server=h;Database=d;User Id=u;Password=p;SSL Mode=VerifyFull", false, true)]
    public async Task MySql_dialect_enforces_encryption(string connection, bool allowTrust, bool accepted)
    {
        var result = new MySqlDialect().SanitizeAndValidateConnectionString(connection, false, allowTrust, "app", 30);
        await Assert.That(result.IsSuccess).IsEqualTo(accepted);
    }

    /// <summary>
    /// A mesma string vai para o EF Core (MySql.EntityFrameworkCore) e para o Dapper: o driver dos dois (MySql.Data) tem de
    /// aceitá-la. Antes, a string montada pelo MySqlConnector levava "Application Name", que o MySql.Data recusa.
    /// </summary>
    [Test]
    public async Task MySql_dialect_output_is_accepted_by_the_ef_core_driver()
    {
        var result = new MySqlDialect().SanitizeAndValidateConnectionString(
            "Server=h;Database=d;User Id=u;Password=p;SSL Mode=VerifyFull;Allow Load Local Infile=true", true, false, "Vendas", 17);
        await using var connection = new global::MySql.Data.MySqlClient.MySqlConnection();

        connection.ConnectionString = result.Value.Reveal();

        var applied = new global::MySql.Data.MySqlClient.MySqlConnectionStringBuilder(result.Value.Reveal());
        await Assert.That(applied.AllowLoadLocalInfile).IsFalse();
        await Assert.That(applied.PersistSecurityInfo).IsFalse();
        await Assert.That(applied.AutoEnlist).IsFalse();
        await Assert.That(applied.ConnectionReset).IsTrue();
        await Assert.That(applied.DefaultCommandTimeout).IsEqualTo(17u);
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

    /// <summary>Formas que escapavam da verificação do PROTOCOL=TCP: EZConnect, espaços/tabulações/quebras e endereços mistos.</summary>
    [Test]
    [Arguments("db.interno:1521/vendas")]
    [Arguments("//db.interno:1521/vendas")]
    [Arguments("db.interno/vendas")]
    [Arguments("tcp://db.interno:1521/vendas")]
    [Arguments("(DESCRIPTION=(ADDRESS=(PROTOCOL = TCP)(HOST=h)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=s)))")]
    [Arguments("(DESCRIPTION=(ADDRESS=(PROTOCOL\t=\tTCP)(HOST=h)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=s)))")]
    [Arguments("(DESCRIPTION=\n (ADDRESS=\n  (PROTOCOL=\r\nTCP)\n  (HOST=h)(PORT=1521))\n (CONNECT_DATA=(SERVICE_NAME=s)))")]
    [Arguments("(DESCRIPTION=(ADDRESS=(PROTOCOL=tcp)(HOST=h)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=s)))")]
    [Arguments("(DESCRIPTION=(ADDRESS=(HOST=h)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=s)))")]
    [Arguments("(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCPS)(HOST=a)(PORT=2484))(ADDRESS=(PROTOCOL=TCP)(HOST=b)(PORT=1521)))(CONNECT_DATA=(SERVICE_NAME=s)))")]
    [Arguments("(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCPS)(HOST=a)(PORT=2484))(ADDRESS=(HOST=b)(PORT=1521)))(CONNECT_DATA=(SERVICE_NAME=s)))")]
    [Arguments("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=h)(PORT=2484))(CONNECT_DATA=(SERVICE_NAME=s))(SECURITY=(SSL_SERVER_DN_MATCH=NO)))")]
    [Arguments("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=h)(PORT=2484))(CONNECT_DATA=(SERVICE_NAME=s))(SECURITY=(ssl_server_dn_match = false)))")]
    public async Task Oracle_plain_transport_variants_are_rejected_fail_closed(string dataSource)
    {
        string connection = $"Data Source={Quote(dataSource)};User Id=u;Password=p";

        var result = new OracleDialect().SanitizeAndValidateConnectionString(connection, false, false, "app", 30);
        var allowed = new OracleDialect().SanitizeAndValidateConnectionString(connection, false, true, "app", 30);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error!.Message).DoesNotContain("p;");
        // Só desenvolvimento: com AllowTrustServerCertificate passa, marcado como transporte relaxado
        await Assert.That(allowed.IsSuccess).IsTrue();
        await Assert.That(allowed.Value.RelaxedTransportSecurity).IsTrue();
    }

    [Test]
    [Arguments("tcps://db.interno:2484/vendas")]
    [Arguments("TCPS://db.interno:2484/vendas?wallet_location=/opt/wallet")]
    [Arguments("(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCPS)(HOST=a)(PORT=2484))(ADDRESS=(PROTOCOL=TCPS)(HOST=b)(PORT=2484)))(CONNECT_DATA=(SERVICE_NAME=s)))")]
    [Arguments("(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=h)(PORT=2484))(CONNECT_DATA=(SERVICE_NAME=s))(SECURITY=(SSL_SERVER_DN_MATCH=YES)))")]
    [Arguments("vendas_high")]
    public async Task Oracle_tls_or_uninspectable_alias_is_accepted(string dataSource)
    {
        var result = new OracleDialect().SanitizeAndValidateConnectionString(
            $"Data Source={Quote(dataSource)};User Id=u;Password=p", true, false, "app", 30);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.RelaxedTransportSecurity).IsFalse();
        var applied = new global::Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder(result.Value.Reveal());
        await Assert.That(applied.Enlist.ToLowerInvariant()).IsEqualTo("false");
        await Assert.That(applied.PersistSecurityInfo).IsFalse();
    }

    [Test]
    public async Task Oracle_command_timeout_does_not_change_the_connect_timeout()
    {
        var result = new OracleDialect().SanitizeAndValidateConnectionString(
            "Data Source=tcps://h:2484/s;User Id=u;Password=p;Connection Timeout=7", false, false, "app", 120);

        await Assert.That(new global::Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder(result.Value.Reveal()).ConnectionTimeout).IsEqualTo(7);
    }

    /// <summary>Valor com <c>;</c>, <c>=</c> ou espaço precisa de aspas na string de conexão.</summary>
    private static string Quote(string value) => "\"" + value + "\"";

    // ---------- Funções e pacotes com efeito colateral ----------

    [Test]
    [Arguments("SELECT pg_sleep(5) FROM t")]
    [Arguments("SELECT pg_read_file('foo') FROM t")]
    [Arguments("SELECT lo_import('/etc/passwd') FROM t")]
    [Arguments("SELECT dblink_exec('conn', 'drop table t') FROM t")]
    [Arguments("SELECT set_config('app.tenant', 'x', false)")]
    [Arguments("SELECT nextval('pedidos_id_seq')")]
    [Arguments("SELECT setval('pedidos_id_seq', 1)")]
    [Arguments("SELECT pg_advisory_lock(1)")]
    [Arguments("SELECT pg_advisory_xact_lock_shared(1)")]
    [Arguments("SELECT pg_try_advisory_lock(1)")]
    [Arguments("SELECT pg_terminate_backend(123)")]
    [Arguments("SELECT pg_cancel_backend(123)")]
    [Arguments("SELECT pg_catalog.pg_sleep(5)")]
    [Arguments("SELECT \"pg_sleep\"(5)")]
    [Arguments("SELECT pg_catalog.\"set_config\"('a', 'b', false)")]
    [Arguments("SELECT U&\"\\0070g_sleep\"(5)")]
    [Arguments("SELECT * FROM t FOR SHARE")]
    [Arguments("SELECT * FROM t FOR KEY SHARE")]
    [Arguments("SELECT * FROM t FOR NO KEY UPDATE")]
    public async Task PostgreSql_guard_rejects_dangerous_functions(string sql)
    {
        await Assert.That(new PostgreSqlReadOnlySqlValidator().Check(sql)).IsNotNull();
    }

    [Test]
    [Arguments("SELECT LOAD_FILE('/etc/passwd') FROM t")]
    [Arguments("SELECT SLEEP(5) FROM t")]
    [Arguments("SELECT GET_LOCK('chave', 10)")]
    [Arguments("SELECT RELEASE_LOCK('chave')")]
    [Arguments("SELECT * FROM t FOR SHARE")]
    [Arguments("SELECT * FROM t LOCK IN SHARE MODE")]
    [Arguments("SELECT * FROM t FOR UPDATE")]
    [Arguments("SELECT `SLEEP`(5)")]
    [Arguments("SELECT 1; REPLACE INTO t VALUES (1)")]
    [Arguments("WITH x AS (SELECT 1) REPLACE INTO t SELECT * FROM x")]
    [Arguments("SELECT 1 INTO OUTFILE '/tmp/x'")]
    public async Task MySql_guard_rejects_dangerous_functions(string sql)
    {
        await Assert.That(new MySqlReadOnlySqlValidator().Check(sql)).IsNotNull();
    }

    /// <summary>REPLACE(), TRUNCATE() e INSERT() são funções de texto/número do MySQL: só como comando são recusadas.</summary>
    [Test]
    [Arguments("SELECT REPLACE(Nome, 'a', 'b') FROM t")]
    [Arguments("SELECT TRUNCATE(Valor, 2) FROM t")]
    [Arguments("SELECT INSERT(Nome, 1, 2, 'xy') FROM t")]
    [Arguments("SELECT REPLACE (Nome, 'a', 'b') FROM t")]
    [Arguments("SELECT replace(Nome, 'a', 'b') FROM t")]
    public async Task MySql_guard_accepts_text_and_number_functions(string sql)
    {
        await Assert.That(new MySqlReadOnlySqlValidator().Check(sql)).IsNull();
    }

    [Test]
    [Arguments("SELECT UTL_HTTP.REQUEST('http://evil.com') FROM dual")]
    [Arguments("SELECT UTL_FILE.FOPEN('dir', 'file', 'r') FROM dual")]
    [Arguments("SELECT SYS.DBMS_LOCK.SLEEP(5) FROM dual")]
    [Arguments("SELECT SYS . DBMS_LOCK . SLEEP(5) FROM dual")]
    [Arguments("SELECT sys.utl_http.request('http://evil.com') FROM dual")]
    [Arguments("SELECT \"SYS\".\"DBMS_LOCK\".SLEEP(5) FROM dual")]
    [Arguments("SELECT SYS.\"UTL_INADDR\".GET_HOST_ADDRESS('evil.com') FROM dual")]
    [Arguments("SELECT HTTPURITYPE('http://evil.com').GETCLOB() FROM dual")]
    [Arguments("SELECT SYS.HTTPURITYPE('http://evil.com').GETCLOB() FROM dual")]
    [Arguments("SELECT * FROM t FOR UPDATE")]
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
        var oracle = new OracleDialect().SanitizeAndValidateConnectionString(
            "Data Source=tcps://h:2484/s;User Id=u;Password=p", true, false, "app", 30);

        await Assert.That(pg.Value.Reveal()).Contains("Enlist=False");
        await Assert.That(my.Value.Reveal().ToLowerInvariant()).Contains("autoenlist=false");
        await Assert.That(oracle.Value.Reveal().ToLowerInvariant()).Contains("enlist=false");
    }
}
