using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TEC.Core.Common.Results;
using TEC.Core.Domain;
using TEC.Core.Exceptions;
using TEC.ORM.Auditing;
using TEC.ORM.Common;
using TEC.ORM.Entities;
using TEC.ORM.EntityFrameworkCore;
using TEC.ORM.EntityFrameworkCore.Auditing;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Diagnostics;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Idempotency;
using TEC.ORM.EntityFrameworkCore.Security;
using TEC.ORM.EntityFrameworkCore.SoftDelete;
using TEC.ORM.MySql.Dialect;
using TEC.ORM.Oracle.Dialect;
using TEC.ORM.PostgreSql.Dialect;
using TEC.ORM.Queries;
using TEC.ORM.SqlServer;
using TEC.ORM.SqlServer.DependencyInjection;
using TEC.ORM.Tests.Fakes;
using TEC.Vault.DependencyInjection;
using TEC.Vault.InMemory;

namespace TEC.ORM.Tests;

/// <summary>Entidade auditada com texto e binário, para o limite de tamanho da trilha.</summary>
[Auditable]
public sealed class AuditedBlob : Entity<int>
{
    public string Text { get; set; } = string.Empty;

    public byte[] Data { get; set; } = [];
}

/// <summary>Contexto da trilha sem nome de tabela explícito (vale o <c>OrmOptions.AuditLogTableName</c>).</summary>
public sealed class DefaultAuditTableContext(DbContextOptions<DefaultAuditTableContext> options) : DbContext(options)
{
    public DbSet<AuditedBlob> Blobs => Set<AuditedBlob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddTecOrmHistoricalAudit();
}

/// <summary>Contexto da trilha com nome de tabela explícito (vence a opção).</summary>
public sealed class ExplicitAuditTableContext(DbContextOptions<ExplicitAuditTableContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddTecOrmHistoricalAudit("Explicita", "auditoria");
}

/// <summary>Contexto com exclusão lógica, idempotência e trilha histórica, para o SQL de modelo de cada banco.</summary>
public sealed class ModelSqlContext(DbContextOptions<ModelSqlContext> options) : OrmDbContext(options)
{
    public DbSet<SoftDeleteCategory> Categories => Set<SoftDeleteCategory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SoftDeleteCategory>().HasSoftDelete();
        modelBuilder.AddTecIdempotency();
        modelBuilder.AddTecOrmHistoricalAudit();
    }
}

