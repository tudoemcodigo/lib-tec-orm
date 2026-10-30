using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TEC.Core.Security;
using TEC.Cqrs.Idempotency;
using TEC.ORM.Abstractions;
using TEC.ORM.Auditing;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Idempotency;
using TEC.ORM.MySql.DependencyInjection;
using TEC.ORM.Oracle.DependencyInjection;
using TEC.ORM.PostgreSql.DependencyInjection;
using TEC.ORM.Queries;
using TEC.ORM.SqlServer.DependencyInjection;
using TEC.ORM.Testing;
using TEC.ORM.Tests.Fakes;
using TEC.Vault.DependencyInjection;
using TEC.Vault.InMemory;

namespace TEC.ORM.Tests.Integration;

/// <summary>
/// Bancos das integrações por provedor. O SQL Server usa a conexão dos demais testes (<see cref="SqlServerFixture"/>); os outros
/// leem a conexão de uma variável de ambiente (ou do user-secrets / <c>appsettings.Local.json</c>, seção <c>TecTestes</c>, como
/// em <see cref="TestDatabase"/>). Sem ela, os testes daquele banco são pulados com o motivo.
/// </summary>
/// <remarks>
/// <para>A conexão passa pelas mesmas regras de produção: TLS obrigatório. Os testes ligam <c>AllowTrustServerCertificate</c>
/// (certificado autoassinado de container), o que aceita <c>SSL Mode=Require</c> no PostgreSQL, <c>SslMode=Required</c> no
/// MySQL e <c>SSL_SERVER_DN_MATCH=NO</c> no Oracle; sem TLS (ou com o Oracle fora de <c>TCPS</c>) a conexão é recusada.</para>
/// <para>O usuário da conexão precisa criar tabelas no schema padrão dele: a preparação apaga e recria as tabelas de
/// <see cref="ProviderIntegrationContext"/> uma vez por execução. No Oracle, o DDL usa a compatibilidade SQL padrão do provedor
/// (com <c>BOOLEAN</c>, do Oracle Database 23ai).</para>
/// </remarks>
internal static class ProviderDatabases
{
    public const string SqlServer = "SqlServer";
    public const string PostgreSql = "PostgreSql";
    public const string MySql = "MySql";
    public const string Oracle = "Oracle";

    /// <summary>Conexão do PostgreSQL de testes (chave <c>OrmPgConexao</c> no user-secrets).</summary>
    public const string PostgreSqlVariable = "TEC_TESTES_ORM_PG_CONEXAO";

    /// <summary>Conexão do MySQL de testes (chave <c>OrmMySqlConexao</c> no user-secrets).</summary>
    public const string MySqlVariable = "TEC_TESTES_ORM_MYSQL_CONEXAO";

    /// <summary>Conexão do Oracle de testes (chave <c>OrmOracleConexao</c> no user-secrets).</summary>
    public const string OracleVariable = "TEC_TESTES_ORM_ORACLE_CONEXAO";

    private const string SecretName = "orm-testes-provedor";

    private static readonly Dictionary<string, (string Variable, string Key, string Label)> Sources = new()
    {
        [PostgreSql] = (PostgreSqlVariable, "OrmPgConexao", "PostgreSQL"),
        [MySql] = (MySqlVariable, "OrmMySqlConexao", "MySQL"),
        [Oracle] = (OracleVariable, "OrmOracleConexao", "Oracle")
    };

    /// <summary>Preparação de cada banco, uma vez por execução: motivo do pulo, ou <c>null</c> com as tabelas prontas.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> Prepared = new();

    /// <summary>Usuário da autoria e da trilha nos testes.</summary>
    public sealed class IntegrationUser : ICurrentUser
    {
        public const string UserId = "oid-integracao";
        public const string Tenant = "contoso";

        public PrincipalKind Kind => PrincipalKind.User;

        public string? Id => UserId;

        public string? TenantId => Tenant;
    }

    /// <summary>Pula o teste se o banco não estiver configurado; senão garante as tabelas.</summary>
    public static async Task RequireAsync(string database)
    {
        if (database == SqlServer)
            await SqlServerFixture.RequireAsync();

        string? skip = await Prepared.GetOrAdd(database, name => new Lazy<Task<string?>>(() => PrepareAsync(name))).Value;
        Skip.When(skip is not null, skip ?? string.Empty);
    }

