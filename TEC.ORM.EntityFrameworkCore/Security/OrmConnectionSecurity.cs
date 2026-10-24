using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using TEC.Core.Common.Guards;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Internal;
using TEC.Vault.Abstractions;

namespace TEC.ORM.EntityFrameworkCore.Security;

/// <summary>
/// Implementação agnóstica de gerenciamento seguro de conexões com suporte a múltiplos dialetos e Circuit Breaker.
/// </summary>
public sealed class OrmConnectionSecurity(
    ISecretReader secrets,
    OrmOptions options,
    ILogger<OrmConnectionSecurity> logger,
    ISqlDialect? dialect = null,
    TimeProvider? time = null)
    : IOrmConnectionSecurity
{
    private readonly ISqlDialect _dialect = dialect ?? options.Dialect ?? throw new ArgumentException("Dialeto de banco de dados não informado nas opções.", nameof(dialect));
    private readonly OrmConnectionCircuit?[] _circuits =
    [
        OrmConnectionCircuit.Create(options, OrmConnectionKind.ReadWrite, time, logger, dialect ?? options.Dialect),
        OrmConnectionCircuit.Create(options, OrmConnectionKind.ReadOnly, time, logger, dialect ?? options.Dialect)
    ];

    private int _trustWarnings;

    /// <inheritdoc />
    public async Task<Result<DbConnection>> OpenConnectionAsync(OrmConnectionKind kind, CancellationToken cancellationToken)
    {
        var connectionString = await BuildConnectionStringAsync(kind, cancellationToken).ConfigureAwait(false);
        if (connectionString.IsFailure)
            return connectionString.ToFailure<DbConnection>();

        var connection = _dialect.CreateConnection(connectionString.Value);
        try
        {
            if (_circuits[(int)kind] is { } circuit)
                await circuit.OpenAsync(connection, cancellationToken).ConfigureAwait(false);
            else
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (OrmConnectionException exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return exception.Error;
        }
        catch (Exception exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            // Sem a mensagem: ela pode citar servidor, banco e usuário. Só o tipo da exceção
            OrmLog.ConnectionOpenFailed(logger, KindName(kind), exception.GetType().Name);
            return OrmErrors.ConnectionUnavailable();
        }
    }

    /// <inheritdoc />
    public async Task<Result> ConfigureConnectionAsync(DbConnection connection, OrmConnectionKind kind, CancellationToken cancellationToken)
    {
        Guard.NotNull(connection);
        if (connection.State != ConnectionState.Closed)
            return Result.Success();

        if (!string.IsNullOrEmpty(connection.ConnectionString))
            return Result.Success();

        var connectionString = await BuildConnectionStringAsync(kind, cancellationToken).ConfigureAwait(false);
        if (connectionString.IsFailure)
            return connectionString.ToFailure();

        connection.ConnectionString = connectionString.Value.Reveal();
        return Result.Success();
    }

    internal async Task<Result<ProtectedConnectionString>> BuildConnectionStringAsync(OrmConnectionKind kind, CancellationToken cancellationToken)
    {
        string kindName = KindName(kind);
        var secret = await secrets.GetSecretAsync(options.SecretNameFor(kind == OrmConnectionKind.ReadOnly), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (secret.IsFailure)
        {
            OrmLog.SecretUnavailable(logger, kindName, secret.Error!.Code);
            return OrmErrors.ConnectionUnavailable();
        }

        var sanitized = _dialect.SanitizeAndValidateConnectionString(
            secret.Value.Value,
            isReadOnly: kind == OrmConnectionKind.ReadOnly,
            allowTrustServerCertificate: options.AllowTrustServerCertificate,
            applicationName: options.ApplicationName,
            commandTimeoutSeconds: options.CommandTimeoutSeconds);

        if (sanitized.IsFailure)
        {
            OrmLog.SecretRejected(logger, kindName, sanitized.Error!.Message);
            return sanitized.ToFailure<ProtectedConnectionString>();
        }

        if (options.AllowTrustServerCertificate)
        {
            int bit = 1 << (int)kind;
            if ((Interlocked.Or(ref _trustWarnings, bit) & bit) == 0)
                OrmLog.TrustServerCertificateEnabled(logger, kindName);
        }

        return sanitized;
    }

    private static string KindName(OrmConnectionKind kind) => kind == OrmConnectionKind.ReadOnly ? "leitura" : "escrita";
}
