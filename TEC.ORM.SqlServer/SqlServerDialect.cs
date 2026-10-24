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
public sealed class SqlServerDialect : ISqlDialect
{
    /// <inheritdoc />
    public string ProviderName => "SqlServer";

    /// <inheritdoc />
    public string HealthCheckProbeQuery => "SELECT 1";

    /// <inheritdoc />
    public IReadOnlySqlValidator ReadOnlyValidator { get; } = new SqlServerReadOnlySqlValidator();

    /// <inheritdoc />
    public IOrmExceptionTranslator ExceptionTranslator { get; } = new SqlServerExceptionTranslatorAdapter();

    /// <inheritdoc />
    public string DelimitIdentifier(string identifier) =>
        "[" + (identifier ?? string.Empty).Replace("]", "]]") + "]";

    /// <inheritdoc />
    public string FormatAtomicIdempotencyInsert(string tableName) =>
        $"INSERT INTO {tableName} ([Scope], [IdempotencyKey], [RequestHash], [LockId], [State], [LockedUntil], [RetentionSeconds], [ExpiresAt], [CreatedAt]) " +
        "SELECT {0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8} " +
        $"WHERE NOT EXISTS (SELECT 1 FROM {tableName} WITH (UPDLOCK, HOLDLOCK) WHERE [Scope] = {{0}} AND [IdempotencyKey] = {{1}})";

    /// <inheritdoc />
    public DbConnection CreateConnection(ProtectedConnectionString connectionString) =>
        new SqlConnection(connectionString.Reveal());

    /// <inheritdoc />
    public Result<ProtectedConnectionString> SanitizeAndValidateConnectionString(
        string rawConnectionString,
        bool isReadOnly,
        bool allowTrustServerCertificate,
        string applicationName,
        int commandTimeoutSeconds)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(rawConnectionString);
        }
        catch (Exception)
        {
            return OrmErrors.InvalidConnectionSecret();
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
            return OrmErrors.InvalidInput("DataSource", "servidor não informado");
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
            return OrmErrors.InvalidInput("InitialCatalog", "banco de dados não informado");
        if (builder.Encrypt == SqlConnectionEncryptOption.Optional)
            return OrmErrors.InvalidInput("Encrypt", "criptografia desabilitada (Encrypt=False)");
        if (builder.TrustServerCertificate && !allowTrustServerCertificate)
            return OrmErrors.InvalidInput("TrustServerCertificate", "TrustServerCertificate=True não permitido");

        builder.PersistSecurityInfo = false;
        builder.ApplicationName = applicationName;
        builder.CommandTimeout = commandTimeoutSeconds;
        if (isReadOnly)
        {
            builder.ApplicationIntent = ApplicationIntent.ReadOnly;
            builder.Enlist = false;
        }

        return new ProtectedConnectionString(builder.ConnectionString);
    }
}

internal sealed class SqlServerReadOnlySqlValidator : IReadOnlySqlValidator
{
    public string? Check(string sql) => ReadOnlySqlGuard.Check(sql);
}

internal sealed class SqlServerExceptionTranslatorAdapter : IOrmExceptionTranslator
{
    public OrmDbExceptionTranslation Translate(Exception exception)
    {
        var tr = OrmExceptionTranslator.Translate(exception);
        return new OrmDbExceptionTranslation(tr.Error, tr.IsInfrastructure, tr.SqlErrorNumber.ToString());
    }

    public bool IsTransient(Exception exception) => OrmExceptionTranslator.IsTransient(exception);

    public bool IsConnectionFailure(Exception exception)
    {
        var tr = OrmExceptionTranslator.Translate(exception);
        return tr.IsInfrastructure && tr.Error.Code == OrmErrors.ConnectionUnavailable().Code;
    }
}
