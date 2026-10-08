using TEC.Core.Common.Results;

namespace TEC.ORM.Common;

/// <summary>
/// Erros padronizados do TEC.ORM. Toda implementação converte as suas falhas (exceções do ORM e do banco) para estes códigos,
/// então o consumidor trata o resultado da mesma forma, seja qual for o banco.
/// </summary>
/// <remarks>
/// Segurança: as mensagens nunca incluem SQL, valores de colunas, nomes de servidor/banco, nome do segredo ou a mensagem
/// original do banco (que costuma trazer o valor duplicado de uma chave única, por exemplo). Falhas de infraestrutura usam
/// <see cref="ErrorType.ExternalService"/>, cuja mensagem não é exposta ao cliente da API.
/// </remarks>
public static class OrmErrors
{
    /// <summary>Código: dado de entrada inválido (entidade nula, página fora do limite, ordenação não permitida...).</summary>
    public const string InvalidInputCode = "ORM_ENTRADA_INVALIDA";

    /// <summary>Código: registro não encontrado (ou excluído logicamente).</summary>
    public const string NotFoundCode = "ORM_NAO_ENCONTRADO";

    /// <summary>Código: violação de chave única ou de integridade referencial.</summary>
    public const string ConflictCode = "ORM_CONFLITO";

    /// <summary>Código: o registro foi alterado por outra operação (concorrência otimista ou deadlock).</summary>
    public const string ConcurrencyCode = "ORM_CONCORRENCIA";

    /// <summary>Código: a busca retornaria mais registros que o limite configurado; use a listagem paginada.</summary>
    public const string TooManyResultsCode = "ORM_LIMITE_EXCEDIDO";

    /// <summary>Código: consulta recusada por não ser somente leitura.</summary>
    public const string QueryNotAllowedCode = "ORM_CONSULTA_NAO_PERMITIDA";

    /// <summary>Código: não foi possível obter ou abrir a conexão (segredo indisponível, login recusado, rede).</summary>
    public const string ConnectionUnavailableCode = "ORM_CONEXAO_INDISPONIVEL";

    /// <summary>Código: o segredo da conexão existe, mas está em formato inválido ou fora da política de segurança.</summary>
    public const string InvalidConnectionSecretCode = "ORM_CONEXAO_INVALIDA";

    /// <summary>Código: tempo limite do comando excedido.</summary>
    public const string TimeoutCode = "ORM_TEMPO_ESGOTADO";

    /// <summary>Código: gravação de entidade auditada sem identidade autenticada.</summary>
    public const string AuditIdentityRequiredCode = "ORM_AUDITORIA_SEM_IDENTIDADE";

    /// <summary>Código: falha não classificada.</summary>
    public const string FailureCode = "ORM_FALHA";

    /// <summary>Entrada inválida (HTTP 400).</summary>
    public static Error InvalidInput(string field, string message) => Error.Validation(InvalidInputCode, message, field);

    /// <summary>Registro não encontrado (HTTP 404).</summary>
    public static Error NotFound() => Error.NotFound(NotFoundCode, "Registro não encontrado.");

    /// <summary>Violação de chave única ou de integridade referencial (HTTP 409).</summary>
    public static Error Conflict() =>
        Error.Conflict(ConflictCode, "A operação viola uma restrição de unicidade ou de integridade referencial.");

    /// <summary>Concorrência (HTTP 409).</summary>
    public static Error Concurrency() =>
        Error.Conflict(ConcurrencyCode, "O registro foi alterado ou excluído por outra operação. Leia novamente e repita.");

    /// <summary>Limite de resultados da busca excedido (HTTP 400).</summary>
    public static Error TooManyResults(int limit) =>
        Error.Validation(TooManyResultsCode, $"A busca retornaria mais de {limit} registros. Use a listagem paginada.", "specification");

    /// <summary>Consulta SQL que devolveria mais linhas que o limite configurado (HTTP 400; mesmo código da busca).</summary>
    /// <param name="limit">Limite de linhas configurado.</param>
    public static Error QueryTooManyRows(int limit) =>
        Error.Validation(TooManyResultsCode,
            $"A consulta retornaria mais de {limit} linhas. Pagine no SQL (OFFSET/FETCH) ou restrinja o filtro.", "sql");

    /// <summary>Consulta que não é somente leitura (HTTP 400).</summary>
    public static Error QueryNotAllowed(string reason) =>
        Error.Validation(QueryNotAllowedCode, $"Consulta recusada: {reason}", "sql");

    /// <summary>Conexão indisponível (HTTP 502).</summary>
    public static Error ConnectionUnavailable() =>
        Error.ExternalService(ConnectionUnavailableCode, "Banco de dados indisponível.");

    /// <summary>Segredo de conexão inválido (HTTP 502: é problema de configuração da aplicação, não do usuário).</summary>
    public static Error InvalidConnectionSecret() =>
        Error.ExternalService(InvalidConnectionSecretCode, "Configuração de conexão com o banco de dados inválida.");

    /// <summary>Tempo limite excedido (HTTP 502).</summary>
    public static Error Timeout() => Error.ExternalService(TimeoutCode, "O banco de dados não respondeu a tempo.");

    /// <summary>Gravação sem identidade autenticada (HTTP 401).</summary>
    public static Error AuditIdentityRequired() =>
        Error.Unauthorized(AuditIdentityRequiredCode, "Identificação necessária para gravar o registro.");

    /// <summary>Falha não classificada (HTTP 500).</summary>
    public static Error Failure() => Error.Failure(FailureCode, "Falha ao acessar o banco de dados.");
}
