using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TEC.Core.Common.Guards;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Internal;
using TEC.Vault.Abstractions;

namespace TEC.ORM.EntityFrameworkCore.Security;

/// <inheritdoc cref="IOrmConnectionSecurity" />
/// <remarks>
/// <para>A política aplicada ao segredo é a do dialeto (<see cref="ISqlDialect.SanitizeAndValidateConnectionString(string, bool, bool, string, int, string?)"/>):
/// formato válido, servidor e banco informados, criptografia obrigatória e certificado validado (o relaxamento só com
/// <see cref="OrmOptions.AllowTrustServerCertificate"/>, com aviso 3103 uma vez por tipo de conexão). Recusa vira
/// <c>ORM_CONEXAO_INVALIDA</c>, com o motivo no log 3101 e nunca trechos da string.</para>
/// <para>Para rotação de senha sem reiniciar: o segredo é lido a cada nova conexão do EF Core (uma por <c>DbContext</c>) e do
/// Dapper; habilite o cache do TEC.Vault (<c>EnableSecretCache</c>) para não consultar o cofre a cada abertura.</para>
/// </remarks>
/// <param name="secrets">Leitor de segredos do TEC.Vault.</param>
/// <param name="options">Opções do contexto.</param>
/// <param name="logger">Log (nunca recebe a string de conexão).</param>
/// <param name="dialect">Dialeto; <c>null</c> usa <see cref="OrmOptions.Dialect"/>.</param>
/// <param name="time">Relógio do circuit breaker (<see cref="OrmOptions.CircuitBreaker"/>). Padrão: <see cref="TimeProvider.System"/>.</param>
public sealed class OrmConnectionSecurity(
    ISecretReader secrets,
    OrmOptions options,
    ILogger<OrmConnectionSecurity> logger,
    ISqlDialect? dialect = null,
    TimeProvider? time = null)
    : IOrmConnectionSecurity
{
    private readonly ISqlDialect _dialect = dialect ?? options.Dialect
        ?? throw new ArgumentException("Dialeto de banco de dados não informado nas opções.", nameof(dialect));

    // Um circuito por tipo de conexão (OpenConnectionAsync: leituras complexas do Dapper e health check)
    private readonly OrmConnectionCircuit?[] _circuits =
    [
        OrmConnectionCircuit.Create(options, OrmConnectionKind.ReadWrite, time, logger, dialect ?? options.Dialect),
        OrmConnectionCircuit.Create(options, OrmConnectionKind.ReadOnly, time, logger, dialect ?? options.Dialect)
    ];

    // Bits por OrmConnectionKind: o aviso de certificado não validado sai uma vez por tipo de conexão, não a cada abertura
    private int _trustWarnings;

    /// <inheritdoc />
    /// <remarks>
    /// Cancelamento pelo chamador é relançado (não vira indisponibilidade). Erro do banco na abertura e pool esgotado viram
    /// <c>ORM_CONEXAO_INDISPONIVEL</c> (ou <c>ORM_TEMPO_ESGOTADO</c>), registrados só com o tipo e o código do erro; outras
    /// exceções são erro de programação e sobem.
    /// </remarks>
    public async Task<Result<DbConnection>> OpenConnectionAsync(OrmConnectionKind kind, CancellationToken cancellationToken)
    {
        var connectionString = await BuildConnectionStringAsync(kind, cancellationToken).ConfigureAwait(false);
        if (connectionString.IsFailure)
            return connectionString.ToFailure<DbConnection>();

        var connection = _dialect.CreateConnection(connectionString.Value);
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (_circuits[(int)kind] is { } circuit)
                await circuit.OpenAsync(connection, cancellationToken).ConfigureAwait(false);
            else
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelado pelo chamador: não é indisponibilidade do banco (nem conta como tal no log)
            await connection.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OrmConnectionException exception)
        {
            // Circuito aberto: recusada sem tocar no banco
            await connection.DisposeAsync().ConfigureAwait(false);
            return exception.Error;
        }
        catch (DbException exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            // Sem a exceção no log: a mensagem pode citar servidor, banco e usuário
            var translation = _dialect.ExceptionTranslator.Translate(exception);
            OrmLog.ConnectionOpenFailed(logger, KindName(kind), exception.GetType().Name, translation.ErrorCode ?? "-");
            return translation.IsInfrastructure && translation.Error.Code != OrmErrors.FailureCode
                ? translation.Error
                : OrmErrors.ConnectionUnavailable();
        }
        catch (InvalidOperationException exception) when (OrmConnectionFailures.IsPoolExhaustion(
            exception, Stopwatch.GetElapsedTime(started), connection.ConnectionTimeout))
        {
            // Pool esgotado (todas as conexões em uso até o Connect Timeout): infraestrutura, não programação. Outras
            // InvalidOperationException (rápidas) caem no catch genérico: erro de programação, não indisponibilidade
            await connection.DisposeAsync().ConfigureAwait(false);
            OrmLog.ConnectionOpenFailed(logger, KindName(kind), exception.GetType().Name, "pool");
            return OrmErrors.ConnectionUnavailable();
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Também completa conexões derivadas pelo EF Core sem servidor (ex.: a conexão com <c>master</c> que o EF cria a partir da
    /// principal para criar o banco), quando o dialeto as reconhece (<see cref="ISqlDialect.TryGetDerivedDatabase"/>): o segredo
    /// é aplicado e só o banco pedido pelo EF é mantido.
    /// <para><b>Segurança:</b> conexão que já tem servidor não é alterada nem validada: a política do segredo <b>não</b> se
    /// aplica a ela (intencional).</para>
    /// </remarks>
    public async Task<Result> ConfigureConnectionAsync(DbConnection connection, OrmConnectionKind kind, CancellationToken cancellationToken)
    {
        Guard.NotNull(connection);
        if (connection.State != ConnectionState.Closed)
            return Result.Success();

        string? database = null;
        if (!string.IsNullOrEmpty(connection.ConnectionString) && !_dialect.TryGetDerivedDatabase(connection.ConnectionString, out database))
            return Result.Success();   // já configurada (pela aplicação ou por uma abertura anterior)

        var connectionString = await BuildConnectionStringAsync(kind, cancellationToken, database).ConfigureAwait(false);
        if (connectionString.IsFailure)
            return connectionString.ToFailure();

        connection.ConnectionString = connectionString.Value.Reveal();
        return Result.Success();
    }

    /// <summary>Lê o segredo, valida a política e devolve a string de conexão protegida (o <c>ToString</c> mascara).</summary>
    /// <param name="kind">Tipo da conexão.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <param name="databaseOverride">Banco a usar no lugar do segredo (conexões derivadas pelo EF Core, ex.: <c>master</c>).</param>
    internal async Task<Result<ProtectedConnectionString>> BuildConnectionStringAsync(OrmConnectionKind kind, CancellationToken cancellationToken,
        string? databaseOverride = null)
    {
        string kindName = KindName(kind);
        bool readOnly = kind == OrmConnectionKind.ReadOnly;
        var secret = await secrets.GetSecretAsync(options.SecretNameFor(readOnly), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (secret.IsFailure)
        {
            // O nome do segredo já fica na auditoria do TEC.Vault; aqui só o tipo da conexão e o código
            OrmLog.SecretUnavailable(logger, kindName, secret.Error!.Code);
            return OrmErrors.ConnectionUnavailable();
        }

        Result<ProtectedConnectionString> sanitized;
        try
        {
            sanitized = _dialect.SanitizeAndValidateConnectionString(secret.Value.Value, readOnly, options.AllowTrustServerCertificate,
                options.ApplicationName, options.CommandTimeoutSeconds, databaseOverride);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or KeyNotFoundException
                                              or InvalidOperationException or InvalidCastException or OverflowException)
        {
            // Nunca registrar a exceção do parse: a mensagem do driver pode reproduzir trechos do texto (inclusive a senha)
            return Reject(kindName, "formato inválido");
        }

        if (sanitized.IsFailure)
            return Reject(kindName, sanitized.Error!.Message);

        if (sanitized.Value.RelaxedTransportSecurity)
        {
            int bit = 1 << (int)kind;
            if ((Interlocked.Or(ref _trustWarnings, bit) & bit) == 0)
                OrmLog.TrustServerCertificateEnabled(logger, kindName);
        }
        return sanitized;
    }

    private Error Reject(string kindName, string reason)
    {
        OrmLog.SecretRejected(logger, kindName, reason);
        return OrmErrors.InvalidConnectionSecret();
    }

    private static string KindName(OrmConnectionKind kind) => kind == OrmConnectionKind.ReadOnly ? "leitura" : "escrita";
}
