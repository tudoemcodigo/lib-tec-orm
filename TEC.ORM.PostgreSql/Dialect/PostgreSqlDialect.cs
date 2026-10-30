using System.Data.Common;
using Npgsql;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Security;

namespace TEC.ORM.PostgreSql.Dialect;

/// <summary>
/// Especialização do dialeto SQL e conexões para PostgreSQL.
/// </summary>
/// <remarks>
/// <para>Política do segredo: <c>Host</c> e <c>Database</c> obrigatórios; <c>SslMode</c> <c>Disable</c>, <c>Allow</c> e
/// <c>Prefer</c> são recusados (aceitam ou voltam a conexão sem TLS); <c>Require</c> só com
/// <c>OrmOptions.AllowTrustServerCertificate</c>: desde o Npgsql 6 ele cifra mas <b>não valida</b> o certificado do servidor (no
/// Npgsql 8 deixou de exigir <c>Trust Server Certificate</c>, que ficou obsoleto); só <c>VerifyCA</c> e <c>VerifyFull</c>
/// validam. Sempre impostos: <c>Persist Security Info=False</c>, <c>Include Error Detail=False</c> (o detalhe do erro repete
/// valores de linhas), <c>Application Name</c> e <c>Command Timeout</c>; a conexão de leitura recebe <c>Enlist=false</c>.</para>
/// <para>As leituras complexas rodam numa transação <c>READ ONLY</c> (<c>SET TRANSACTION READ ONLY</c>).</para>
/// </remarks>
public sealed class PostgreSqlDialect : ISqlDialect
{
    /// <inheritdoc />
    public string ProviderName => "PostgreSql";

    /// <inheritdoc />
    public string DbSystemName => "postgresql";

    /// <inheritdoc />
    public string HealthCheckProbeQuery => "SELECT 1";

    /// <inheritdoc />
    public IReadOnlySqlValidator ReadOnlyValidator { get; } = new PostgreSqlReadOnlySqlValidator();

    /// <inheritdoc />
    public IOrmExceptionTranslator ExceptionTranslator { get; } = new PostgreSqlExceptionTranslator();

    /// <inheritdoc />
    public string DelimitIdentifier(string identifier) =>
        "\"" + (identifier ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <inheritdoc />
    public string FormatAtomicIdempotencyInsert(string tableName) =>
        FormatAtomicIdempotencyInsert(tableName, new IdempotencyInsertColumns("\"Scope\"", "\"IdempotencyKey\"", "\"RequestHash\"",
            "\"LockId\"", "\"State\"", "\"LockedUntil\"", "\"RetentionSeconds\"", "\"ExpiresAt\"", "\"CreatedAt\""));

    /// <inheritdoc />
    /// <remarks><c>INSERT ... ON CONFLICT (escopo, chave) DO NOTHING</c>: a chave existente não gera erro (nem aborta a transação).</remarks>
    public string FormatAtomicIdempotencyInsert(string tableName, IdempotencyInsertColumns columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return $"INSERT INTO {tableName} ({columns.Scope}, {columns.Key}, {columns.RequestHash}, {columns.LockId}, {columns.State}, " +
               $"{columns.LockedUntil}, {columns.RetentionSeconds}, {columns.ExpiresAt}, {columns.CreatedAt}) " +
               "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}) " +
               $"ON CONFLICT ({columns.Scope}, {columns.Key}) DO NOTHING";
    }

    /// <inheritdoc />
    public DbConnection CreateConnection(ProtectedConnectionString connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        return new NpgsqlConnection(connectionString.Reveal());
    }

    /// <inheritdoc />
    /// <remarks>O EF Core cria a conexão administrativa (<c>postgres</c>) a partir da principal, que sem string fica só com o banco.</remarks>
    public bool TryGetDerivedDatabase(string connectionString, out string? database)
    {
        database = null;
        if (!TryParse(connectionString, out var builder) || !string.IsNullOrWhiteSpace(builder.Host))
            return false;
        database = builder.Database;
        return true;
    }

    /// <inheritdoc />
    public bool IsServerConnection(DbConnection connection) =>
        connection is not null && connection.Database is { } database &&
        (database.Equals("postgres", StringComparison.Ordinal) || database.Equals("template1", StringComparison.Ordinal));

    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable?> BeginReadOnlyTransactionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.Transaction = transaction;
                command.CommandText = "SET TRANSACTION READ ONLY";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
        if (string.IsNullOrWhiteSpace(builder.Host))
            return Reason("servidor (Host) não informado");
        if (string.IsNullOrWhiteSpace(databaseOverride) && string.IsNullOrWhiteSpace(builder.Database))
            return Reason("banco de dados não informado");
        // Disable/Allow/Prefer aceitam (ou voltam a) conexão sem TLS: a criptografia é obrigatória
        if (builder.SslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer)
            return Reason("criptografia desabilitada ou opcional (use SslMode=VerifyFull ou VerifyCA)");
        // Require cifra mas não valida o certificado do servidor (Npgsql 6+): equivale ao TrustServerCertificate e exige o opt-in
        bool relaxed = builder.SslMode == SslMode.Require;
        if (relaxed && !allowTrustServerCertificate)
            return Reason("SslMode=Require não valida o certificado do servidor (use VerifyFull ou VerifyCA, ou habilite AllowTrustServerCertificate)");

        if (!string.IsNullOrWhiteSpace(databaseOverride))
            builder.Database = databaseOverride;
        builder.PersistSecurityInfo = false;
        builder.IncludeErrorDetail = false;
        if (isReadOnly)
            builder.Enlist = false;   // fora do TransactionScope ambiente, como no SQL Server
        builder.ApplicationName = applicationName;
        builder.CommandTimeout = commandTimeoutSeconds;

        return new ProtectedConnectionString(builder.ConnectionString) { RelaxedTransportSecurity = relaxed };
    }

    private static bool TryParse(string connectionString, out NpgsqlConnectionStringBuilder builder)
    {
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or KeyNotFoundException
                                              or InvalidOperationException or InvalidCastException or OverflowException)
        {
            builder = null!;
            return false;
        }
    }

    private static Error Reason(string reason) => OrmErrors.InvalidInput("ConnectionString", reason);
}
