using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using TEC.Vault.Abstractions;
using TEC.Core.Common.Guards;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.Internal;

namespace TEC.ORM.SqlServer.Security;

/// <summary>Tipo de conexão.</summary>
public enum OrmConnectionKind
{
    /// <summary>Leitura e escrita (EF Core): segredo <see cref="OrmOptions.ConnectionSecretName"/>.</summary>
    ReadWrite = 0,

    /// <summary>
    /// Somente leitura (Dapper): segredo <see cref="OrmOptions.ReadOnlyConnectionSecretName"/> e <c>ApplicationIntent=ReadOnly</c>
    /// (direciona para réplicas de leitura em Always On).
    /// </summary>
    ReadOnly = 1
}

/// <summary>
/// Gerencia a conexão com o banco de forma segura: a string de conexão vem <b>só</b> do TEC.Vault, existe apenas em memória
/// durante a abertura e nunca é devolvida, registrada ou guardada.
/// </summary>
/// <remarks>Ponto de extensão: substitua no DI (ex.: autenticação por token do Entra ID em vez de usuário e senha).</remarks>
public interface IOrmConnectionSecurity
{
    /// <summary>Cria e abre uma conexão. O chamador descarta.</summary>
    Task<Result<SqlConnection>> OpenConnectionAsync(OrmConnectionKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// Aplica a string de conexão do cofre a uma conexão fechada que ainda não tem uma (usado pelo interceptor do EF Core antes
    /// de abrir). Conexão que já tem string de conexão não é alterada.
    /// </summary>
    /// <remarks>
    /// <b>Segurança:</b> uma conexão que já chega com servidor (<c>Data Source</c>) é considerada configurada pela aplicação e
    /// passa <b>sem</b> a política do segredo (<c>Encrypt</c> obrigatório, <c>TrustServerCertificate</c> controlado,
    /// <c>Persist Security Info=False</c>, <c>Application Name</c>). É intencional (ex.: testes, ferramentas, conexão entregue
    /// pela aplicação ao <c>UseSqlServer(connection)</c>), mas a responsabilidade pela segurança dessa conexão é de quem a criou.
    /// </remarks>
    Task<Result> ConfigureConnectionAsync(DbConnection connection, OrmConnectionKind kind, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IOrmConnectionSecurity" />
/// <remarks>
/// <para>Política aplicada ao segredo (recusado com <c>ORM_CONEXAO_INVALIDA</c> se não atender):</para>
/// <list type="bullet">
/// <item><description>formato válido de string de conexão do SQL Server, com servidor e banco;</description></item>
/// <item><description>criptografia obrigatória (<c>Encrypt=False</c>/<c>Optional</c> é recusado);</description></item>
/// <item><description><c>TrustServerCertificate=True</c> só com <see cref="OrmOptions.AllowTrustServerCertificate"/>.</description></item>
/// </list>
/// <para>Sempre impostos: <c>Persist Security Info=False</c> (a senha some de <c>ConnectionString</c> depois de aberta),
/// <c>Application Name</c> e <c>Command Timeout</c> das opções.</para>
/// <para>Para rotação de senha sem reiniciar: o segredo é lido a cada nova conexão do EF Core (uma por <c>DbContext</c>) e do
/// Dapper; habilite o cache do TEC.Vault (<c>EnableSecretCache</c>) para não consultar o cofre a cada abertura.</para>
/// </remarks>
/// <param name="secrets">Leitor de segredos do TEC.Vault.</param>
/// <param name="options">Opções do contexto.</param>
/// <param name="logger">Log (nunca recebe a string de conexão).</param>
/// <param name="time">Relógio do circuit breaker (<see cref="OrmOptions.CircuitBreaker"/>). Padrão: <see cref="TimeProvider.System"/>.</param>
public sealed class OrmConnectionSecurity(ISecretReader secrets, OrmOptions options, ILogger<OrmConnectionSecurity> logger,
    TimeProvider? time = null) : IOrmConnectionSecurity
{
    // Um circuito por tipo de conexão (OpenConnectionAsync: leituras complexas do Dapper)
    private readonly OrmConnectionCircuit?[] _circuits =
    [
        OrmConnectionCircuit.Create(options, OrmConnectionKind.ReadWrite, time, logger),
        OrmConnectionCircuit.Create(options, OrmConnectionKind.ReadOnly, time, logger)
    ];

    // Bits por OrmConnectionKind: o aviso de TrustServerCertificate sai uma vez por tipo de conexão, não a cada abertura
    private int _trustWarnings;

    /// <inheritdoc />
    public async Task<Result<SqlConnection>> OpenConnectionAsync(OrmConnectionKind kind, CancellationToken cancellationToken)
    {
        var connectionString = await BuildConnectionStringAsync(kind, cancellationToken).ConfigureAwait(false);
        if (connectionString.IsFailure)
            return connectionString.ToFailure<SqlConnection>();

        var connection = new SqlConnection(connectionString.Value.Reveal());
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
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
            // Circuito aberto: recusada sem tocar no banco
            await connection.DisposeAsync().ConfigureAwait(false);
            return exception.Error;
        }
        catch (SqlException exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            // Sem a exceção no log: a mensagem pode citar servidor, banco e usuário
            OrmLog.ConnectionOpenFailed(logger, KindName(kind), exception.Number);
            return OrmExceptionTranslator.Translate(exception).Error;
        }
        catch (InvalidOperationException exception) when (OrmExceptionTranslator.IsPoolExhaustion(
            exception, System.Diagnostics.Stopwatch.GetElapsedTime(started), connection.ConnectionTimeout))
        {
            // Pool do SqlClient esgotado (todas as conexões em uso até o Connect Timeout): infraestrutura, não programação.
            // Outras InvalidOperationException (rápidas) caem no catch genérico: erro de programação, não indisponibilidade
            await connection.DisposeAsync().ConfigureAwait(false);
            OrmLog.ConnectionOpenFailed(logger, KindName(kind), 0);
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
    /// principal para criar o banco): o segredo é aplicado e só o banco pedido pelo EF é mantido.
    /// <para><b>Segurança:</b> conexão que já tem <c>Data Source</c> não é alterada nem validada: a política do segredo
    /// (<c>Encrypt</c>, <c>TrustServerCertificate</c>, <c>Persist Security Info</c>) <b>não</b> se aplica a ela (intencional).</para>
    /// </remarks>
    public async Task<Result> ConfigureConnectionAsync(DbConnection connection, OrmConnectionKind kind, CancellationToken cancellationToken)
    {
        Guard.NotNull(connection);
        if (connection.State != ConnectionState.Closed)
            return Result.Success();

        string? catalog = null;
        if (!string.IsNullOrEmpty(connection.ConnectionString))
        {
            if (!TryParse(connection.ConnectionString, out var existing) || !string.IsNullOrWhiteSpace(existing.DataSource))
                return Result.Success();   // já configurada (pela aplicação ou por uma abertura anterior)
            catalog = existing.InitialCatalog;
        }

        var connectionString = await BuildConnectionStringAsync(kind, cancellationToken, catalog).ConfigureAwait(false);
        if (connectionString.IsFailure)
            return connectionString.ToFailure();

        connection.ConnectionString = connectionString.Value.Reveal();
        return Result.Success();
    }

    /// <summary>Lê o segredo, valida a política e devolve a string de conexão protegida (o <c>ToString</c> mascara).</summary>
    /// <param name="kind">Tipo da conexão.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <param name="catalogOverride">Banco a usar no lugar do segredo (conexões derivadas pelo EF Core, ex.: <c>master</c>).</param>
    internal async Task<Result<ProtectedConnectionString>> BuildConnectionStringAsync(OrmConnectionKind kind, CancellationToken cancellationToken,
        string? catalogOverride = null)
    {
        string kindName = KindName(kind);
        var secret = await secrets.GetSecretAsync(options.SecretNameFor(kind == OrmConnectionKind.ReadOnly), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (secret.IsFailure)
        {
            // O nome do segredo já fica na auditoria do TEC.Vault; aqui só o tipo da conexão e o código
            OrmLog.SecretUnavailable(logger, kindName, secret.Error!.Code);
            return OrmErrors.ConnectionUnavailable();
        }

        // Nunca registrar a exceção do parse: a mensagem do SqlClient pode reproduzir trechos do texto (inclusive a senha)
        if (!TryParse(secret.Value.Value, out var builder))
            return Reject(kindName, "formato inválido");

        if (string.IsNullOrWhiteSpace(builder.DataSource))
            return Reject(kindName, "servidor não informado");
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
            return Reject(kindName, "banco de dados não informado");
        if (builder.Encrypt == SqlConnectionEncryptOption.Optional)
            return Reject(kindName, "criptografia desabilitada (Encrypt=False)");
        if (builder.TrustServerCertificate)
        {
            if (!options.AllowTrustServerCertificate)
                return Reject(kindName, "TrustServerCertificate=True não permitido");
            int bit = 1 << (int)kind;
            if ((Interlocked.Or(ref _trustWarnings, bit) & bit) == 0)
                OrmLog.TrustServerCertificateEnabled(logger, kindName);
        }

        if (!string.IsNullOrWhiteSpace(catalogOverride))
            builder.InitialCatalog = catalogOverride;
        builder.PersistSecurityInfo = false;
        builder.ApplicationName = options.ApplicationName;
        builder.CommandTimeout = options.CommandTimeoutSeconds;
        if (kind == OrmConnectionKind.ReadOnly)
            builder.ApplicationIntent = ApplicationIntent.ReadOnly;

        return new ProtectedConnectionString(builder.ConnectionString);
    }

    private static bool TryParse(string connectionString, out SqlConnectionStringBuilder builder)
    {
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or KeyNotFoundException
                                              or InvalidOperationException)
        {
            builder = null!;
            return false;
        }
    }

    private Error Reject(string kindName, string reason)
    {
        OrmLog.SecretRejected(logger, kindName, reason);
        return OrmErrors.InvalidConnectionSecret();
    }

    private static string KindName(OrmConnectionKind kind) => kind == OrmConnectionKind.ReadOnly ? "leitura" : "escrita";
}

/// <summary>
/// String de conexão em trânsito. <see cref="ToString"/> e o depurador mascaram o valor, para que um log ou uma
/// interpolação acidental nunca exponha credenciais.
/// </summary>
[System.Diagnostics.DebuggerDisplay("***")]
internal sealed class ProtectedConnectionString(string value)
{
    /// <summary>Valor real; use só para entregar ao SqlClient.</summary>
    public string Reveal() => value;

    /// <inheritdoc />
    public override string ToString() => "***";
}
