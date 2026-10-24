using System.Data.Common;
using TEC.Core.Common.Results;
using TEC.ORM.EntityFrameworkCore.Security;

namespace TEC.ORM.EntityFrameworkCore.Dialects;

/// <summary>
/// Contrato do dialeto de banco de dados para especializações de sintaxe, segurança e mapeamento de exceções.
/// </summary>
public interface ISqlDialect
{
    /// <summary>Nome do provedor (ex.: "SqlServer", "PostgreSql", "MySql", "Oracle").</summary>
    string ProviderName { get; }

    /// <summary>Instrução SQL leve de verificação de conectividade no health check.</summary>
    string HealthCheckProbeQuery { get; }

    /// <summary>Delimita um identificador (tabela ou coluna) de acordo com o dialeto.</summary>
    string DelimitIdentifier(string identifier);

    /// <summary>Validador de consultas SQL somente-leitura.</summary>
    IReadOnlySqlValidator ReadOnlyValidator { get; }

    /// <summary>Tradutor de exceções nativas do banco.</summary>
    IOrmExceptionTranslator ExceptionTranslator { get; }

    /// <summary>
    /// Gera a instrução SQL atômica de inserção condicional para reserva de idempotência.
    /// </summary>
    string FormatAtomicIdempotencyInsert(string tableName);

    /// <summary>
    /// Cria uma nova instância de conexão nativa do provedor.
    /// </summary>
    DbConnection CreateConnection(ProtectedConnectionString connectionString);

    /// <summary>
    /// Valida e sanitiza a string de conexão obtida do cofre, aplicando as diretivas estritas de segurança
    /// (criptografia mandatória, proibição de salvar senhas, timeout e nome da aplicação).
    /// </summary>
    Result<ProtectedConnectionString> SanitizeAndValidateConnectionString(string rawConnectionString, bool isReadOnly,
        bool allowTrustServerCertificate, string applicationName, int commandTimeoutSeconds);
}
