using Microsoft.Extensions.Logging;

namespace TEC.ORM.SqlServer.Internal;

/// <summary>
/// Mensagens de log do TEC.ORM (source generator: sem alocação quando o nível está desabilitado).
/// Segurança: nunca registra string de conexão, SQL, valores de parâmetros ou de colunas, nem a mensagem original do banco
/// (que pode conter o valor de uma chave duplicada); apenas provedor, operação, alvo, identificador (conforme
/// <c>IdentifierLogMode</c>), duração e códigos. Escritas são registradas em Information (trilha de auditoria); a exclusão
/// física, em Warning (alerta: remoção irreversível).
/// </summary>
internal static partial class OrmLog
{
    [LoggerMessage(3000, LogLevel.Debug, "ORM {Provider}: {Operation} de {Target} ({Identifier}) concluída em {ElapsedMilliseconds} ms.")]
    public static partial void ReadSucceeded(ILogger logger, string provider, string operation, string target, string identifier,
        long elapsedMilliseconds);

    [LoggerMessage(3001, LogLevel.Information, "Auditoria ORM {Provider}: {Operation} de {Target} ({Identifier}) concluída em {ElapsedMilliseconds} ms.")]
    public static partial void WriteSucceeded(ILogger logger, string provider, string operation, string target, string identifier,
        long elapsedMilliseconds);

    [LoggerMessage(3002, LogLevel.Information, "ORM {Provider}: {Operation} de {Target} ({Identifier}) retornou {ErrorCode} em {ElapsedMilliseconds} ms.")]
    public static partial void ExpectedFailure(ILogger logger, string provider, string operation, string target, string identifier,
        string errorCode, long elapsedMilliseconds);

    [LoggerMessage(3003, LogLevel.Warning, "Auditoria ORM {Provider}: {Operation} de {Target} ({Identifier}) retornou {ErrorCode} em {ElapsedMilliseconds} ms.")]
    public static partial void WriteFailure(ILogger logger, string provider, string operation, string target, string identifier,
        string errorCode, long elapsedMilliseconds);

    [LoggerMessage(3004, LogLevel.Error,
        "ORM {Provider}: {Operation} de {Target} ({Identifier}) falhou por infraestrutura: {ErrorCode} ({ExceptionType}, SQL {SqlErrorNumber}) em {ElapsedMilliseconds} ms.")]
    public static partial void InfrastructureFailure(ILogger logger, string provider, string operation, string target, string identifier,
        string errorCode, string exceptionType, int sqlErrorNumber, long elapsedMilliseconds);

    [LoggerMessage(3005, LogLevel.Error, "ORM {Provider}: {Operation} de {Target} ({Identifier}) lançou {ExceptionType} inesperada; convertida em {ErrorCode}.")]
    public static partial void UnexpectedException(ILogger logger, Exception exception, string provider, string operation, string target,
        string identifier, string exceptionType, string errorCode);

    [LoggerMessage(3006, LogLevel.Debug, "ORM {Provider}: {Operation} de {Target} ({Identifier}) cancelada após {ElapsedMilliseconds} ms.")]
    public static partial void Canceled(ILogger logger, string provider, string operation, string target, string identifier, long elapsedMilliseconds);

    [LoggerMessage(3007, LogLevel.Warning,
        "Auditoria ORM {Provider}: EXCLUSÃO FÍSICA - {Operation} de {Target} ({Identifier}) removeu os registros de fato do banco (irreversível) em {ElapsedMilliseconds} ms.")]
    public static partial void HardDeleteSucceeded(ILogger logger, string provider, string operation, string target, string identifier,
        long elapsedMilliseconds);

    [LoggerMessage(3008, LogLevel.Warning,
        "ORM {Provider}: {Operation} de {Target} teve falha transitória {ErrorCode} (SQL {SqlErrorNumber}); nova tentativa {Retry} de {MaxRetries}.")]
    public static partial void TransientFailureRetry(ILogger logger, string provider, string operation, string target, string errorCode,
        int sqlErrorNumber, int retry, int maxRetries);

    // ---------- Conexão (TEC.Vault) ----------

    [LoggerMessage(3100, LogLevel.Error, "ORM: segredo da conexão {Kind} indisponível no cofre: {ErrorCode}.")]
    public static partial void SecretUnavailable(ILogger logger, string kind, string errorCode);

    [LoggerMessage(3101, LogLevel.Error, "ORM: segredo da conexão {Kind} recusado pela política de segurança: {Reason}.")]
    public static partial void SecretRejected(ILogger logger, string kind, string reason);

    [LoggerMessage(3102, LogLevel.Error, "ORM: falha ao abrir a conexão {Kind} (SQL {SqlErrorNumber}).")]
    public static partial void ConnectionOpenFailed(ILogger logger, string kind, int sqlErrorNumber);

    [LoggerMessage(3103, LogLevel.Warning, "ORM: TrustServerCertificate habilitado na conexão {Kind}: o certificado do servidor não é validado (use só em desenvolvimento).")]
    public static partial void TrustServerCertificateEnabled(ILogger logger, string kind);

    [LoggerMessage(3104, LogLevel.Warning, "Health check do ORM falhou: {ErrorCode}.")]
    public static partial void HealthCheckFailed(ILogger logger, string errorCode);
}
