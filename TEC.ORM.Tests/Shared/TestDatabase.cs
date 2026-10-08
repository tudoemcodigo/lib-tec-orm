using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using TEC.Vault.AzureKeyVault;
using TEC.Vault.DependencyInjection;
using TEC.Vault.InMemory;

namespace TEC.ORM.Testing;

/// <summary>De onde veio a conexão dos testes com banco.</summary>
internal enum ConnectionSource
{
    /// <summary>Sem conexão: os testes com banco são pulados.</summary>
    None,

    /// <summary>
    /// Segredo do Azure Key Vault de testes (<see cref="TestDatabase.SecretVariable"/>): o TEC.ORM sob teste lê a conexão do cofre
    /// real, como em produção.
    /// </summary>
    KeyVault,

    /// <summary>Conexão em <see cref="TestDatabase.ConnectionVariable"/>, entregue ao TEC.Vault em memória.</summary>
    InMemory
}

/// <summary>Conexão resolvida para os testes com banco (ou o motivo de não haver).</summary>
/// <param name="Source">Origem.</param>
/// <param name="ConnectionString">Conexão (só para o código de teste: preparar o banco e provar que a senha não vaza).</param>
/// <param name="SkipReason">Motivo para pular os testes com banco; <c>null</c> quando há conexão.</param>
internal sealed record TestConnection(ConnectionSource Source, string? ConnectionString, string? SkipReason);

/// <summary>
/// Configuração do SQL Server de testes, compartilhada por <c>TEC.ORM.Tests</c> e <c>TEC.ORM.LoadTests</c> (este arquivo é
/// incluído nos dois projetos). Os testes com banco têm a categoria <c>Integracao</c> (ou <c>Carga-*</c>) e se pulam com motivo
/// quando não há conexão.
/// </summary>
/// <remarks>
/// <para>Origem da conexão (o TEC.ORM sempre a lê de um cofre, nunca de arquivo):</para>
/// <list type="number">
/// <item><description><see cref="SecretVariable"/> definido (CI na main, com OIDC): nome de um segredo no Azure Key Vault de testes
/// (<see cref="VaultUriVariable"/>, tenant em <see cref="TenantIdVariable"/>), lido pelo TEC.Vault.AzureKeyVault. Configurado e
/// inacessível é <b>falha</b>, não pulo: o ambiente declarou o cofre.</description></item>
/// <item><description>Senão, <see cref="ConnectionVariable"/> (CI com SQL Server em container, ou desenvolvimento local): entregue
/// ao TEC.Vault em memória.</description></item>
/// </list>
/// <para>Em desenvolvimento, as duas também podem vir do <c>dotnet user-secrets</c> (id <see cref="UserSecretsId"/>, o mesmo em
/// todos os projetos de teste) ou do <c>appsettings.Local.json</c> da saída (ignorado pelo git), seção <see cref="Section"/>,
/// chaves <c>OrmSqlConexao</c>, <c>OrmSqlSegredo</c>, <c>VaultUri</c> e <c>TenantId</c>.</para>
/// <para>O banco é o da conexão (obrigatório e nunca de sistema). A preparação cria o banco se não existir e liga o
/// <c>READ_COMMITTED_SNAPSHOT</c> (como no Azure SQL e no script de CI: leitores não disputam travas com escritores).</para>
/// </remarks>
internal static class TestDatabase
{
    /// <summary>Conexão do SQL Server de testes.</summary>
    public const string ConnectionVariable = "TEC_TESTES_ORM_SQL_CONEXAO";

    /// <summary>Nome do segredo (temporário, criado pelo CI) com a conexão no Key Vault de testes.</summary>
    public const string SecretVariable = "TEC_TESTES_ORM_SQL_SEGREDO";

    /// <summary>URI do Key Vault de testes (compartilhada pelos componentes).</summary>
    public const string VaultUriVariable = "TEC_TESTES_VAULT_URI";

