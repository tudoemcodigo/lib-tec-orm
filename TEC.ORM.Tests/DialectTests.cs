using Microsoft.EntityFrameworkCore;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Auditing;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Security;
using TEC.ORM.EntityFrameworkCore.SoftDelete;
using TEC.ORM.MySql.Dialect;
using TEC.ORM.Oracle.Dialect;
using TEC.ORM.PostgreSql.Dialect;
using TEC.ORM.Queries;
using TEC.ORM.SqlServer;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests;

public class DialectTests
{
    private static readonly IdempotencyInsertColumns SnakeCase = new("\"scope\"", "\"idempotency_key\"", "\"request_hash\"", "\"lock_id\"",
        "\"state\"", "\"locked_until\"", "\"retention_seconds\"", "\"expires_at\"", "\"created_at\"");

    [Test]
    public async Task PostgreSql_dialect_properties_and_sql_syntax()
    {
        ISqlDialect dialect = new PostgreSqlDialect();

        await Assert.That(dialect.ProviderName).IsEqualTo("PostgreSql");
        await Assert.That(dialect.DbSystemName).IsEqualTo("postgresql");
        await Assert.That(dialect.ParameterPrefix).IsEqualTo('@');
        await Assert.That(dialect.DelimitIdentifier("usuarios")).IsEqualTo("\"usuarios\"");
        await Assert.That(dialect.HealthCheckProbeQuery).IsEqualTo("SELECT 1");

        var idempotencySql = dialect.FormatAtomicIdempotencyInsert("tec_idempotencia");
        await Assert.That(idempotencySql).Contains("ON CONFLICT (\"Scope\", \"IdempotencyKey\") DO NOTHING");
    }

    [Test]
    public async Task MySql_dialect_properties_and_sql_syntax()
    {
        ISqlDialect dialect = new MySqlDialect();

        await Assert.That(dialect.ProviderName).IsEqualTo("MySql");
        await Assert.That(dialect.DbSystemName).IsEqualTo("mysql");
        await Assert.That(dialect.DelimitIdentifier("clientes")).IsEqualTo("`clientes`");
        await Assert.That(dialect.HealthCheckProbeQuery).IsEqualTo("SELECT 1");

        // INSERT simples: a chave repetida é violação de chave primária (tratada pelo store); sem IGNORE, que engoliria outros erros
        var idempotencySql = dialect.FormatAtomicIdempotencyInsert("tec_idempotencia");
        await Assert.That(idempotencySql).StartsWith("INSERT INTO tec_idempotencia");
        await Assert.That(idempotencySql).DoesNotContain("IGNORE");
    }

    [Test]
    public async Task Oracle_dialect_properties_and_sql_syntax()
    {
        ISqlDialect dialect = new OracleDialect();

        await Assert.That(dialect.ProviderName).IsEqualTo("Oracle");
        await Assert.That(dialect.DbSystemName).IsEqualTo("oracle.db");
        await Assert.That(dialect.ParameterPrefix).IsEqualTo(':');
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
        ISqlDialect dialect = new SqlServerDialect();

        await Assert.That(dialect.ProviderName).IsEqualTo("SqlServer");
        await Assert.That(dialect.DbSystemName).IsEqualTo("microsoft.sql_server");
        await Assert.That(dialect.DelimitIdentifier("pedidos")).IsEqualTo("[pedidos]");
        await Assert.That(dialect.DelimitIdentifier("a]b")).IsEqualTo("[a]]b]");
        await Assert.That(dialect.HealthCheckProbeQuery).IsEqualTo("SELECT 1");

        var idempotencySql = dialect.FormatAtomicIdempotencyInsert("TecIdempotencia");
        await Assert.That(idempotencySql).Contains("WHERE NOT EXISTS");
        await Assert.That(idempotencySql).Contains("WITH (UPDLOCK, HOLDLOCK)");
    }

    /// <summary>Convenção de nomes (ex.: snake_case) renomeia as colunas: o SQL da reserva usa os nomes do modelo.</summary>
    [Test]
    public async Task Idempotency_sql_uses_the_model_column_names()
    {
        foreach (ISqlDialect dialect in new ISqlDialect[] { new SqlServerDialect(), new PostgreSqlDialect(), new MySqlDialect(), new OracleDialect() })
        {
            string sql = dialect.FormatAtomicIdempotencyInsert("t", SnakeCase);
            await Assert.That(sql).Contains("\"idempotency_key\"");
            await Assert.That(sql).Contains("\"created_at\"");
            await Assert.That(sql).DoesNotContain("IdempotencyKey");
            await Assert.That(sql).Contains("{8}");
        }
    }

