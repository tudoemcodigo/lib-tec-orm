using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.MySql.Dialect;
using TEC.ORM.Oracle.Dialect;
using TEC.ORM.PostgreSql.Dialect;
using TEC.ORM.SqlServer;

namespace TEC.ORM.Tests;

public class DialectTests
{
    [Test]
    public async Task PostgreSql_dialect_properties_and_sql_syntax()
    {
        var dialect = new PostgreSqlDialect();

        await Assert.That(dialect.ProviderName).IsEqualTo("PostgreSql");
        await Assert.That(dialect.DelimitIdentifier("usuarios")).IsEqualTo("\"usuarios\"");
        await Assert.That(dialect.HealthCheckProbeQuery).IsEqualTo("SELECT 1");

        var idempotencySql = dialect.FormatAtomicIdempotencyInsert("tec_idempotencia");
        await Assert.That(idempotencySql).Contains("ON CONFLICT");
        await Assert.That(idempotencySql).Contains("DO NOTHING");
    }

    [Test]
    public async Task MySql_dialect_properties_and_sql_syntax()
    {
        var dialect = new MySqlDialect();

        await Assert.That(dialect.ProviderName).IsEqualTo("MySql");
        await Assert.That(dialect.DelimitIdentifier("clientes")).IsEqualTo("`clientes`");
        await Assert.That(dialect.HealthCheckProbeQuery).IsEqualTo("SELECT 1");

        var idempotencySql = dialect.FormatAtomicIdempotencyInsert("tec_idempotencia");
        await Assert.That(idempotencySql).Contains("INSERT IGNORE INTO");
    }

    [Test]
    public async Task Oracle_dialect_properties_and_sql_syntax()
    {
        var dialect = new OracleDialect();

        await Assert.That(dialect.ProviderName).IsEqualTo("Oracle");
        await Assert.That(dialect.DelimitIdentifier("contas")).IsEqualTo("\"contas\"");
        await Assert.That(dialect.HealthCheckProbeQuery).IsEqualTo("SELECT 1 FROM DUAL");

        var idempotencySql = dialect.FormatAtomicIdempotencyInsert("TEC_IDEMPOTENCIA");
        await Assert.That(idempotencySql).Contains("MERGE INTO");
        await Assert.That(idempotencySql).Contains("FROM DUAL");
        await Assert.That(idempotencySql).Contains("WHEN NOT MATCHED THEN");
    }

    [Test]
    public async Task SqlServer_dialect_properties_and_sql_syntax()
    {
        var dialect = new SqlServerDialect();

        await Assert.That(dialect.ProviderName).IsEqualTo("SqlServer");
        await Assert.That(dialect.DelimitIdentifier("pedidos")).IsEqualTo("[pedidos]");
        await Assert.That(dialect.HealthCheckProbeQuery).IsEqualTo("SELECT 1");

        var idempotencySql = dialect.FormatAtomicIdempotencyInsert("TecIdempotencia");
        await Assert.That(idempotencySql).Contains("WHERE NOT EXISTS");
    }

    [Test]
    public async Task ReadOnly_guards_prevent_mutations_across_dialects()
    {
        var pgGuard = new PostgreSqlReadOnlySqlValidator();
        var myGuard = new MySqlReadOnlySqlValidator();
        var oraGuard = new OracleReadOnlySqlValidator();

        string[] mutations =
        [
            "DELETE FROM Clientes WHERE 1=1",
            "UPDATE Contas SET Saldo = 0",
            "INSERT INTO Logs VALUES (1)",
            "DROP TABLE Usuarios",
            "ALTER TABLE Pedidos ADD Coluna INT"
        ];

        foreach (var sql in mutations)
        {
            await Assert.That(pgGuard.Check(sql)).IsNotNull();
            await Assert.That(myGuard.Check(sql)).IsNotNull();
            await Assert.That(oraGuard.Check(sql)).IsNotNull();
        }

        string safeSql = "SELECT Id, Nome FROM Clientes WHERE Ativo = 1";
        await Assert.That(pgGuard.Check(safeSql)).IsNull();
        await Assert.That(myGuard.Check(safeSql)).IsNull();
        await Assert.That(oraGuard.Check(safeSql)).IsNull();
    }

    [Test]
    public async Task Exception_translators_handle_generic_and_timeout_exceptions()
    {
        var pgTranslator = new PostgreSqlExceptionTranslator();
        var myTranslator = new MySqlExceptionTranslator();
        var oraTranslator = new OracleExceptionTranslator();

        var timeoutEx = new TimeoutException("Database operation timed out");

        var pgTr = pgTranslator.Translate(timeoutEx);
        var myTr = myTranslator.Translate(timeoutEx);
        var oraTr = oraTranslator.Translate(timeoutEx);

        await Assert.That(pgTr.Error.Code).IsEqualTo(OrmErrors.Timeout().Code);
        await Assert.That(myTr.Error.Code).IsEqualTo(OrmErrors.Timeout().Code);
        await Assert.That(oraTr.Error.Code).IsEqualTo(OrmErrors.Timeout().Code);
    }
}