    /// <summary>Senha da conexão (para provar que não aparece nos logs), quando tem tamanho para isso.</summary>
    public static string? Password(string database)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = ConnectionString(database) };
        foreach (string key in new[] { "Password", "Pwd" })
        {
            if (builder.TryGetValue(key, out object? value) && value is string { Length: >= 8 } password)
                return password;
        }
        return null;
    }

    /// <summary>Container com TEC.Vault em memória (ou o cofre do SQL Server de testes) + TEC.ORM do banco informado.</summary>
    public static ServiceProvider Build(string database, CapturingLoggerProvider logs, Action<OrmOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        string secretName = SecretName;
        services.AddTecVault(vault =>
        {
            if (database == SqlServer)
                secretName = TestDatabase.ConfigureVault(vault, new TestConnection(SqlServerFixture.Source, SqlServerFixture.ConnectionString, null));
            else
                vault.UseInMemory(options =>
                {
                    options.AllowOutsideDevelopment = true;
                    options.InitialSecrets[SecretName] = ConnectionString(database);
                });
            vault.EnableSecretCache(TimeSpan.FromMinutes(5));
        });
        services.AddScoped<ICurrentUser, IntegrationUser>();

        void Configure(OrmOptions orm)
        {
            orm.ConnectionSecretName = secretName;
            orm.ApplicationName = "TEC.ORM.Tests";
            orm.AllowTrustServerCertificate = true;   // certificado autoassinado do container
            orm.AuditLogTableName = ProviderIntegrationContext.AuditTable;
            configure?.Invoke(orm);
        }

        switch (database)
        {
            case SqlServer: services.AddTecOrm<ProviderIntegrationContext>(Configure); break;
            case PostgreSql: services.AddTecOrmPostgreSql<ProviderIntegrationContext>(Configure); break;
            case MySql: services.AddTecOrmMySql<ProviderIntegrationContext>(Configure); break;
            case Oracle: services.AddTecOrmOracle<ProviderIntegrationContext>(Configure); break;
            default: throw new ArgumentOutOfRangeException(nameof(database), database, "Banco desconhecido.");
        }
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static string ConnectionString(string database) => database == SqlServer
        ? SqlServerFixture.ConnectionString
        : TestDatabase.Read(Sources[database].Variable, Sources[database].Key)
          ?? throw new InvalidOperationException("Chame RequireAsync antes de usar o banco.");

    private static async Task<string?> PrepareAsync(string database)
    {
        if (database != SqlServer)
        {
            var (variable, key, label) = Sources[database];
            if (TestDatabase.Read(variable, key) is null)
                return $"{label} de testes não configurado: defina {variable} (ou 'dotnet user-secrets set {TestDatabase.Section}:{key} " +
                       $"\"<conexão>\" --id {TestDatabase.UserSecretsId}'). A conexão precisa de TLS, como em produção.";
        }

        await using var provider = Build(database, new CapturingLoggerProvider());
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ProviderIntegrationContext>();
        var sql = context.GetService<ISqlGenerationHelper>();
        foreach (string table in new[] { ProviderIntegrationContext.NotesTable, ProviderIntegrationContext.AuditTable, ProviderIntegrationContext.IdempotencyTable })
        {
            // Sem IF EXISTS (o Oracle anterior ao 23ai não tem): a tabela que não existe só falha e é ignorada
            string drop = "DROP TABLE " + sql.DelimitIdentifier(table) + (database == Oracle ? " PURGE" : string.Empty);
            try
            {
#pragma warning disable EF1002 // nome de tabela constante do próprio teste, delimitado pelo provedor
                await context.Database.ExecuteSqlRawAsync(drop);
#pragma warning restore EF1002
            }
            catch (DbException)
            {
                // Primeira execução: a tabela ainda não existe
            }
        }
        await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        return null;
    }
}