    /// <summary>Tenant do Entra ID do Key Vault de testes (opcional).</summary>
    public const string TenantIdVariable = "TEC_TESTES_TENANT_ID";

    /// <summary>Id do <c>dotnet user-secrets</c> compartilhado por todos os projetos de teste dos componentes TEC.</summary>
    public const string UserSecretsId = "tudoemcodigo-tec-testes";

    /// <summary>Seção das chaves no user-secrets e no <c>appsettings.Local.json</c>.</summary>
    public const string Section = "TecTestes";

    /// <summary>Nome do segredo no cofre em memória (origem <see cref="ConnectionSource.InMemory"/>).</summary>
    public const string InMemorySecretName = "orm-testes-sql";

    /// <summary>Mensagem de pulo sem ICU.</summary>
    public const string SqlClientRequiresIcu = "Microsoft.Data.SqlClient não suporta o modo de globalização invariante (exige ICU).";

    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase) { "master", "model", "msdb", "tempdb" };

    private static readonly IConfiguration? UserSecrets = Load(builder => builder.AddUserSecrets(UserSecretsId));

    private static readonly IConfiguration? LocalFile = Load(builder => builder
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.Local.json", optional: true));

    /// <summary>
    /// Modo de globalização invariante (sem ICU), como no passo "sem ICU" do CI: o SqlClient não abre conexões nesse modo.
    /// </summary>
    public static bool GlobalizationInvariant =>
        (AppContext.TryGetSwitch("System.Globalization.Invariant", out bool enabled) && enabled) ||
        Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT") is "1" or "true" or "True";

    /// <summary>Nome do segredo no Key Vault, ou <c>null</c>.</summary>
    public static string? KeyVaultSecretName => Read(SecretVariable, "OrmSqlSegredo");

    /// <summary>Resolve a conexão (Key Vault ou variável) e valida o banco; sem nenhuma das duas, devolve o motivo do pulo.</summary>
    public static async Task<TestConnection> ResolveAsync()
    {
        if (GlobalizationInvariant)
            return new(ConnectionSource.None, null, SqlClientRequiresIcu);

        ConnectionSource source;
        string? connectionString;
        if (KeyVaultSecretName is { } secretName)
        {
            source = ConnectionSource.KeyVault;
            connectionString = await ReadKeyVaultSecretAsync(secretName);
        }
        else if (Read(ConnectionVariable, "OrmSqlConexao") is { } local)
        {
            source = ConnectionSource.InMemory;
            connectionString = local;
        }
        else
        {
            return new(ConnectionSource.None, null,
                $"SQL Server de testes não configurado: defina {ConnectionVariable} (ou {SecretVariable} com {VaultUriVariable}). " +
                $"Local: 'dotnet user-secrets set {Section}:OrmSqlConexao \"<conexão>\" --id {UserSecretsId}'.");
        }

        SqlConnectionStringBuilder target;
        try
        {
            target = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or KeyNotFoundException)
        {
            throw new InvalidOperationException($"A conexão de testes ({source}) não é uma string de conexão válida.");
        }
        if (string.IsNullOrWhiteSpace(target.InitialCatalog) || SystemDatabases.Contains(target.InitialCatalog.Trim()))
            throw new InvalidOperationException(
                $"A conexão de testes ({source}) deve indicar um banco exclusivo de testes (Database=<banco>, nunca de sistema).");

        return new(source, connectionString, null);
    }

    /// <summary>
    /// Registra no TEC.Vault a origem da conexão e devolve o nome do segredo que o TEC.ORM deve ler.
    /// </summary>
    /// <param name="vault">Builder do TEC.Vault.</param>
    /// <param name="connection">Conexão resolvida.</param>
    /// <param name="secretOverride">Força o cofre em memória com outro valor (ex.: senha errada).</param>
    public static string ConfigureVault(VaultBuilder vault, TestConnection connection, string? secretOverride = null)
    {
        if (secretOverride is null && connection.Source == ConnectionSource.KeyVault)
        {
            vault.UseAzureKeyVault(ConfigureKeyVault);
            return KeyVaultSecretName!;
        }

        vault.UseInMemory(options =>
        {
            options.AllowOutsideDevelopment = true;
            options.InitialSecrets[InMemorySecretName] = secretOverride ?? connection.ConnectionString!;
        });
        return InMemorySecretName;
    }

    /// <summary>
    /// Cria o banco se não existir (exige permissão de criar banco) e liga o <c>READ_COMMITTED_SNAPSHOT</c> se estiver
    /// desligado. Nome do banco sempre como parâmetro + <c>QUOTENAME</c>.
    /// </summary>
    public static async Task PrepareDatabaseAsync(string connectionString)
    {
        var target = new SqlConnectionStringBuilder(connectionString);
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", ConnectTimeout = 15 };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF DB_ID(@name) IS NULL
            BEGIN
                DECLARE @create nvarchar(300) = N'CREATE DATABASE ' + QUOTENAME(@name);
                EXEC (@create);
            END;
            IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @name AND is_read_committed_snapshot_on = 0)
            BEGIN
                DECLARE @rcsi nvarchar(400) = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE';
                EXEC (@rcsi);
            END;
            """;
        command.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = target.InitialCatalog });
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Separa um script nos lotes delimitados por <c>GO</c> (linha própria), como o sqlcmd.</summary>
    public static IEnumerable<string> SplitBatches(string script)
    {
        var batch = new System.Text.StringBuilder();
        foreach (string line in script.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                if (batch.ToString().Trim().Length > 0)
                    yield return batch.ToString();
                batch.Clear();
            }
            else
                batch.Append(line).Append('\n');
        }
        if (batch.ToString().Trim().Length > 0)
            yield return batch.ToString();
    }

    private static void ConfigureKeyVault(AzureKeyVaultOptions options)
    {
        string? uri = Read(VaultUriVariable, "VaultUri");
        if (uri is null || !Uri.TryCreate(uri, UriKind.Absolute, out var vaultUri) || vaultUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{SecretVariable} exige {VaultUriVariable} com a URL https do Key Vault de testes.");

        options.VaultUri = vaultUri;
        // CI: credencial do login OIDC (az login); local: az login, azd ou Visual Studio
        options.Authentication = AzureKeyVaultAuthentication.Developer;
        options.AllowDeveloperCredentialsOutsideDevelopment = true;
        options.TenantId = Read(TenantIdVariable, "TenantId");
        options.Stores = VaultStores.Secrets;   // menor privilégio: só leitura de segredos
    }

    private static async Task<string> ReadKeyVaultSecretAsync(string secretName)
    {
        var store = AzureKeyVaultStores.CreateSecretStore(ConfigureKeyVault);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var secret = await store.GetSecretAsync(secretName, cancellationToken: timeout.Token);
        if (secret.IsFailure)
            throw new InvalidOperationException(
                $"{SecretVariable} está definido, mas o segredo não pôde ser lido do Key Vault de testes ({secret.Error!.Code}).");
        return secret.Value.Value;
    }

    /// <summary>Primeiro valor: variável de ambiente, user-secrets e <c>appsettings.Local.json</c> (seção <see cref="Section"/>).</summary>
    private static string? Read(string variable, string key)
    {
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value))
            return value.Trim();
        foreach (var source in new[] { UserSecrets, LocalFile })
        {
            if (source?[$"{Section}:{key}"] is { } configured && !string.IsNullOrWhiteSpace(configured))
                return configured.Trim();
        }
        return null;
    }

    private static IConfiguration? Load(Action<IConfigurationBuilder> configure)
    {
        try
        {
            var builder = new ConfigurationBuilder();
            configure(builder);
            return builder.Build();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or IOException or InvalidDataException)
        {
            // Sem pasta de perfil (user-secrets) ou arquivo inválido: a fonte é ignorada, as demais continuam valendo
            return null;
        }
    }
}