    [Test]
    public async Task Interpolated_query_parameters_follow_the_dialect_prefix()
    {
        int id = 42;
        string name = "Ana";
        var query = SqlQuery.Interpolated("clientes.por-id", $"SELECT Id FROM Clientes WHERE Id = {id} AND Nome = {name}");

        await Assert.That(query.Sql).IsEqualTo("SELECT Id FROM Clientes WHERE Id = @p0 AND Nome = @p1");
        await Assert.That(query.SqlFor('@')).IsEqualTo(query.Sql);
        await Assert.That(query.SqlFor(((ISqlDialect)new OracleDialect()).ParameterPrefix))
            .IsEqualTo("SELECT Id FROM Clientes WHERE Id = :p0 AND Nome = :p1");
        // O texto do Create é o informado (escrito com os marcadores do banco)
        var created = SqlQuery.Create("clientes.por-nome", "SELECT Id FROM Clientes WHERE Nome = :Nome", new { Nome = name });
        await Assert.That(created.SqlFor(':')).IsEqualTo("SELECT Id FROM Clientes WHERE Nome = :Nome");
        // Os marcadores do Oracle passam pelo validador do Oracle
        await Assert.That(new OracleReadOnlySqlValidator().Check(query.SqlFor(':'))).IsNull();
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

    // ---------- Tradução comum: DbUpdateException, concorrência e recusas do TEC.ORM ----------

    private static IOrmExceptionTranslator[] Translators() =>
        [new SqlServerExceptionTranslator(), new PostgreSqlExceptionTranslator(), new MySqlExceptionTranslator(), new OracleExceptionTranslator()];

    [Test]
    public async Task Exception_translators_handle_generic_and_timeout_exceptions()
    {
        foreach (var translator in Translators())
        {
            await Assert.That(translator.Translate(new TimeoutException("Database operation timed out")).Error.Code).IsEqualTo(OrmErrors.TimeoutCode);
            await Assert.That(translator.Translate(new InvalidOperationException("bug")).Error.Code).IsEqualTo(OrmErrors.FailureCode);
        }
    }

    [Test]
    public async Task Optimistic_concurrency_is_concurrency_in_every_dialect()
    {
        foreach (var translator in Translators())
        {
            var translation = translator.Translate(new DbUpdateConcurrencyException("linha alterada"));
            await Assert.That(translation.Error.Code).IsEqualTo(OrmErrors.ConcurrencyCode);
            await Assert.That(translation.IsInfrastructure).IsFalse();
        }
    }

    [Test]
    public async Task Orm_refusals_keep_their_error_in_every_dialect()
    {
        foreach (var translator in Translators())
        {
            await Assert.That(translator.Translate(new OrmAuditException(OrmErrors.AuditIdentityRequired())).Error.Code)
                .IsEqualTo(OrmErrors.AuditIdentityRequiredCode);
            await Assert.That(translator.Translate(new OrmSoftDeletePropagationException(OrmErrors.SoftDeletePropagationTooLarge(5))).Error.Code)
                .IsEqualTo(OrmErrors.TooManyResultsCode);
            var refused = translator.Translate(new OrmConnectionException(OrmErrors.InvalidConnectionSecret()));
            await Assert.That(refused.Error.Code).IsEqualTo(OrmErrors.InvalidConnectionSecretCode);
            await Assert.That(refused.IsInfrastructure).IsTrue();
        }
    }

    /// <summary>O EF Core embrulha o erro do banco em <c>DbUpdateException</c>: a chave duplicada tem de sair como conflito.</summary>
    [Test]
    public async Task Database_error_wrapped_in_DbUpdateException_is_found_in_every_dialect()
    {
        (IOrmExceptionTranslator Translator, Exception Duplicate, Exception ForeignKey)[] cases =
        [
            (new SqlServerExceptionTranslator(), SqlExceptionFactory.Create(2627), SqlExceptionFactory.Create(547)),
            (new PostgreSqlExceptionTranslator(), new Npgsql.PostgresException("dup", "ERROR", "ERROR", "23505"),
                new Npgsql.PostgresException("fk", "ERROR", "ERROR", "23503")),
            (new MySqlExceptionTranslator(), ProviderExceptionFactory.MySql(1062), ProviderExceptionFactory.MySql(1452)),
            (new OracleExceptionTranslator(), ProviderExceptionFactory.Oracle(1), ProviderExceptionFactory.Oracle(2291))
        ];

        foreach (var (translator, duplicate, foreignKey) in cases)
        {
            var wrapped = new DbUpdateException("falha ao salvar", new InvalidOperationException("intermediária", duplicate));
            await Assert.That(translator.Translate(wrapped).Error.Code).IsEqualTo(OrmErrors.ConflictCode);
            // Só a chave única/primária conta como "já existe" na idempotência; chave estrangeira não
            await Assert.That(translator.IsUniqueViolation(wrapped)).IsTrue();
            await Assert.That(translator.IsUniqueViolation(new DbUpdateException("fk", foreignKey))).IsFalse();
            await Assert.That(translator.Translate(new DbUpdateException("fk", foreignKey)).Error.Code).IsEqualTo(OrmErrors.ConflictCode);
            await Assert.That(translator.IsUniqueViolation(new InvalidOperationException("outra"))).IsFalse();
        }
    }

    [Test]
    public async Task Driver_exception_factories_produce_the_requested_number()
    {
        await Assert.That(ProviderExceptionFactory.MySql(1062).Number).IsEqualTo(1062);
        await Assert.That(ProviderExceptionFactory.Oracle(2291).Number).IsEqualTo(2291);
    }

    [Test]
    public async Task Transient_and_connection_failures_are_recognized_inside_wrappers()
    {
        var pg = new PostgreSqlExceptionTranslator();
        var deadlock = new DbUpdateException("x", new Npgsql.PostgresException("deadlock", "ERROR", "ERROR", "40P01"));
        await Assert.That(pg.IsTransient(deadlock)).IsTrue();
        await Assert.That(pg.Translate(deadlock).Error.Code).IsEqualTo(OrmErrors.ConcurrencyCode);
        await Assert.That(pg.IsConnectionFailure(new Npgsql.PostgresException("x", "FATAL", "FATAL", "57P01"))).IsTrue();

        var my = new MySqlExceptionTranslator();
        await Assert.That(my.IsTransient(new InvalidOperationException("x", ProviderExceptionFactory.MySql(1213)))).IsTrue();
        await Assert.That(my.Translate(ProviderExceptionFactory.MySql(2013)).Error.Code).IsEqualTo(OrmErrors.ConnectionUnavailableCode);

        var oracle = new OracleExceptionTranslator();
        await Assert.That(oracle.Translate(ProviderExceptionFactory.Oracle(1013)).Error.Code).IsEqualTo(OrmErrors.TimeoutCode);
        await Assert.That(oracle.IsConnectionFailure(new DbUpdateException("x", ProviderExceptionFactory.Oracle(12541)))).IsTrue();
        await Assert.That(oracle.Translate(ProviderExceptionFactory.Oracle(1)).ErrorCode).IsEqualTo("ORA-00001");
    }

    [Test]
    public async Task Unknown_database_exception_is_failure_without_its_message()
    {
        var translation = new PostgreSqlExceptionTranslator().Translate(new FakeDbException("senha=segredo"));

        await Assert.That(translation.Error.Code).IsEqualTo(OrmErrors.FailureCode);
        await Assert.That(translation.Error.Message).DoesNotContain("segredo");
    }

    [Test]
    public async Task Relaxed_transport_is_flagged_only_when_the_secret_uses_it()
    {
        var relaxed = new PostgreSqlDialect().SanitizeAndValidateConnectionString(
            "Host=h;Database=d;Username=u;Password=segredo;SSL Mode=Require", false, true, "app", 30);
        var strict = new PostgreSqlDialect().SanitizeAndValidateConnectionString(
            "Host=h;Database=d;Username=u;Password=segredo;SSL Mode=VerifyFull", false, true, "app", 30);

        await Assert.That(relaxed.Value.ToString()).IsEqualTo("***");
        await Assert.That(relaxed.Value.RelaxedTransportSecurity).IsTrue();
        // Opção ligada, mas o segredo valida o certificado: sem o aviso 3103
        await Assert.That(strict.Value.RelaxedTransportSecurity).IsFalse();
    }
}
