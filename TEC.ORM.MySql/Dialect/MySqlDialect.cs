using System.Data.Common;
using MySqlConnector;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Security;

namespace TEC.ORM.MySql.Dialect;

/// <summary>
/// Especialização do dialeto SQL e conexões para MySQL / MariaDB.
/// </summary>
public sealed class MySqlDialect : ISqlDialect
{
    /// <inheritdoc />
    public string ProviderName => "MySql";

    /// <inheritdoc />
    public string HealthCheckProbeQuery => "SELECT 1";

    /// <inheritdoc />
    public IReadOnlySqlValidator ReadOnlyValidator { get; } = new MySqlReadOnlySqlValidator();

    /// <inheritdoc />
    public IOrmExceptionTranslator ExceptionTranslator { get; } = new MySqlExceptionTranslator();

    /// <inheritdoc />
    public string DelimitIdentifier(string identifier) =>
        "`" + (identifier ?? string.Empty).Replace("`", "``") + "`";

    /// <inheritdoc />
    public string FormatAtomicIdempotencyInsert(string tableName) =>
        $"INSERT IGNORE INTO {tableName} (`Scope`, `IdempotencyKey`, `RequestHash`, `LockId`, `State`, `LockedUntil`, `RetentionSeconds`, `ExpiresAt`, `CreatedAt`) " +
        "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8})";

    /// <inheritdoc />
    public DbConnection CreateConnection(ProtectedConnectionString connectionString) =>
        new MySqlConnection(connectionString.Reveal());

    /// <inheritdoc />
    public Result<ProtectedConnectionString> SanitizeAndValidateConnectionString(
        string rawConnectionString,
        bool isReadOnly,
        bool allowTrustServerCertificate,
        string applicationName,
        int commandTimeoutSeconds)
    {
        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(rawConnectionString);
        }
        catch (Exception)
        {
            return OrmErrors.InvalidInput("ConnectionString", "Formato inválido de connection string do MySQL.");
        }

        if (string.IsNullOrWhiteSpace(builder.Server))
            return OrmErrors.InvalidInput("Server", "Servidor (Server) do MySQL não informado no segredo.");

        if (string.IsNullOrWhiteSpace(builder.Database))
            return OrmErrors.InvalidInput("Database", "Banco de dados (Database) do MySQL não informado no segredo.");

        if (builder.SslMode == MySqlSslMode.None)
            return OrmErrors.InvalidInput("SslMode", "Criptografia desabilitada (SslMode=None não permitido).");

        if (builder.SslMode == MySqlSslMode.Preferred && !allowTrustServerCertificate)
            return OrmErrors.InvalidInput("SslMode", "SslMode=Preferred exige certificado confiável em produção (use Required ou VerifyCA).");

        builder.PersistSecurityInfo = false;
        builder.ApplicationName = applicationName;
        builder.DefaultCommandTimeout = (uint)commandTimeoutSeconds;

        return new ProtectedConnectionString(builder.ConnectionString);
    }
}