/// <summary>Categoria simples (chave texto) com exclusão lógica.</summary>
public sealed class SoftDeleteCategory : Entity<string>
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Peças comuns do <c>TEC.ORM.EntityFrameworkCore</c> usadas por todos os provedores: abertura de conexão (cancelamento e
/// falhas), transação somente leitura de cada dialeto, falha fechada sem verificação de leitura, limite de tamanho da trilha,
/// nome da tabela da trilha, collation da idempotência e filtro do índice da exclusão lógica por banco.
/// </summary>
public class CommonPipelineTests
{
    private const string SqlServerSecret = "Server=tcp:db,1433;Database=app;User ID=u;Password=p;Encrypt=True";

    // ---------- Abertura da conexão ----------

    private static (OrmConnectionSecurity Security, RecordingConnection Connection, CapturingLoggerProvider Logs) Security()
    {
        var connection = new RecordingConnection(ConnectionState.Closed);
        var options = TestOrm.Options(o => o.CircuitBreaker.Enabled = false);
        options.Dialect = new ScriptedDialect(connection);
        var logs = new CapturingLoggerProvider();
        var security = new OrmConnectionSecurity(TestOrm.Secrets((TestOrm.SecretName, SqlServerSecret)), options,
            logs.CreateLogger<OrmConnectionSecurity>());
        return (security, connection, logs);
    }

    [Test]
    public async Task Caller_cancellation_during_open_is_rethrown_not_converted_into_unavailable()
    {
        var (security, connection, logs) = Security();
        using var cancel = new CancellationTokenSource();
        connection.OnOpen = _ =>
        {
            cancel.Cancel();
            throw new FakeDbException("falha de rede depois do cancelamento");
        };

        await Assert.That(async () => await security.OpenConnectionAsync(OrmConnectionKind.ReadOnly, cancel.Token))
            .Throws<OperationCanceledException>();
        // Nada de "falha ao abrir a conexão" no log: cancelamento não é indisponibilidade do banco
        await Assert.That(logs.Entries.Any(e => e.EventId.Id == 3102)).IsFalse();
    }

    [Test]
    public async Task Database_failure_on_open_is_unavailable_without_the_message()
    {
        var (security, connection, logs) = Security();
        connection.OnOpen = _ => throw new FakeDbException("servidor db.interno recusou usuario-secreto");

        var result = await security.OpenConnectionAsync(OrmConnectionKind.ReadWrite, CancellationToken.None);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConnectionUnavailableCode);
        await Assert.That(logs.AllText).DoesNotContain("usuario-secreto");
        await Assert.That(logs.Entries.Any(e => e.EventId.Id == 3102)).IsTrue();
    }

    [Test]
    public async Task Programming_error_on_open_is_not_disguised_as_unavailable()
    {
        var (security, connection, _) = Security();
        connection.OnOpen = _ => throw new InvalidOperationException("estado inválido da conexão");

        await Assert.That(async () => await security.OpenConnectionAsync(OrmConnectionKind.ReadWrite, CancellationToken.None))
            .Throws<InvalidOperationException>();
    }

    // ---------- Transação somente leitura das leituras complexas ----------

    [Test]
    [Arguments("PostgreSql")]
    [Arguments("Oracle")]
    public async Task Read_only_transaction_is_set_as_the_first_statement_and_rolled_back(string provider)
    {
        ISqlDialect dialect = provider == "Oracle" ? new OracleDialect() : new PostgreSqlDialect();
        var connection = new RecordingConnection();

        var scope = await dialect.BeginReadOnlyTransactionAsync(connection, CancellationToken.None);

        await Assert.That(scope).IsNotNull();
        await Assert.That(connection.Executed.Count).IsEqualTo(1);
        await Assert.That(connection.Executed[0].Sql).IsEqualTo("SET TRANSACTION READ ONLY");
        await Assert.That(connection.Executed[0].Transaction).IsSameReferenceAs(connection.LastTransaction);
        await scope!.DisposeAsync();
        await Assert.That(connection.LastTransaction!.RolledBack).IsTrue();
        await Assert.That(connection.LastTransaction.Committed).IsFalse();
    }

    [Test]
    public async Task MySql_read_only_transaction_is_started_and_ended_by_sql()
    {
        ISqlDialect dialect = new MySqlDialect();
        var connection = new RecordingConnection();

        var scope = await dialect.BeginReadOnlyTransactionAsync(connection, CancellationToken.None);
        await scope!.DisposeAsync();

        await Assert.That(string.Join(" | ", connection.Executed.Select(e => e.Sql))).IsEqualTo("START TRANSACTION READ ONLY | ROLLBACK");
    }

    [Test]
    public async Task SqlServer_has_no_read_only_transaction()
    {
        ISqlDialect dialect = new SqlServerDialect();
        var connection = new RecordingConnection();

        await Assert.That(await dialect.BeginReadOnlyTransactionAsync(connection, CancellationToken.None)).IsNull();
        await Assert.That(connection.Executed.Count).IsEqualTo(0);
    }

    // ---------- Verificação de somente leitura: falha fechada ----------

    private sealed class CountingSecurity : IOrmConnectionSecurity
    {
        public int Opens;

        public Task<Result<DbConnection>> OpenConnectionAsync(OrmConnectionKind kind, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Opens);
            return Task.FromResult(Result<DbConnection>.Failure(OrmErrors.ConnectionUnavailable()));
        }

        public Task<Result> ConfigureConnectionAsync(DbConnection connection, OrmConnectionKind kind, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private static OrmQueryExecutor Executor(OrmOptions options, IOrmConnectionSecurity security) =>
        new(security, new OrmOperationRunner(NullLogger<OrmOperationRunner>.Instance, options), options);

    [Test]
    public async Task Without_a_read_only_validator_no_query_reaches_the_database()
    {
        var options = new OrmOptions { ConnectionSecretName = TestOrm.SecretName };
        options.Validate();
        var security = new CountingSecurity();

        var result = await Executor(options, security).QueryAsync<int>(SqlQuery.Create("sem-dialeto", "SELECT 1"));

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.QueryNotAllowedCode);
        await Assert.That(security.Opens).IsEqualTo(0);
    }

    [Test]
    [Arguments("PostgreSql", "SELECT pg_sleep(30)")]
    [Arguments("MySql", "SELECT GET_LOCK('x', 30)")]
    [Arguments("Oracle", "SELECT SYS.DBMS_LOCK.SLEEP(30) FROM dual")]
    public async Task Dialect_validator_refuses_before_opening_the_connection(string provider, string sql)
    {
        var options = TestOrm.Options();
        options.Dialect = provider switch { "PostgreSql" => new PostgreSqlDialect(), "MySql" => new MySqlDialect(), _ => new OracleDialect() };
        var security = new CountingSecurity();

        var result = await Executor(options, security).QueryAsync<int>(SqlQuery.Create("perigosa", sql));

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.QueryNotAllowedCode);
        await Assert.That(security.Opens).IsEqualTo(0);
    }

    // ---------- Trilha histórica: tamanho e nome da tabela ----------

    private static DefaultAuditTableContext AuditContext(string database, HistoricalAuditInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<DefaultAuditTableContext>()
            .UseInMemoryDatabase(database)
            .AddInterceptors(interceptor)
            .Options);

    [Test]
    public async Task Audit_values_are_cut_at_the_limit_and_binary_becomes_its_size()
    {
        string database = Guid.NewGuid().ToString("N");
        var interceptor = new HistoricalAuditInterceptor(new DefaultOrmAuditContextProvider(), TimeProvider.System, maxValueLength: 32);
        await using (var context = AuditContext(database, interceptor))
        {
            context.Blobs.Add(new AuditedBlob { Id = 1, Text = new string('x', 100), Data = new byte[5000] });
            await context.SaveChangesAsync();
        }

        await using var reading = AuditContext(database, interceptor);
        var record = await reading.Set<OrmAuditRecord>().AsNoTracking().SingleAsync();
        // O JSON da trilha usa o codificador padrão (escapa '+', '<', '>' e não ASCII): confere o valor decodificado
        using var values = JsonDocument.Parse(record.NewValues!);
        await Assert.That(values.RootElement.GetProperty("Text").GetString()).IsEqualTo(new string('x', 32) + "…(+68)");
        await Assert.That(values.RootElement.GetProperty("Data").GetString()).IsEqualTo("<5000 bytes>");
    }

    [Test]
    public async Task Audit_text_fields_are_fitted_to_the_column_size()
    {
        string database = Guid.NewGuid().ToString("N");
        var actor = new OrmAuditActorContext { ActorId = new string('a', 1000), ActorType = "User", Endpoint = new string('e', 1000) };
        var interceptor = new HistoricalAuditInterceptor(new DefaultOrmAuditContextProvider(null, () => actor), TimeProvider.System);
        await using (var context = AuditContext(database, interceptor))
        {
            context.Blobs.Add(new AuditedBlob { Id = 2, Text = "t" });
            await context.SaveChangesAsync();
        }

        await using var reading = AuditContext(database, interceptor);
        var record = await reading.Set<OrmAuditRecord>().AsNoTracking().SingleAsync();
        // Um texto maior que a coluna derrubaria o salvamento inteiro no banco relacional
        await Assert.That(record.ActorId!.Length).IsEqualTo(256);
        await Assert.That(record.Endpoint!.Length).IsEqualTo(256);
    }

    [Test]
    public async Task Audit_value_limit_is_validated()
    {
        await Assert.That(() => TestOrm.Options(o => o.MaxAuditValueLength = 10)).Throws<InvalidConfigurationException>();
        await Assert.That(() => TestOrm.Options(o => o.MaxAuditValueLength = 2_000_000)).Throws<InvalidConfigurationException>();
        await Assert.That(() => TestOrm.Options(o => o.AuditLogTableName = " ")).Throws<InvalidConfigurationException>();
        await Assert.That(() => TestOrm.Options(o => o.AuditLogTableName = new string('t', 129))).Throws<InvalidConfigurationException>();
    }

    [Test]
    public async Task AuditLogTableName_names_the_trail_table_unless_the_model_names_it()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecVault(vault => vault.UseInMemory(o =>
        {
            o.AllowOutsideDevelopment = true;
            o.InitialSecrets[TestOrm.SecretName] = SqlServerSecret;
        }));
        services.AddTecOrm<DefaultAuditTableContext>(o =>
        {
            o.ConnectionSecretName = TestOrm.SecretName;
            o.AuditLogTableName = "TrilhaVendas";
        });
        services.AddTecOrm<ExplicitAuditTableContext>(o =>
        {
            o.ConnectionSecretName = TestOrm.SecretName;
            o.AuditLogTableName = "Ignorada";
        });
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();

        var configured = scope.ServiceProvider.GetRequiredService<DefaultAuditTableContext>().Model.FindEntityType(typeof(OrmAuditRecord))!;
        var explicitName = scope.ServiceProvider.GetRequiredService<ExplicitAuditTableContext>().Model.FindEntityType(typeof(OrmAuditRecord))!;

        await Assert.That(configured.GetTableName()).IsEqualTo("TrilhaVendas");
        await Assert.That(explicitName.GetTableName()).IsEqualTo("Explicita");
        await Assert.That(explicitName.GetSchema()).IsEqualTo("auditoria");
    }

    [Test]
    public async Task Without_the_registration_the_trail_table_keeps_the_default_name()
    {
        await using var context = new DefaultAuditTableContext(new DbContextOptionsBuilder<DefaultAuditTableContext>().UseSqlServer().Options);

        await Assert.That(context.Model.FindEntityType(typeof(OrmAuditRecord))!.GetTableName()).IsEqualTo(OrmOptions.DefaultAuditLogTableName);
    }

    // ---------- SQL de modelo por banco ----------

    private static ModelSqlContext ModelContext(Func<DbContextOptionsBuilder<ModelSqlContext>, DbContextOptionsBuilder<ModelSqlContext>> provider) =>
        new(provider(new DbContextOptionsBuilder<ModelSqlContext>()).Options);

    private static string? SoftDeleteIndexFilter(DbContext context) =>
        context.Model.FindEntityType(typeof(SoftDeleteCategory))!.GetIndexes().Single(i => i.Properties.Single().Name == nameof(ISoftDelete.IsDeleted)).GetFilter();

    // A collation é anotação de design (migrações/script): o modelo de execução (context.Model) não a guarda.
    private static string? KeyCollation(DbContext context) =>
        context.GetService<IDesignTimeModel>().Model.FindEntityType("TEC.ORM.EntityFrameworkCore.Idempotency.IdempotencyRecord")!.FindProperty("Key")!.GetCollation();

    [Test]
    public async Task SqlServer_model_keeps_the_t_sql_index_filter_and_binary_collation()
    {
        await using var context = ModelContext(b => b.UseSqlServer());

        await Assert.That(SoftDeleteIndexFilter(context)).IsEqualTo("[IsDeleted] = 0");
        await Assert.That(KeyCollation(context)).IsEqualTo(IdempotencyModelBuilderExtensions.BinaryCollation);
    }

    [Test]
    public async Task PostgreSql_model_uses_its_own_index_filter_and_collation()
    {
        await using var context = ModelContext(b => b.UseNpgsql());

        await Assert.That(SoftDeleteIndexFilter(context)).IsEqualTo("\"IsDeleted\" = FALSE");
        await Assert.That(KeyCollation(context)).IsEqualTo("C");
    }

    [Test]
    public async Task Oracle_model_has_no_filtered_index_nor_column_collation()
    {
        await using var context = ModelContext(b => b.UseOracle());

        await Assert.That(SoftDeleteIndexFilter(context)).IsNull();
        await Assert.That(KeyCollation(context)).IsNull();
    }

    [Test]
    public async Task MySql_script_declares_the_binary_collation_in_the_provider_annotations()
    {
        await using var context = ModelContext(b => b.UseMySQL());
        string script = context.Database.GenerateCreateScript();

        // O MySql.EntityFrameworkCore ignora a collation relacional: sem as anotações dele, escopo e chave ficariam com a
        // collation padrão do banco (sem diferenciar maiúsculas) e "Abc" e "abc" seriam a mesma chave
        await Assert.That(script.Split("CHARACTER SET utf8mb4 COLLATE utf8mb4_bin").Length - 1).IsEqualTo(2);
        await Assert.That(script).DoesNotContain(IdempotencyModelBuilderExtensions.BinaryCollation);
    }

    [Test]
    public async Task Oracle_script_keeps_large_trail_values_and_response_bodies()
    {
        await using var context = ModelContext(b => b.UseOracle());
        string script = context.Database.GenerateCreateScript();

        // Sem tipo, o provedor do Oracle usaria NVARCHAR2(2000) e RAW(2000): uma alteração ou resposta maior falharia
        await Assert.That(script).Contains("\"OldValues\" NCLOB");
        await Assert.That(script).Contains("\"NewValues\" NCLOB");
        await Assert.That(script).Contains("\"AffectedColumns\" NCLOB");
        await Assert.That(script).Contains("\"Body\" BLOB");
        await Assert.That(script).DoesNotContain("RAW(2000)");
    }

    [Test]
    public async Task Model_sql_per_database_matches_what_each_database_supports()
    {
        await Assert.That(SoftDeleteQueryFilterConvention.IndexFilter(OrmDatabase.SqlServer, "Excluido")).IsEqualTo("[Excluido] = 0");
        await Assert.That(SoftDeleteQueryFilterConvention.IndexFilter(OrmDatabase.PostgreSql, "is_deleted")).IsEqualTo("\"is_deleted\" = FALSE");
        // MySQL e Oracle não têm índice filtrado; provedor desconhecido não recebe SQL de outro banco
        await Assert.That(SoftDeleteQueryFilterConvention.IndexFilter(OrmDatabase.MySql, "IsDeleted")).IsNull();
        await Assert.That(SoftDeleteQueryFilterConvention.IndexFilter(OrmDatabase.Oracle, "IsDeleted")).IsNull();
        await Assert.That(SoftDeleteQueryFilterConvention.IndexFilter(OrmDatabase.Other, "IsDeleted")).IsNull();
        // Escape do nome no delimitador de cada banco
        await Assert.That(SoftDeleteQueryFilterConvention.IndexFilter(OrmDatabase.SqlServer, "a]b")).IsEqualTo("[a]]b] = 0");
        await Assert.That(SoftDeleteQueryFilterConvention.IndexFilter(OrmDatabase.PostgreSql, "a\"b")).IsEqualTo("\"a\"\"b\" = FALSE");

        await Assert.That(IdempotencyModelConvention.BinaryCollationFor(OrmDatabase.MySql)).IsEqualTo("utf8mb4_bin");
        await Assert.That(IdempotencyModelConvention.BinaryCollationFor(OrmDatabase.PostgreSql)).IsEqualTo("C");
        await Assert.That(IdempotencyModelConvention.BinaryCollationFor(OrmDatabase.Oracle)).IsNull();
    }

    [Test]
    public async Task Database_is_detected_from_the_ef_core_provider()
    {
        await Assert.That(OrmDatabaseProvider.Detect(new DbContextOptionsBuilder().UseSqlServer().Options)).IsEqualTo(OrmDatabase.SqlServer);
        await Assert.That(OrmDatabaseProvider.Detect(new DbContextOptionsBuilder().UseNpgsql().Options)).IsEqualTo(OrmDatabase.PostgreSql);
        await Assert.That(OrmDatabaseProvider.Detect(new DbContextOptionsBuilder().UseMySQL().Options)).IsEqualTo(OrmDatabase.MySql);
        await Assert.That(OrmDatabaseProvider.Detect(new DbContextOptionsBuilder().UseOracle().Options)).IsEqualTo(OrmDatabase.Oracle);
        await Assert.That(OrmDatabaseProvider.Detect(new DbContextOptionsBuilder().UseInMemoryDatabase("x").Options)).IsEqualTo(OrmDatabase.Other);
        await Assert.That(OrmDatabaseProvider.Detect(null)).IsEqualTo(OrmDatabase.Other);
    }
}
