using System.Data.Common;
using TEC.Core.Common.Results;
using TEC.ORM.EntityFrameworkCore.Security;

namespace TEC.ORM.EntityFrameworkCore.Dialects;

/// <summary>
/// Contrato do dialeto de banco de dados para especializações de sintaxe, segurança e mapeamento de exceções.
/// </summary>
/// <remarks>
/// Os membros com implementação padrão foram acrescentados depois da 0.0.1: um dialeto próprio continua compilando e herda o
/// comportamento conservador (sem transação somente leitura, sem conexão derivada, marcador <c>@</c>).
/// </remarks>
public interface ISqlDialect
{
    /// <summary>Nome do provedor (ex.: "SqlServer", "PostgreSql", "MySql", "Oracle").</summary>
    string ProviderName { get; }

    /// <summary>
    /// Valor de <c>db.system.name</c> (convenção semântica do OpenTelemetry) nas métricas e traces, ex.:
    /// <c>microsoft.sql_server</c>, <c>postgresql</c>, <c>mysql</c>, <c>oracle.db</c>. Padrão: <see cref="ProviderName"/>.
    /// </summary>
    string DbSystemName => ProviderName;

    /// <summary>
    /// Prefixo dos marcadores de parâmetro que o driver entende (<c>@</c> no SQL Server, PostgreSQL e MySQL; <c>:</c> no
    /// Oracle). Os marcadores gerados por <c>SqlQuery.Interpolated</c> saem com este prefixo.
    /// </summary>
    char ParameterPrefix => '@';

    /// <summary>Instrução SQL leve de verificação de conectividade no health check.</summary>
    string HealthCheckProbeQuery { get; }

    /// <summary>Delimita um identificador (tabela ou coluna) de acordo com o dialeto.</summary>
    string DelimitIdentifier(string identifier);

    /// <summary>Validador de consultas SQL somente-leitura.</summary>
    IReadOnlySqlValidator ReadOnlyValidator { get; }

    /// <summary>Tradutor de exceções nativas do banco.</summary>
    IOrmExceptionTranslator ExceptionTranslator { get; }

    /// <summary>
    /// Gera a instrução SQL atômica de inserção condicional para reserva de idempotência, com os nomes de coluna padrão.
    /// </summary>
    string FormatAtomicIdempotencyInsert(string tableName);

    /// <summary>
    /// Gera a instrução SQL atômica de reserva de idempotência com os nomes de coluna do modelo (já delimitados), na ordem dos
    /// marcadores <c>{0}</c> a <c>{8}</c>. Padrão: <see cref="FormatAtomicIdempotencyInsert(string)"/>, que ignora os nomes.
    /// </summary>
    string FormatAtomicIdempotencyInsert(string tableName, IdempotencyInsertColumns columns) => FormatAtomicIdempotencyInsert(tableName);

    /// <summary>
    /// Cria uma nova instância de conexão nativa do provedor.
    /// </summary>
    DbConnection CreateConnection(ProtectedConnectionString connectionString);

    /// <summary>
    /// Valida e sanitiza a string de conexão obtida do cofre, aplicando as diretivas estritas de segurança
    /// (criptografia mandatória, proibição de salvar senhas, timeout e nome da aplicação).
    /// </summary>
    /// <remarks>
    /// Na recusa, a mensagem do erro é o motivo registrado no log (texto fixo, nunca trechos da string de conexão); quem chama
    /// devolve <c>ORM_CONEXAO_INVALIDA</c>.
    /// </remarks>
    Result<ProtectedConnectionString> SanitizeAndValidateConnectionString(string rawConnectionString, bool isReadOnly,
        bool allowTrustServerCertificate, string applicationName, int commandTimeoutSeconds);

    /// <summary>
    /// Como <see cref="SanitizeAndValidateConnectionString(string, bool, bool, string, int)"/>, trocando o banco do segredo
    /// por <paramref name="databaseOverride"/> (conexão derivada pelo EF Core, ver <see cref="TryGetDerivedDatabase"/>).
    /// Padrão: ignora a troca.
    /// </summary>
    Result<ProtectedConnectionString> SanitizeAndValidateConnectionString(string rawConnectionString, bool isReadOnly,
        bool allowTrustServerCertificate, string applicationName, int commandTimeoutSeconds, string? databaseOverride) =>
        SanitizeAndValidateConnectionString(rawConnectionString, isReadOnly, allowTrustServerCertificate, applicationName, commandTimeoutSeconds);

    /// <summary>
    /// Reconhece uma conexão que o EF Core derivou da principal <b>sem servidor</b> (ex.: <c>Initial Catalog=master</c> para
    /// criar o banco): devolve <c>true</c> e o banco pedido, e o segredo é aplicado mantendo esse banco. Conexão com servidor é
    /// da aplicação e não é alterada (<c>false</c>). Padrão: <c>false</c>.
    /// </summary>
    bool TryGetDerivedDatabase(string connectionString, out string? database)
    {
        database = null;
        return false;
    }

    /// <summary>
    /// Conexão administrativa (ex.: <c>master</c> no SQL Server, <c>postgres</c> no PostgreSQL) que o EF Core abre para criar
    /// ou apagar o banco: passa fora do circuit breaker da aplicação. Padrão: <c>false</c>.
    /// </summary>
    bool IsServerConnection(DbConnection connection) => false;

    /// <summary>
    /// Abre, na conexão de leitura já aberta, uma transação <c>READ ONLY</c> (PostgreSQL, MySQL e Oracle): o banco recusa
    /// escrita mesmo que o SQL passe pelo validador. O descarte encerra a transação (rollback). <c>null</c> quando o banco não
    /// tem equivalente (SQL Server: use um login só com <c>SELECT</c> e <c>ApplicationIntent=ReadOnly</c>). Padrão: <c>null</c>.
    /// </summary>
    ValueTask<IAsyncDisposable?> BeginReadOnlyTransactionAsync(DbConnection connection, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IAsyncDisposable?>(null);
}

/// <summary>
/// Nomes das colunas da tabela de idempotência, já delimitados pelo dialeto, na ordem dos valores da reserva
/// (<c>{0}</c> a <c>{8}</c>).
/// </summary>
/// <param name="Scope">Escopo.</param>
/// <param name="Key">Chave do cliente.</param>
/// <param name="RequestHash">Hash da requisição.</param>
/// <param name="LockId">Identificador da reserva.</param>
/// <param name="State">Estado (0 = em andamento, 1 = concluída).</param>
/// <param name="LockedUntil">Fim da reserva.</param>
/// <param name="RetentionSeconds">Retenção em segundos.</param>
/// <param name="ExpiresAt">Expiração.</param>
/// <param name="CreatedAt">Criação.</param>
public sealed record IdempotencyInsertColumns(string Scope, string Key, string RequestHash, string LockId, string State,
    string LockedUntil, string RetentionSeconds, string ExpiresAt, string CreatedAt);
