using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TEC.ORM.SqlServer.Internal;

namespace TEC.ORM.SqlServer.Security;

/// <summary>
/// Entrega ao EF Core a string de conexão do TEC.Vault no momento de abrir a conexão. O <c>DbContext</c> é configurado com
/// <c>UseSqlServer()</c> sem string de conexão: ela nunca passa pelas opções, pela configuração nem pelos logs do EF.
/// </summary>
internal sealed class SecretConnectionInterceptor(IOrmConnectionSecurity security) : DbConnectionInterceptor
{
    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        var configured = await security.ConfigureConnectionAsync(connection, OrmConnectionKind.ReadWrite, cancellationToken)
            .ConfigureAwait(false);
        if (configured.IsFailure)
            throw new OrmConnectionException(configured.Error!);
        return result;
    }

    /// <summary>
    /// Falha ao abrir (ex.: pool do SqlClient esgotado): marca a exceção para ser traduzida em <c>ORM_CONEXAO_INDISPONIVEL</c>.
    /// </summary>
    public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        MarkOpenFailure(connection, eventData);
        return Task.CompletedTask;
    }

    /// <inheritdoc cref="ConnectionFailedAsync" />
    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) =>
        MarkOpenFailure(connection, eventData);

    /// <summary>
    /// Só o esgotamento do pool na abertura (critério em <see cref="OrmExceptionTranslator.IsPoolExhaustion"/>): falhas de uma
    /// conexão já aberta e outras exceções seguem a tradução normal.
    /// </summary>
    private static void MarkOpenFailure(DbConnection connection, ConnectionErrorEventData eventData)
    {
        if (connection.State != System.Data.ConnectionState.Open
            && OrmExceptionTranslator.IsPoolExhaustion(eventData.Exception, eventData.Duration, connection.ConnectionTimeout))
            OrmExceptionTranslator.MarkConnectionOpenFailure(eventData.Exception);
    }

    /// <remarks>
    /// Caminho síncrono do EF Core (ex.: <c>SaveChanges()</c>, <c>ToList()</c>): bloqueia na leitura do segredo. Seguro em
    /// ASP.NET Core e workers (sem <c>SynchronizationContext</c>); prefira sempre as operações assíncronas.
    /// </remarks>
    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        var configured = security.ConfigureConnectionAsync(connection, OrmConnectionKind.ReadWrite, CancellationToken.None)
            .ConfigureAwait(false).GetAwaiter().GetResult();
        if (configured.IsFailure)
            throw new OrmConnectionException(configured.Error!);
        return result;
    }
}
