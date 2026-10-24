using System.Data.Common;
using Oracle.ManagedDataAccess.Client;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Security;

namespace TEC.ORM.Oracle.Dialect;

/// <summary>
/// Especialização do dialeto SQL e conexões para Oracle Database.
/// </summary>
public sealed class OracleDialect : ISqlDialect
{
    /// <inheritdoc />
    public string ProviderName => "Oracle";

    /// <inheritdoc />
    public string HealthCheckProbeQuery => "SELECT 1 FROM DUAL";

    /// <inheritdoc />
    public IReadOnlySqlValidator ReadOnlyValidator { get; } = new OracleReadOnlySqlValidator();

    /// <inheritdoc />
    public IOrmExceptionTranslator ExceptionTranslator { get; } = new OracleExceptionTranslator();

    /// <inheritdoc />
    public string DelimitIdentifier(string identifier) =>
        "\"" + (identifier ?? string.Empty).Replace("\"", "\"\"") + "\"";

    /// <inheritdoc />
    public string FormatAtomicIdempotencyInsert(string tableName) =>
        $"MERGE INTO {tableName} t " +
        "USING (SELECT {0} AS \"Scope\", {1} AS \"IdempotencyKey\" FROM DUAL) s " +
        "ON (t.\"Scope\" = s.\"Scope\" AND t.\"IdempotencyKey\" = s.\"IdempotencyKey\") " +
        "WHEN NOT MATCHED THEN " +
        "INSERT (\"Scope\", \"IdempotencyKey\", \"RequestHash\", \"LockId\", \"State\", \"LockedUntil\", \"RetentionSeconds\", \"ExpiresAt\", \"CreatedAt\") " +
        "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8})";

    /// <inheritdoc />
    public DbConnection CreateConnection(ProtectedConnectionString connectionString) =>
        new OracleConnection(connectionString.Reveal());

    /// <inheritdoc />
    public Result<ProtectedConnectionString> SanitizeAndValidateConnectionString(
        string rawConnectionString,
        bool isReadOnly,
        bool allowTrustServerCertificate,
        string applicationName,
        int commandTimeoutSeconds)
    {
        OracleConnectionStringBuilder builder;
        try
        {
            builder = new OracleConnectionStringBuilder(rawConnectionString);
        }
        catch (Exception)
        {
            return OrmErrors.InvalidInput("ConnectionString", "Formato inválido de connection string do Oracle.");
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
            return OrmErrors.InvalidInput("DataSource", "Data Source do Oracle não informado no segredo.");

        if (string.IsNullOrWhiteSpace(builder.UserID))
            return OrmErrors.InvalidInput("UserID", "Usuário do Oracle não informado no segredo.");

        // Sem TLS na descrição do endereço (PROTOCOL=TCP ou tcp:): senha e dados trafegam em claro. Alias do tnsnames/wallet
        // (sem protocolo na string) não é inspecionável aqui: a política de TLS do alias é de quem o mantém
        string dataSource = builder.DataSource.Replace(" ", "", StringComparison.Ordinal);
        bool plainTcp = dataSource.Contains("PROTOCOL=TCP)", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase);
        if (plainTcp && !allowTrustServerCertificate)
            return OrmErrors.InvalidInput("DataSource", "Conexão Oracle sem criptografia (PROTOCOL=TCP): use TCPS ou habilite AllowTrustServerCertificate só em desenvolvimento.");

        builder.PersistSecurityInfo = false;
        builder.ConnectionTimeout = commandTimeoutSeconds;

        return new ProtectedConnectionString(builder.ConnectionString);
    }
}
