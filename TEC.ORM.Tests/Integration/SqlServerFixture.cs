using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.DependencyInjection;
using TEC.ORM.SqlServer.HealthChecks;
using TEC.ORM.SqlServer.Security;
using TEC.ORM.Testing;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Database.DbFirst;
using TEC.ORM.Tests.Fakes;
using TEC.Vault.DependencyInjection;

namespace TEC.ORM.Tests.Integration;

/// <summary>
/// Acesso ao SQL Server de testes (ver <see cref="TestDatabase"/>: conexão pelo TEC.Vault, em memória a partir de
/// <c>TEC_TESTES_ORM_SQL_CONEXAO</c> ou do Key Vault quando <c>TEC_TESTES_ORM_SQL_SEGREDO</c> existir). Sem nenhuma das duas,
/// os testes (categoria <c>Integracao</c>) são pulados com o motivo.
/// </summary>
/// <remarks>
/// Preparação, uma vez por execução: cria o banco se não existir (com <c>READ_COMMITTED_SNAPSHOT</c>), aplica as migrations do
/// schema <c>codefirst</c> (opção 1) e executa o script idempotente do schema <c>dbfirst</c> (opção 2). Cada teste usa dados com
/// sufixo único, então os testes rodam em paralelo e repetidamente sem limpeza.
/// </remarks>
internal static class SqlServerFixture
{
    private static readonly Lazy<Task<TestConnection>> Initialized = new(InitializeAsync);

    /// <summary>Estado usado por <see cref="Build{TContext}"/> enquanto a própria preparação roda (antes do Lazy concluir).</summary>
    private static TestConnection? PreparationState;

    private static TestConnection Current => PreparationState
        ?? (Initialized.Value.IsCompletedSuccessfully
            ? Initialized.Value.Result
            : throw new InvalidOperationException("Chame RequireAsync antes de usar a fixture."));

    /// <summary>Origem da conexão em uso.</summary>
    public static ConnectionSource Source => Current.Source;

    /// <summary>Conexão de testes (só para o código de teste: preparar o banco e provar que a senha não vaza).</summary>
    public static string ConnectionString => Current.ConnectionString!;

    /// <summary>Senha da conexão de testes, para provar que ela nunca aparece nos logs.</summary>
    public static string Password => new SqlConnectionStringBuilder(ConnectionString).Password;

    /// <summary>Pula o teste se o SQL Server não estiver configurado (ou sem ICU).</summary>
    public static async Task RequireAsync()
    {
        var state = await Initialized.Value;
        Skip.When(state.SkipReason is not null, state.SkipReason ?? string.Empty);
    }

    /// <summary>
    /// Container com TEC.Vault (Key Vault ou em memória, com cache) + TEC.ORM para o contexto informado.
    /// <paramref name="secretOverride"/> força o cofre em memória com outro valor (ex.: senha errada).
    /// <paramref name="register"/> acrescenta serviços (ex.: o TEC.Cqrs).
    /// </summary>
    public static ServiceProvider Build<TContext>(CapturingLoggerProvider logs, Action<OrmOptions>? configure = null,
        string? secretOverride = null, Action<IServiceCollection>? register = null) where TContext : DbContext
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        string secretName = string.Empty;
        services.AddTecVault(vault =>
        {
            secretName = TestDatabase.ConfigureVault(vault, Current, secretOverride);
            vault.EnableSecretCache(TimeSpan.FromMinutes(5));
        });
        services.AddTecOrm<TContext>(orm =>
        {
            orm.ConnectionSecretName = secretName;
            orm.ApplicationName = "TEC.ORM.Tests";
            orm.AllowTrustServerCertificate = true;   // SQL Server no Docker: certificado autoassinado
            configure?.Invoke(orm);
        }, typeof(TContext) == typeof(CodeFirstContext) ? CodeFirstContext.ConfigureSqlServer : null);
        services.AddHealthChecks().AddTecOrm();
        register?.Invoke(services);
        return services.BuildServiceProvider(validateScopes: true);
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
            await MigrateCodeFirstAsync();
            await ApplyDbFirstScriptAsync();
            return connection;
        }
        finally
        {
            PreparationState = null;
        }
    }

    private static async Task MigrateCodeFirstAsync()
    {
        await using var provider = Build<CodeFirstContext>(new CapturingLoggerProvider());
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CodeFirstContext>().Database.MigrateAsync();
    }

    private static async Task ApplyDbFirstScriptAsync()
    {
        string script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Database", "DbFirst", "dbfirst.sql"),
            Encoding.UTF8);

        await using var provider = Build<DbFirstContext>(new CapturingLoggerProvider());
        var opened = await provider.GetRequiredService<IOrmConnectionSecurity>().OpenConnectionAsync(OrmConnectionKind.ReadWrite, CancellationToken.None);
        if (opened.IsFailure)
            throw new InvalidOperationException("Conexão indisponível para o script database first: " + opened.Error!.Code);

        await using var connection = opened.Value;
        foreach (string batch in TestDatabase.SplitBatches(script))
        {
            await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // script versionado do próprio projeto de testes (dbfirst.sql), sem entrada externa
            command.CommandText = batch;
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync();
        }
    }
}
