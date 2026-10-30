using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.EntityFrameworkCore.Security;

/// <summary>
/// Entrega ao EF Core a string de conexão do TEC.Vault no momento de abrir a conexão. O <c>DbContext</c> é configurado sem
/// string de conexão (<c>UseSqlServer()</c>, <c>UseNpgsql()</c>...): ela nunca passa pelas opções, pela configuração nem pelos
/// logs do EF.
/// </summary>
/// <remarks>
/// Com o circuit breaker ligado (<c>OrmOptions.CircuitBreaker</c>) o próprio interceptor abre a conexão pelo circuito e
/// suprime a abertura do EF Core; falhas continuam passando pelo <c>ConnectionFailed</c> do EF (marca de esgotamento do pool).
/// Com o circuito aberto a abertura lança <see cref="OrmConnectionException"/> na hora.
/// </remarks>
internal sealed class SecretConnectionInterceptor(IOrmConnectionSecurity security, OrmConnectionCircuit? circuit = null,
    ISqlDialect? dialect = null) : DbConnectionInterceptor
{
    /// <summary>Circuito da conexão de leitura e escrita deste contexto (<c>null</c> = desligado).</summary>
    internal OrmConnectionCircuit? Circuit => circuit;

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
        if (circuit is null || result.IsSuppressed || IsServerConnection(connection))
            return result;

        circuit.Open(connection);
        return InterceptionResult.Suppress();
    }

    /// <summary>
    /// Falha ao abrir (ex.: pool esgotado): marca a exceção para ser traduzida em <c>ORM_CONEXAO_INDISPONIVEL</c>.
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
    /// Conexão administrativa derivada pelo EF Core para criar ou apagar o banco (<c>EnsureCreated</c>, <c>Migrate</c>): passa
    /// fora do circuito, para operações de criação não abrirem nem serem barradas pelo circuito do banco da aplicação.
    /// </summary>
    private bool IsServerConnection(DbConnection connection) => dialect?.IsServerConnection(connection) == true;

    /// <summary>
    /// Só o esgotamento do pool na abertura (critério em <see cref="OrmConnectionFailures.IsPoolExhaustion"/>): falhas de uma
    /// conexão já aberta e outras exceções seguem a tradução normal.
    /// </summary>
    private static void MarkOpenFailure(DbConnection connection, ConnectionErrorEventData eventData)
    {
        if (connection.State != System.Data.ConnectionState.Open
            && OrmConnectionFailures.IsPoolExhaustion(eventData.Exception, eventData.Duration, connection.ConnectionTimeout))
            OrmConnectionFailures.Mark(eventData.Exception);
    }
}
