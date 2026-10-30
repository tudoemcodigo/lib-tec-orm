using System.Data.Common;
using Microsoft.Data.SqlClient;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Security;
using TEC.ORM.SqlServer.Internal;

namespace TEC.ORM.SqlServer;

/// <summary>
/// Especialização do dialeto SQL e conexões para Microsoft SQL Server / Azure SQL.
/// </summary>
/// <remarks>
/// <para>Política aplicada ao segredo (recusado com <c>ORM_CONEXAO_INVALIDA</c> se não atender):</para>
/// <list type="bullet">
/// <item><description>formato válido de string de conexão do SQL Server, com servidor e banco;</description></item>
/// <item><description>criptografia obrigatória (<c>Encrypt=False</c>/<c>Optional</c> é recusado);</description></item>
/// <item><description><c>TrustServerCertificate=True</c> só com <c>OrmOptions.AllowTrustServerCertificate</c>.</description></item>
/// </list>
/// <para>Sempre impostos: <c>Persist Security Info=False</c> (a senha some de <c>ConnectionString</c> depois de aberta),
/// <c>Application Name</c> e <c>Command Timeout</c> das opções. A conexão de leitura recebe <c>ApplicationIntent=ReadOnly</c>
/// e <c>Enlist=false</c>. O SQL Server não tem transação somente leitura: a proteção das leituras complexas é o login de leitura
/// só com <c>SELECT</c> (e a verificação T-SQL, como segunda barreira).</para>
/// </remarks>
public sealed class SqlServerDialect : ISqlDialect
{
    /// <inheritdoc />
    public string ProviderName => "SqlServer";

    /// <inheritdoc />
    public string DbSystemName => "microsoft.sql_server";

    /// <inheritdoc />
    public string HealthCheckProbeQuery => "SELECT 1";

    /// <inheritdoc />
    public IReadOnlySqlValidator ReadOnlyValidator { get; } = new SqlServerReadOnlySqlValidator();

    /// <inheritdoc />
    public IOrmExceptionTranslator ExceptionTranslator { get; } = new SqlServerExceptionTranslator();

    /// <inheritdoc />
    public string DelimitIdentifier(string identifier) =>
        "[" + (identifier ?? string.Empty).Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <inheritdoc />
    public string FormatAtomicIdempotencyInsert(string tableName) =>
        FormatAtomicIdempotencyInsert(tableName, new IdempotencyInsertColumns("[Scope]", "[IdempotencyKey]", "[RequestHash]", "[LockId]",
            "[State]", "[LockedUntil]", "[RetentionSeconds]", "[ExpiresAt]", "[CreatedAt]"));

    /// <inheritdoc />
    /// <remarks><c>INSERT ... SELECT ... WHERE NOT EXISTS</c> com <c>UPDLOCK, HOLDLOCK</c>: só uma requisição reserva a chave.</remarks>
    public string FormatAtomicIdempotencyInsert(string tableName, IdempotencyInsertColumns columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return $"INSERT INTO {tableName} ({columns.Scope}, {columns.Key}, {columns.RequestHash}, {columns.LockId}, {columns.State}, " +
               $"{columns.LockedUntil}, {columns.RetentionSeconds}, {columns.ExpiresAt}, {columns.CreatedAt}) " +
               "SELECT {0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8} " +
               $"WHERE NOT EXISTS (SELECT 1 FROM {tableName} WITH (UPDLOCK, HOLDLOCK) WHERE {columns.Scope} = {{0}} AND {columns.Key} = {{1}})";
    }

    /// <inheritdoc />
    public DbConnection CreateConnection(ProtectedConnectionString connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        return new SqlConnection(connectionString.Reveal());
    }

    /// <inheritdoc />
    /// <remarks>O EF Core deriva a conexão com <c>master</c> da principal (que, sem string de conexão, fica só com o banco).</remarks>
    public bool TryGetDerivedDatabase(string connectionString, out string? database)
    {
        database = null;
        if (!TryParse(connectionString, out var builder) || !string.IsNullOrWhiteSpace(builder.DataSource))
            return false;
        database = builder.InitialCatalog;
        return true;
    }

    /// <inheritdoc />
    public bool IsServerConnection(DbConnection connection) =>
        connection is not null && string.Equals(connection.Database, "master", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Result<ProtectedConnectionString> SanitizeAndValidateConnectionString(string rawConnectionString, bool isReadOnly,
        bool allowTrustServerCertificate, string applicationName, int commandTimeoutSeconds) =>
        SanitizeAndValidateConnectionString(rawConnectionString, isReadOnly, allowTrustServerCertificate, applicationName,
            commandTimeoutSeconds, databaseOverride: null);

    /// <inheritdoc />
    public Result<ProtectedConnectionString> SanitizeAndValidateConnectionString(string rawConnectionString, bool isReadOnly,
        bool allowTrustServerCertificate, string applicationName, int commandTimeoutSeconds, string? databaseOverride)
    {
        // Nunca expor a exceção do parse: a mensagem do SqlClient pode reproduzir trechos do texto (inclusive a senha)
        if (!TryParse(rawConnectionString, out var builder))
            return Reason("formato inválido");
        if (string.IsNullOrWhiteSpace(builder.DataSource))
            return Reason("servidor não informado");
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
            return Reason("banco de dados não informado");
        if (builder.Encrypt == SqlConnectionEncryptOption.Optional)
            return Reason("criptografia desabilitada (Encrypt=False)");
        if (builder.TrustServerCertificate && !allowTrustServerCertificate)
            return Reason("TrustServerCertificate=True não permitido");

        if (!string.IsNullOrWhiteSpace(databaseOverride))
            builder.InitialCatalog = databaseOverride;
        builder.PersistSecurityInfo = false;
        builder.ApplicationName = applicationName;
        builder.CommandTimeout = commandTimeoutSeconds;
        if (isReadOnly)
        {
            builder.ApplicationIntent = ApplicationIntent.ReadOnly;
            // Nunca entra no TransactionScope ambiente: junto com a conexão do EF Core (outra string), a transação seria promovida
            // a distribuída (falha no Linux e, sem ImplicitDistributedTransactions, no Windows). Fica fora de transação, como
            // documentado, e a nova tentativa das leituras complexas continua segura.
            builder.Enlist = false;
        }

        return new ProtectedConnectionString(builder.ConnectionString) { RelaxedTransportSecurity = builder.TrustServerCertificate };
    }

    private static bool TryParse(string connectionString, out SqlConnectionStringBuilder builder)
    {
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or KeyNotFoundException
                                              or InvalidOperationException)
        {
            builder = null!;
            return false;
        }
    }

    private static Error Reason(string reason) => OrmErrors.InvalidInput("ConnectionString", reason);
}

/// <summary>Verificação de somente leitura do T-SQL (<see cref="ReadOnlySqlGuard"/>).</summary>
internal sealed class SqlServerReadOnlySqlValidator : IReadOnlySqlValidator
{
    public string? Check(string sql) => ReadOnlySqlGuard.Check(sql);
}
