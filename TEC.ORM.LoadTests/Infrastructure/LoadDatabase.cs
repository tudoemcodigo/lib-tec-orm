using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TEC.Core.Security;
using TEC.ORM.Abstractions;
using TEC.ORM.Queries;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.DependencyInjection;
using TEC.ORM.SqlServer.Security;
using TEC.Vault.DependencyInjection;

namespace TEC.ORM.LoadTests.Infrastructure;

/// <summary>
/// SQL Server dos testes de carga: o mesmo banco de testes da integração do TEC.ORM.Tests (mesma configuração, ver
/// <see cref="TestDatabase"/>), com as tabelas no schema <c>loadtest</c>. Sem conexão configurada, os testes com banco são pulados
/// com o motivo.
/// </summary>
/// <remarks>
/// <para>O <c>loadtest.sql</c> (idempotente) cria o schema e as tabelas.</para>
/// <para>Isolamento: cada teste grava com um <see cref="Account.Batch"/> próprio e apaga as suas linhas no fim
/// (<see cref="PurgeAsync"/>), então os testes rodam em paralelo e repetidamente.</para>
/// </remarks>
public static class LoadDatabase
{
    private static readonly Lazy<Task<TestConnection>> Initialized = new(InitializeAsync);

    private static TestConnection? PreparationState;

    private static TestConnection Current => PreparationState
        ?? (Initialized.Value.IsCompletedSuccessfully
            ? Initialized.Value.Result
            : throw new InvalidOperationException("Chame RequireAsync antes de usar o banco de carga."));

    /// <summary>Senha da conexão de testes, para provar que ela nunca aparece nos logs.</summary>
    public static string Password => new SqlConnectionStringBuilder(Current.ConnectionString).Password;

    /// <summary>Pula o teste se o SQL Server não estiver configurado (ou sem ICU: o SqlClient exige ICU).</summary>
    public static async Task RequireAsync()
    {
        var state = await Initialized.Value;
        Skip.When(state.SkipReason is not null, state.SkipReason ?? string.Empty);
    }

    /// <summary>Lote novo (isolamento dos dados de um teste).</summary>
    public static Guid NewBatch() => Guid.NewGuid();

    /// <summary>
    /// Container com TEC.Vault (Key Vault ou InMemory, com cache) + TEC.ORM para o <see cref="LoadContext"/> e um
    /// <see cref="TestUser"/> por escopo como <see cref="ICurrentUser"/> (auditoria).
    /// </summary>
    /// <param name="logs">Provedor de logs (para provar que nada sensível vaza sob carga); sem ele, só Warning ou acima.</param>
    /// <param name="configure">Ajustes das opções do ORM.</param>
    public static ServiceProvider Build(ILoggerProvider? logs = null, Action<OrmOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(logs is null ? LogLevel.Warning : LogLevel.Trace);
            if (logs is not null)
                builder.AddProvider(logs);
        });
        string secretName = string.Empty;
        services.AddTecVault(vault =>
        {
            secretName = TestDatabase.ConfigureVault(vault, Current);
            vault.EnableSecretCache(TimeSpan.FromMinutes(10));
        });
        services.AddScoped<TestUser>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<TestUser>());
        services.AddTecOrm<LoadContext>(orm =>
        {
            orm.ConnectionSecretName = secretName;
            orm.ApplicationName = "TEC.ORM.LoadTests";
            orm.AllowTrustServerCertificate = true;   // SQL Server no Docker: certificado autoassinado
            configure?.Invoke(orm);
        });
        return services.BuildServiceProvider(validateScopes: true);
    }

#pragma warning disable TECORM014 // limpeza do lote do teste: exclusão física intencional
    /// <summary>Remove de fato todas as linhas do lote (lançamentos primeiro: a chave estrangeira é restrita).</summary>
    public static async Task PurgeAsync(IServiceProvider provider, Guid batch)
    {
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestUser>().As(999);
        var ledgerEntries = await scope.ServiceProvider.GetRequiredService<IOrmRepository<LedgerEntry, long>>().HardDeleteAsync(l => l.Batch == batch);
        var accounts = await scope.ServiceProvider.GetRequiredService<IOrmRepository<Account, Guid>>().HardDeleteAsync(c => c.Batch == batch);
        if (ledgerEntries.IsFailure || accounts.IsFailure)
            throw new InvalidOperationException($"Limpeza do lote falhou: {ledgerEntries.Error?.Code ?? accounts.Error?.Code}");
    }
#pragma warning restore TECORM014

    /// <summary>
    /// Sessões abertas no servidor com o <c>Application Name</c> do ORM (conexões físicas no pool), ou <c>null</c> se o login não
    /// tem <c>VIEW SERVER STATE</c> (sem a permissão, o SQL Server só mostra a própria sessão).
    /// </summary>
    public static async Task<int?> ServerSessionsAsync(IServiceProvider provider, string applicationName = "TEC.ORM.LoadTests")
    {
        await using var scope = provider.CreateAsyncScope();
        var queries = scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>();
        var permitted = await queries.ExecuteScalarAsync<int>(SqlQuery.Create("carga.permissao-dmv",
            "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE')"));
        if (permitted.IsFailure || permitted.Value != 1)
            return null;

        var sessions = await queries.ExecuteScalarAsync<int>(SqlQuery.Interpolated("carga.sessoes",
            $"SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name = {applicationName}"));
        return sessions.IsSuccess ? sessions.Value : null;
    }

    private static async Task<TestConnection> InitializeAsync()
    {
        var connection = await TestDatabase.ResolveAsync();
        if (connection.SkipReason is not null)
            return connection;

        PreparationState = connection;
        try
        {
            await TestDatabase.PrepareDatabaseAsync(connection.ConnectionString!);
            await ApplySchemaAsync();
            return connection;
        }
        finally
        {
            PreparationState = null;
        }
    }

    private static async Task ApplySchemaAsync()
    {
        string script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "loadtest.sql"), Encoding.UTF8);
        await using var provider = Build();
        var opened = await provider.GetRequiredService<IOrmConnectionSecurity>().OpenConnectionAsync(OrmConnectionKind.ReadWrite, CancellationToken.None);
        if (opened.IsFailure)
            throw new InvalidOperationException("Conexão indisponível para o schema de carga: " + opened.Error!.Code);

        await using var connection = opened.Value;
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // script versionado do próprio projeto (loadtest.sql), sem entrada externa
        command.CommandText = script;
#pragma warning restore CA2100
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }
}
