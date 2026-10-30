using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Security;

namespace TEC.ORM.Oracle.Dialect;

/// <summary>
/// Especialização do dialeto SQL e conexões para Oracle Database (ODP.NET).
/// </summary>
/// <remarks>
/// <para>Política do segredo (o <c>Data Source</c> é lido sem espaços, tabulações e quebras de linha):</para>
/// <list type="bullet">
/// <item><description>descritor (<c>(DESCRIPTION=...)</c>): todo <c>ADDRESS</c> precisa de <c>PROTOCOL=TCPS</c> (sem protocolo
/// vale TCP, em texto claro), e <c>SSL_SERVER_DN_MATCH</c> desligado conta como certificado não validado;</description></item>
/// <item><description>EZConnect (<c>host:porta/serviço</c>, <c>//host/serviço</c>, <c>tcp://...</c>): só <c>tcps://...</c>
/// passa;</description></item>
/// <item><description>alias do <c>tnsnames.ora</c>/LDAP (só o nome): não dá para inspecionar daqui e passa; a criptografia do
/// alias é responsabilidade de quem o mantém.</description></item>
/// </list>
/// <para>O que não passa só é aceito com <c>OrmOptions.AllowTrustServerCertificate</c> (desenvolvimento). Sempre impostos:
/// <c>Persist Security Info=False</c>; a conexão de leitura recebe <c>Enlist=false</c>. O tempo limite dos comandos vai em cada
/// comando (EF Core e Dapper), não no <c>Connection Timeout</c>, que é o da abertura e fica como está no segredo.</para>
/// <para>Parâmetros com <c>:</c> (<see cref="ParameterPrefix"/>). As leituras complexas rodam numa transação
/// <c>SET TRANSACTION READ ONLY</c>.</para>
/// </remarks>
public sealed partial class OracleDialect : ISqlDialect
{
    /// <inheritdoc />
    public string ProviderName => "Oracle";

    /// <inheritdoc />
    public string DbSystemName => "oracle.db";

    /// <inheritdoc />
    public char ParameterPrefix => ':';

    /// <inheritdoc />
    public string HealthCheckProbeQuery => "SELECT 1 FROM DUAL";

    /// <inheritdoc />
    public IReadOnlySqlValidator ReadOnlyValidator { get; } = new OracleReadOnlySqlValidator();

    /// <inheritdoc />
    public IOrmExceptionTranslator ExceptionTranslator { get; } = new OracleExceptionTranslator();

    /// <inheritdoc />
    public string DelimitIdentifier(string identifier) =>
        "\"" + (identifier ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <inheritdoc />
    public string FormatAtomicIdempotencyInsert(string tableName) =>
        FormatAtomicIdempotencyInsert(tableName, new IdempotencyInsertColumns("\"Scope\"", "\"IdempotencyKey\"", "\"RequestHash\"",
            "\"LockId\"", "\"State\"", "\"LockedUntil\"", "\"RetentionSeconds\"", "\"ExpiresAt\"", "\"CreatedAt\""));

    /// <inheritdoc />
    /// <remarks><c>MERGE INTO ... WHEN NOT MATCHED THEN INSERT</c>; duas reservas simultâneas podem colidir (ORA-00001), tratado como "já existe".</remarks>
    public string FormatAtomicIdempotencyInsert(string tableName, IdempotencyInsertColumns columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return $"MERGE INTO {tableName} t " +
               "USING (SELECT {0} AS \"S_SCOPE\", {1} AS \"S_KEY\" FROM DUAL) s " +
               $"ON (t.{columns.Scope} = s.\"S_SCOPE\" AND t.{columns.Key} = s.\"S_KEY\") " +
               "WHEN NOT MATCHED THEN " +
               $"INSERT ({columns.Scope}, {columns.Key}, {columns.RequestHash}, {columns.LockId}, {columns.State}, {columns.LockedUntil}, " +
               $"{columns.RetentionSeconds}, {columns.ExpiresAt}, {columns.CreatedAt}) " +
               "VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8})";
    }

    /// <inheritdoc />
    public DbConnection CreateConnection(ProtectedConnectionString connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        return new OracleConnection(connectionString.Reveal());
    }

    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable?> BeginReadOnlyTransactionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.Transaction = transaction;
                // Precisa ser o primeiro comando da transação: o banco recusa DML até o fim dela (ORA-01456)
                command.CommandText = "SET TRANSACTION READ ONLY";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public Result<ProtectedConnectionString> SanitizeAndValidateConnectionString(string rawConnectionString, bool isReadOnly,
        bool allowTrustServerCertificate, string applicationName, int commandTimeoutSeconds)
    {
        OracleConnectionStringBuilder builder;
        try
        {
            builder = new OracleConnectionStringBuilder(rawConnectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or KeyNotFoundException
                                              or InvalidOperationException or InvalidCastException or OverflowException)
        {
            return Reason("formato inválido");
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
            return Reason("Data Source não informado");
        if (string.IsNullOrWhiteSpace(builder.UserID))
            return Reason("usuário não informado");

        bool relaxed = IsPlainTransport(builder.DataSource);
        if (relaxed && !allowTrustServerCertificate)
            return Reason("conexão Oracle sem TCPS validado (PROTOCOL=TCP, EZConnect sem tcps:// ou SSL_SERVER_DN_MATCH desligado): use TCPS ou habilite AllowTrustServerCertificate só em desenvolvimento");

        builder.PersistSecurityInfo = false;
        if (isReadOnly)
            builder.Enlist = "false";   // fora do TransactionScope ambiente, como no SQL Server

        return new ProtectedConnectionString(builder.ConnectionString) { RelaxedTransportSecurity = relaxed };
    }

    /// <summary>
    /// O <c>Data Source</c> leva a conexão por TCP sem TLS (ou com TLS sem validar o servidor). Alias (só um nome) não é
    /// inspecionável e devolve <c>false</c>.
    /// </summary>
    internal static bool IsPlainTransport(string dataSource)
    {
        // Espaço, tabulação e quebra de linha são permitidos no descritor: "PROTOCOL = TCP" não pode escapar da verificação
        var normalized = new StringBuilder(dataSource.Length);
        foreach (char c in dataSource)
        {
            if (!char.IsWhiteSpace(c))
                normalized.Append(char.ToUpperInvariant(c));
        }
        string text = normalized.ToString();

        if (text.Contains('(', StringComparison.Ordinal))
        {
            int addresses = AddressPattern().Count(text);
            var protocols = ProtocolPattern().Matches(text);
            if (protocols.Count == 0 || protocols.Count < addresses)
                return true;   // ADDRESS sem PROTOCOL é TCP
            foreach (Match protocol in protocols)
            {
                if (!protocol.Groups[1].Value.Equals("TCPS", StringComparison.Ordinal))
                    return true;
            }
            return DnMatchOffPattern().IsMatch(text);
        }

        // EZConnect: host:porta/serviço, //host/serviço, tcp://..., tcps://...
        if (text.Contains('/', StringComparison.Ordinal) || text.Contains(':', StringComparison.Ordinal))
            return !text.StartsWith("TCPS://", StringComparison.Ordinal);

        return false;   // alias do tnsnames/LDAP: não inspecionável
    }

    private static Error Reason(string reason) => OrmErrors.InvalidInput("ConnectionString", reason);

    [GeneratedRegex(@"\(ADDRESS=", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex AddressPattern();

    [GeneratedRegex(@"\(PROTOCOL=([^()]*)\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ProtocolPattern();

    [GeneratedRegex(@"\(SSL_SERVER_DN_MATCH=(NO|FALSE|OFF|0)\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex DnMatchOffPattern();
}