/// <summary>
/// O mesmo pipeline contra cada banco real (SQL Server, PostgreSQL, MySQL e Oracle): autoria e trilha com a chave gerada pelo
/// banco, conflito de unicidade, exclusão lógica e física auditadas, consulta Dapper com o prefixo de parâmetro do dialeto,
/// transação somente leitura e idempotência com chave sensível a maiúsculas.
/// </summary>
[Category(TestCategories.Integration)]
public class ProviderIntegrationTests
{
    private static string Unique() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Executa o teste num escopo e, ao final, prova que a senha da conexão não apareceu nos logs.</summary>
    private static async Task WithScopeAsync(string database, Func<IServiceProvider, Task> test, Action<OrmOptions>? configure = null)
    {
        await ProviderDatabases.RequireAsync(database);
        var logs = new CapturingLoggerProvider();
        await using (var provider = ProviderDatabases.Build(database, logs, configure))
        await using (var scope = provider.CreateAsyncScope())
            await test(scope.ServiceProvider);

        if (ProviderDatabases.Password(database) is { } password)
            await Assert.That(logs.AllText).DoesNotContain(password);
    }

    private static async Task<List<OrmAuditRecord>> TrailAsync(IServiceProvider services, long id)
    {
        string key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return await services.GetRequiredService<ProviderIntegrationContext>().Set<OrmAuditRecord>().AsNoTracking()
            .Where(r => r.EntityName == nameof(ProviderNote) && r.EntityKey == key)
            .OrderBy(r => r.Id)
            .ToListAsync();
    }

    private static Task<ProviderNote?> StoredAsync(IServiceProvider services, long id) =>
        services.GetRequiredService<ProviderIntegrationContext>().Notes.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(n => n.Id == id);

    /// <summary>SQL do teste com os nomes delimitados pelo dialeto e os valores como parâmetros (<c>{0}</c>, <c>{1}</c>...).</summary>
    private static SqlQuery Query(IServiceProvider services, string name, string format, params object?[] values)
    {
        var dialect = services.GetRequiredService<ISqlDialect>();
        string text = format
            .Replace("[Notas]", dialect.DelimitIdentifier(ProviderIntegrationContext.NotesTable), StringComparison.Ordinal)
            .Replace("[Id]", dialect.DelimitIdentifier(nameof(ProviderNote.Id)), StringComparison.Ordinal)
            .Replace("[Code]", dialect.DelimitIdentifier(nameof(ProviderNote.Code)), StringComparison.Ordinal)
            .Replace("[Text]", dialect.DelimitIdentifier(nameof(ProviderNote.Text)), StringComparison.Ordinal);
        return SqlQuery.Interpolated(name, FormattableStringFactory.Create(text, values));
    }

    [Test]
    [Arguments(ProviderDatabases.SqlServer)]
    [Arguments(ProviderDatabases.PostgreSql)]
    [Arguments(ProviderDatabases.MySql)]
    [Arguments(ProviderDatabases.Oracle)]
    public async Task Create_stamps_authorship_and_the_trail_gets_the_key_generated_by_the_database(string database) =>
        await WithScopeAsync(database, async services =>
        {
            var created = await services.GetRequiredService<IOrmRepository<ProviderNote, long>>()
                .CreateAsync(new ProviderNote { Code = "c" + Unique(), Text = "primeira" });

            await Assert.That(created.IsSuccess).IsTrue();
            await Assert.That(created.Value.Id).IsGreaterThan(0L);
            await Assert.That(created.Value.CreatedBy).IsEqualTo(ProviderDatabases.IntegrationUser.UserId);
            await Assert.That(created.Value.CreatedByTenant).IsEqualTo(ProviderDatabases.IntegrationUser.Tenant);

            // Registro gravado depois do INSERT: a chave é a do identity, não um valor temporário do EF Core
            var trail = await TrailAsync(services, created.Value.Id);
            await Assert.That(trail.Count).IsEqualTo(1);
            await Assert.That(trail[0].Action).IsEqualTo("Created");
            await Assert.That(trail[0].ActorId).IsEqualTo(ProviderDatabases.IntegrationUser.UserId);
            await Assert.That(trail[0].TenantId).IsEqualTo(ProviderDatabases.IntegrationUser.Tenant);
            await Assert.That(trail[0].NewValues).Contains("primeira");
        });

