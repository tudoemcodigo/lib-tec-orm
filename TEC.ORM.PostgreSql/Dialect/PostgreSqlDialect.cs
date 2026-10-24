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
public sealed class PostgreSqlDialect : ISqlDialect
{
    /// <inheritdoc />
    public string ProviderName => "PostgreSql";

    /// <inheritdoc />
    public string HealthCheckProbeQuery => "SELECT 1";

    /// <inheritdoc />
    public IReadOnlySqlValidator ReadOnlyValidator { get; } = new PostgreSqlReadOnlySqlValidator();

    /// <inheritdoc />
    public IOrmExceptionTranslator ExceptionTranslator { get; } = new PostgreSqlExceptionTranslator();

    /// <inheritdoc />
    public string DelimitIdentifier(string identifier) =>
        "\"" + (identifier ?? string.Empty).Replace("\"", "\"\"") + "\"";

    /// <inheritdoc />
    public string FormatAtomicIdempotencyInsert(string tableName) =>
        $"INSERT INTO {tableName} (\"Scope\", \"IdempotencyKey\", \"RequestHash\", \"LockId\", \"State\", \"LockedUntil\", \"RetentionSeconds\", \"ExpiresAt\", \"CreatedAt\") " +
        "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}) " +
        "ON CONFLICT (\"Scope\", \"IdempotencyKey\") DO NOTHING";

    /// <inheritdoc />
    public DbConnection CreateConnection(ProtectedConnectionString connectionString) =>
        new NpgsqlConnection(connectionString.Reveal());

    /// <inheritdoc />
    public Result<ProtectedConnectionString> SanitizeAndValidateConnectionString(
        string rawConnectionString,
        bool isReadOnly,
        bool allowTrustServerCertificate,
        string applicationName,
        int commandTimeoutSeconds)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(rawConnectionString);
        }
        catch (Exception)
        {
            return OrmErrors.InvalidInput("ConnectionString", "Formato inválido de connection string do PostgreSQL.");
        }

        if (string.IsNullOrWhiteSpace(builder.Host))
            return OrmErrors.InvalidInput("Host", "Servidor (Host) do PostgreSQL não informado no segredo.");

        if (string.IsNullOrWhiteSpace(builder.Database))
            return OrmErrors.InvalidInput("Database", "Banco de dados (Database) do PostgreSQL não informado no segredo.");

        // Disable/Allow/Prefer aceitam (ou voltam a) conexão sem TLS: a criptografia é obrigatória
        if (builder.SslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer)
            return OrmErrors.InvalidInput("SslMode", "Criptografia obrigatória (use SslMode=Require, VerifyCA ou VerifyFull).");

        // Require não valida o certificado do servidor: equivale ao TrustServerCertificate e exige o opt-in
        if (builder.SslMode == SslMode.Require && !allowTrustServerCertificate)
            return OrmErrors.InvalidInput("SslMode", "SslMode=Require não valida o certificado do servidor (use VerifyCA ou VerifyFull, ou habilite AllowTrustServerCertificate).");

        builder.PersistSecurityInfo = false;
        if (isReadOnly)
            builder.Enlist = false;   // fora do TransactionScope ambiente, como no SQL Server
        builder.ApplicationName = applicationName;
        builder.CommandTimeout = commandTimeoutSeconds;

        return new ProtectedConnectionString(builder.ConnectionString);
    }
}
