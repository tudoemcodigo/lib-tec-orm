using System.Data.Common;
using MySql.Data.MySqlClient;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Security;

namespace TEC.ORM.MySql.Dialect;

/// <summary>
/// Especialização do dialeto SQL e conexões para MySQL / MariaDB, com o driver MySql.Data (o mesmo do
/// <c>MySql.EntityFrameworkCore</c>).
/// </summary>
/// <remarks>
/// <para>Política do segredo: <c>Server</c> e <c>Database</c> obrigatórios; <c>SslMode=Disabled</c> (ou <c>None</c>) recusado;
/// <c>Preferred</c> e <c>Required</c> só com <c>OrmOptions.AllowTrustServerCertificate</c> (não validam o certificado do servidor;
/// <c>Preferred</c> ainda volta a conexão sem TLS); <c>VerifyCA</c> e <c>VerifyFull</c> passam. Sempre impostos:
/// <c>Persist Security Info=False</c>, <c>Allow Load Local Infile=False</c> (um servidor falso não lê arquivos do cliente) e
/// <c>Default Command Timeout</c>; a conexão de leitura recebe <c>Auto Enlist=False</c> e <c>Connection Reset=True</c>. O
/// MySql.Data não tem <c>Application Name</c>: <c>OrmOptions.ApplicationName</c> não é aplicado.</para>
/// <para>As leituras complexas rodam numa transação <c>START TRANSACTION READ ONLY</c>.</para>
/// </remarks>
public sealed class MySqlDialect : ISqlDialect
{
    /// <inheritdoc />
    public string ProviderName => "MySql";

    /// <inheritdoc />
    public string DbSystemName => "mysql";

    /// <inheritdoc />
    public string HealthCheckProbeQuery => "SELECT 1";

    /// <inheritdoc />
    public IReadOnlySqlValidator ReadOnlyValidator { get; } = new MySqlReadOnlySqlValidator();

    /// <inheritdoc />
    public IOrmExceptionTranslator ExceptionTranslator { get; } = new MySqlExceptionTranslator();

    /// <inheritdoc />
    public string DelimitIdentifier(string identifier) =>
        "`" + (identifier ?? string.Empty).Replace("`", "``", StringComparison.Ordinal) + "`";

    /// <inheritdoc />
    public string FormatAtomicIdempotencyInsert(string tableName) =>
        FormatAtomicIdempotencyInsert(tableName, new IdempotencyInsertColumns("`Scope`", "`IdempotencyKey`", "`RequestHash`", "`LockId`",
            "`State`", "`LockedUntil`", "`RetentionSeconds`", "`ExpiresAt`", "`CreatedAt`"));

    /// <inheritdoc />
    /// <remarks>
    /// <c>INSERT</c> simples: a chave existente é a violação de chave primária (1062), tratada como "já existe" pelo store.
    /// Sem <c>INSERT IGNORE</c>, que também transformaria em aviso outros erros de dados (valor cortado, tipo inválido).
    /// </remarks>
    public string FormatAtomicIdempotencyInsert(string tableName, IdempotencyInsertColumns columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return $"INSERT INTO {tableName} ({columns.Scope}, {columns.Key}, {columns.RequestHash}, {columns.LockId}, {columns.State}, " +
               $"{columns.LockedUntil}, {columns.RetentionSeconds}, {columns.ExpiresAt}, {columns.CreatedAt}) " +
               "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8})";
    }

    /// <inheritdoc />
    public DbConnection CreateConnection(ProtectedConnectionString connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        return new MySqlConnection(connectionString.Reveal());
    }

    /// <inheritdoc />
    /// <remarks>
    /// O MySql.Data não tem transação somente leitura na API: a transação é aberta e encerrada por SQL
    /// (<c>START TRANSACTION READ ONLY</c> / <c>ROLLBACK</c>). A conexão de leitura tem <c>Connection Reset=True</c>, então o pool
    /// limpa a sessão mesmo se o <c>ROLLBACK</c> falhar.
    /// </remarks>
    public async ValueTask<IAsyncDisposable?> BeginReadOnlyTransactionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await ExecuteAsync(connection, "START TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
        return new ReadOnlyTransaction(connection);
    }

    /// <inheritdoc />
    public Result<ProtectedConnectionString> SanitizeAndValidateConnectionString(string rawConnectionString, bool isReadOnly,
        bool allowTrustServerCertificate, string applicationName, int commandTimeoutSeconds) =>
        SanitizeAndValidateConnectionString(rawConnectionString, isReadOnly, allowTrustServerCertificate, applicationName,
            commandTimeoutSeconds, databaseOverride: null);

    /// <inheritdoc />
    public Result<ProtectedConnectionString> SanitizeAndValidateConnectionString(string rawConnectionString, bool isReadOnly,
        bool allowTrustServerCertificate, string applicationName, int commandTimeoutSeconds, string? databaseOverride)
    {
        if (!TryParse(rawConnectionString, out var builder))
            return Reason("formato inválido");
        if (string.IsNullOrWhiteSpace(builder.Server))
            return Reason("servidor (Server) não informado");
        if (string.IsNullOrWhiteSpace(databaseOverride) && string.IsNullOrWhiteSpace(builder.Database))
            return Reason("banco de dados não informado");
        if (builder.SslMode == MySqlSslMode.Disabled)
            return Reason("criptografia desabilitada (SslMode=Disabled/None)");
        // Preferred volta a conexão sem TLS; Required não valida o certificado (equivale ao TrustServerCertificate)
        bool relaxed = builder.SslMode is MySqlSslMode.Preferred or MySqlSslMode.Required;
        if (relaxed && !allowTrustServerCertificate)
            return Reason("SslMode=Preferred/Required não valida o certificado do servidor (use VerifyFull ou VerifyCA, ou habilite AllowTrustServerCertificate)");

        if (!string.IsNullOrWhiteSpace(databaseOverride))
            builder.Database = databaseOverride;
        builder.PersistSecurityInfo = false;
        builder.AllowLoadLocalInfile = false;
        builder.DefaultCommandTimeout = (uint)commandTimeoutSeconds;
        if (isReadOnly)
        {
            builder.AutoEnlist = false;        // fora do TransactionScope ambiente, como no SQL Server
            builder.ConnectionReset = true;    // a transação somente leitura nunca sobra numa conexão do pool
        }

        return new ProtectedConnectionString(builder.ConnectionString) { RelaxedTransportSecurity = relaxed };
    }

    private static bool TryParse(string connectionString, out MySqlConnectionStringBuilder builder)
    {
        try
        {
            builder = new MySqlConnectionStringBuilder(connectionString);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or KeyNotFoundException
                                              or InvalidOperationException or InvalidCastException or OverflowException)
        {
            builder = null!;
            return false;
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // texto fixo deste dialeto (início/fim da transação somente leitura)
            command.CommandText = sql;
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static Error Reason(string reason) => OrmErrors.InvalidInput("ConnectionString", reason);

    /// <summary>Transação somente leitura aberta por SQL: o descarte a encerra com <c>ROLLBACK</c>.</summary>
    private sealed class ReadOnlyTransaction(DbConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (connection.State == System.Data.ConnectionState.Open)
                await ExecuteAsync(connection, "ROLLBACK", CancellationToken.None).ConfigureAwait(false);
        }
    }
}