    [Test]
    [Arguments(ProviderDatabases.SqlServer)]
    [Arguments(ProviderDatabases.PostgreSql)]
    [Arguments(ProviderDatabases.MySql)]
    [Arguments(ProviderDatabases.Oracle)]
    public async Task Duplicate_unique_value_is_a_conflict_and_leaves_no_trail(string database) =>
        await WithScopeAsync(database, async services =>
        {
            var notes = services.GetRequiredService<IOrmRepository<ProviderNote, long>>();
            string code = "d" + Unique();
            await Assert.That((await notes.CreateAsync(new ProviderNote { Code = code, Text = "original" })).IsSuccess).IsTrue();

            var duplicate = await notes.CreateAsync(new ProviderNote { Code = code, Text = "duplicada" });

            // Violação de unicidade de cada banco (2627/2601, 23505, 1062, ORA-00001) traduzida para o mesmo erro
            await Assert.That(duplicate.Error!.Code).IsEqualTo(OrmErrors.ConflictCode);
            var count = await services.GetRequiredService<IOrmQueryExecutor>().ExecuteScalarAsync<long>(
                Query(services, "notas.por-codigo", "SELECT COUNT(*) FROM [Notas] WHERE [Code] = {0}", code));
            await Assert.That(count.Value).IsEqualTo(1L);
            // A trilha da tentativa foi desfeita junto com o INSERT
            int duplicatedTrail = await services.GetRequiredService<ProviderIntegrationContext>().Set<OrmAuditRecord>().AsNoTracking()
                .CountAsync(r => r.EntityName == nameof(ProviderNote) && r.NewValues!.Contains("duplicada"));
            await Assert.That(duplicatedTrail).IsEqualTo(0);
        });

    [Test]
    [Arguments(ProviderDatabases.SqlServer)]
    [Arguments(ProviderDatabases.PostgreSql)]
    [Arguments(ProviderDatabases.MySql)]
    [Arguments(ProviderDatabases.Oracle)]
    public async Task Soft_delete_fills_who_deleted_and_records_SoftDeleted(string database) =>
        await WithScopeAsync(database, async services =>
        {
            var notes = services.GetRequiredService<IOrmRepository<ProviderNote, long>>();
            long id = (await notes.CreateAsync(new ProviderNote { Code = "s" + Unique(), Text = "excluir" })).Value.Id;

            await Assert.That((await notes.DeleteAsync(id)).IsSuccess).IsTrue();

            await Assert.That((await notes.GetByIdAsync(id)).Error!.Code).IsEqualTo(OrmErrors.NotFoundCode);
            var stored = await StoredAsync(services, id);
            await Assert.That(stored!.IsDeleted).IsTrue();
            await Assert.That(stored.DeletedAt).IsNotNull();
            await Assert.That(stored.DeletedBy).IsEqualTo(ProviderDatabases.IntegrationUser.UserId);
            await Assert.That(stored.DeletedByTenant).IsEqualTo(ProviderDatabases.IntegrationUser.Tenant);

            var trail = await TrailAsync(services, id);
            await Assert.That(string.Join(",", trail.Select(r => r.Action))).IsEqualTo("Created,SoftDeleted");
        });

#pragma warning disable TECORM014 // exclusão física intencional nos testes
    [Test]
    [Arguments(ProviderDatabases.SqlServer)]
    [Arguments(ProviderDatabases.PostgreSql)]
    [Arguments(ProviderDatabases.MySql)]
    [Arguments(ProviderDatabases.Oracle)]
    public async Task Hard_delete_removes_the_row_and_records_Deleted(string database) =>
        await WithScopeAsync(database, async services =>
        {
            var notes = services.GetRequiredService<IOrmRepository<ProviderNote, long>>();
            long id = (await notes.CreateAsync(new ProviderNote { Code = "h" + Unique(), Text = "apagar" })).Value.Id;

            await Assert.That((await notes.HardDeleteAsync(id)).IsSuccess).IsTrue();

            await Assert.That(await StoredAsync(services, id)).IsNull();
            var trail = await TrailAsync(services, id);
            await Assert.That(string.Join(",", trail.Select(r => r.Action))).IsEqualTo("Created,Deleted");
            await Assert.That(trail[1].ActorId).IsEqualTo(ProviderDatabases.IntegrationUser.UserId);
            await Assert.That(trail[1].OldValues).Contains("apagar");
        });
#pragma warning restore TECORM014

