using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TEC.ORM.EntityFrameworkCore.Security;

internal sealed class SecretConnectionInterceptor(IOrmConnectionSecurity security, OrmConnectionCircuit? circuit = null) : DbConnectionInterceptor
{
    public OrmConnectionCircuit? Circuit => circuit;

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        var configured = await security.ConfigureConnectionAsync(connection, OrmConnectionKind.ReadWrite, cancellationToken)
            .ConfigureAwait(false);
        if (configured.IsFailure)
            throw new OrmConnectionException(configured.Error!);
        if (circuit is null || result.IsSuppressed || IsServerConnection(connection))
            return result;

        await circuit.OpenAsync(connection, cancellationToken).ConfigureAwait(false);
        return InterceptionResult.Suppress();
    }

    private static bool IsServerConnection(DbConnection connection) =>
        string.Equals(connection.Database, "master", StringComparison.OrdinalIgnoreCase);

    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        var configured = security.ConfigureConnectionAsync(connection, OrmConnectionKind.ReadWrite, CancellationToken.None)
            .ConfigureAwait(false).GetAwaiter().GetResult();
        if (configured.IsFailure)
            throw new OrmConnectionException(configured.Error!);
        if (circuit is null || result.IsSuppressed || IsServerConnection(connection))
            return result;

        circuit.Open(connection);
        return InterceptionResult.Suppress();
    }
}