    [Test]
    [Arguments(ProviderDatabases.SqlServer)]
    [Arguments(ProviderDatabases.PostgreSql)]
    [Arguments(ProviderDatabases.MySql)]
    [Arguments(ProviderDatabases.Oracle)]
    public async Task Interpolated_query_binds_values_with_the_dialect_prefix(string database) =>
        await WithScopeAsync(database, async services =>
        {
            string code = "q" + Unique();
            await services.GetRequiredService<IOrmRepository<ProviderNote, long>>().CreateAsync(new ProviderNote { Code = code, Text = "lida" });
            var executor = services.GetRequiredService<IOrmQueryExecutor>();

            // Oracle: :p0 (com @p0 o ODP.NET recusaria o comando); demais: @p0
            var text = await executor.QuerySingleOrDefaultAsync<string>(
                Query(services, "notas.texto", "SELECT [Text] FROM [Notas] WHERE [Code] = {0}", code));
            // Valor hostil continua sendo só um valor
            var none = await executor.QueryAsync<string>(
                Query(services, "notas.texto-hostil", "SELECT [Text] FROM [Notas] WHERE [Code] = {0}", "' OR 1=1 --"));

            await Assert.That(text.Value).IsEqualTo("lida");
            await Assert.That(none.Value.Count).IsEqualTo(0);
        });

    [Test]
    [Arguments(ProviderDatabases.PostgreSql)]
    [Arguments(ProviderDatabases.MySql)]
    [Arguments(ProviderDatabases.Oracle)]
    public async Task Read_only_transaction_refuses_a_write_even_without_the_validator(string database) =>
        await WithScopeAsync(database, async services =>
        {
            long id = (await services.GetRequiredService<IOrmRepository<ProviderNote, long>>()
                .CreateAsync(new ProviderNote { Code = "r" + Unique(), Text = "original" })).Value.Id;

            // Verificação de leitura desligada: só a transação READ ONLY do dialeto separa o UPDATE do banco
            var write = await services.GetRequiredService<IOrmQueryExecutor>().ExecuteScalarAsync<long?>(
                Query(services, "notas.escrita", "UPDATE [Notas] SET [Text] = {0} WHERE [Id] = {1}", "alterado", id));

            await Assert.That(write.IsFailure).IsTrue();
            await Assert.That((await StoredAsync(services, id))!.Text).IsEqualTo("original");
        }, configure: options => options.EnforceReadOnlyQueries = false);

    [Test]
    [Arguments(ProviderDatabases.SqlServer)]
    [Arguments(ProviderDatabases.PostgreSql)]
    [Arguments(ProviderDatabases.MySql)]
    [Arguments(ProviderDatabases.Oracle)]
    public async Task Idempotency_reserves_once_tells_keys_apart_by_case_and_keeps_large_responses(string database) =>
        await WithScopeAsync(database, async services =>
        {
            var store = new EfIdempotencyStore<ProviderIntegrationContext>(services.GetRequiredService<ProviderIntegrationContext>(),
                TimeProvider.System);
            string scope = "usuario-" + Unique();
            var upper = new IdempotencyKey(scope, "Pedido-" + Unique());
            var lower = new IdempotencyKey(scope, upper.Key.ToLowerInvariant());
            static IdempotencyRequest Request(IdempotencyKey key) => new(key, "H1", TimeSpan.FromHours(1), TimeSpan.FromSeconds(60));
            // Maior que RAW(2000), o padrão do provedor do Oracle para byte[]
            byte[] body = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();

            var first = await store.TryBeginAsync(Request(upper), CancellationToken.None);
            var concurrent = await store.TryBeginAsync(Request(upper), CancellationToken.None);
            var otherCase = await store.TryBeginAsync(Request(lower), CancellationToken.None);
            bool completed = await store.CompleteAsync(upper, first.LockId,
                new IdempotentResponse(201, "application/json", new Dictionary<string, string> { ["Location"] = "/pedidos/1" }, body),
                CancellationToken.None);
            var replay = await store.TryBeginAsync(Request(upper), CancellationToken.None);

            await Assert.That(first.Status).IsEqualTo(IdempotencyBeginStatus.Started);
            await Assert.That(concurrent.Status).IsEqualTo(IdempotencyBeginStatus.InProgress);
            // Comparação ordinal: "Pedido-x" e "pedido-x" são chaves diferentes em todos os bancos
            await Assert.That(otherCase.Status).IsEqualTo(IdempotencyBeginStatus.Started);
            await Assert.That(completed).IsTrue();
            await Assert.That(replay.Status).IsEqualTo(IdempotencyBeginStatus.Completed);
            await Assert.That(replay.Response!.Body.ToArray().SequenceEqual(body)).IsTrue();
        });
}
