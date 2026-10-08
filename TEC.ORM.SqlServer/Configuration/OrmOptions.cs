using TEC.Core.Exceptions;

namespace TEC.ORM.SqlServer.Configuration;

/// <summary>Como o identificador do registro aparece nos logs de auditoria.</summary>
public enum IdentifierLogMode
{
    /// <summary>Valor como está (adequado a chaves técnicas: <c>int</c>, <c>long</c>, <c>Guid</c>).</summary>
    Plain = 0,

    /// <summary>
    /// Tamanho + prefixo do HMAC-SHA256 com chave aleatória do processo, no formato <c>&lt;N caracteres, hmac:xxxxxxxxxxxx&gt;</c>
    /// (<c>SensitiveDataMasker.DescribeUntrusted</c> do TEC.Core): correlaciona registros no mesmo processo sem expor o valor.
    /// Use quando a chave é dado pessoal (CPF, e-mail).
    /// </summary>
    Hashed = 1,

    /// <summary>Não registra o identificador.</summary>
    Omitted = 2
}

/// <summary>Opções do TEC.ORM para SQL Server.</summary>
/// <remarks>
/// Segurança: aqui só ficam <b>nomes</b> de segredos do TEC.Vault, nunca a string de conexão. Pode vir de appsettings sem risco.
/// </remarks>
public sealed class OrmOptions
{
    /// <summary>Tempo limite padrão dos comandos (segundos).</summary>
    public const int DefaultCommandTimeoutSeconds = 30;

    /// <summary>Nome do segredo no TEC.Vault com a string de conexão de leitura e escrita (EF Core). Obrigatório.</summary>
    public string ConnectionSecretName { get; set; } = string.Empty;

    /// <summary>
    /// Nome do segredo com a string de conexão das leituras complexas (Dapper). Recomendado: um login só com <c>SELECT</c>.
    /// <c>null</c> usa <see cref="ConnectionSecretName"/>.
    /// </summary>
    public string? ReadOnlyConnectionSecretName { get; set; }

    /// <summary>Nome da aplicação na conexão (visível em <c>sys.dm_exec_sessions</c>, auditoria do SQL Server).</summary>
    public string ApplicationName { get; set; } = "TEC.ORM";

    /// <summary>
    /// Aceita <c>TrustServerCertificate=True</c> no segredo (certificado do servidor não validado). Padrão <c>false</c>:
    /// habilite só em desenvolvimento local (ex.: SQL Server no Docker com certificado autoassinado).
    /// </summary>
    public bool AllowTrustServerCertificate { get; set; }

    /// <summary>Tempo limite dos comandos, em segundos (1 a 600).</summary>
    public int CommandTimeoutSeconds { get; set; } = DefaultCommandTimeoutSeconds;

    /// <summary>Itens máximos por página na listagem (1 a 10.000).</summary>
    public int MaxPageSize { get; set; } = 100;

    /// <summary>Registros máximos da busca por critérios (1 a 100.000); acima disso a busca falha com <c>ORM_LIMITE_EXCEDIDO</c>.</summary>
    public int MaxFindResults { get; set; } = 1_000;

    /// <summary>
    /// Linhas máximas lidas por uma leitura complexa (<c>IOrmQueryExecutor</c>; 1 a 1.000.000). Acima disso a consulta falha
    /// com <c>ORM_LIMITE_EXCEDIDO</c> e o comando é cancelado no banco, sem carregar o resto na memória.
    /// </summary>
    public int MaxQueryRows { get; set; } = 10_000;

    /// <summary>
    /// Novas tentativas em falha transitória (0 a 5; padrão 2), <b>só em leituras fora de transação</b> (repositório e
    /// leituras complexas): queda de conexão, failover, banco temporariamente indisponível (Azure SQL) e vítima de deadlock.
    /// Escritas e qualquer operação dentro de transação nunca são repetidas (repetir um <c>SaveChanges</c> ou um
    /// <c>COMMIT</c> de resultado desconhecido poderia duplicar a gravação); tempo esgotado também não (repetiria a consulta cara).
    /// </summary>
    /// <remarks>
    /// Não habilite o <c>EnableRetryOnFailure</c> do EF Core (parâmetro <c>sqlServer</c> do <c>AddTecOrm</c>): ele é
    /// incompatível com as transações do <c>IUnitOfWork</c> (o EF recusa iniciar a transação) e repetiria escritas.
    /// </remarks>
    public int TransientRetryCount { get; set; } = 2;

    /// <summary>
    /// Espera antes da primeira nova tentativa (10 ms a 10 s; padrão 200 ms). Dobra a cada tentativa, com variação aleatória
    /// de até 50% para que muitas instâncias não tentem ao mesmo tempo depois de um failover.
    /// </summary>
    public TimeSpan TransientRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Recusa, nas leituras complexas, SQL que não seja somente leitura (defesa em profundidade). Padrão <c>true</c>.</summary>
    public bool EnforceReadOnlyQueries { get; set; } = true;

    /// <summary>Identificador nos logs de auditoria.</summary>
    public IdentifierLogMode IdentifierLogMode { get; set; } = IdentifierLogMode.Plain;

    /// <summary>Segredo usado pela conexão do tipo informado.</summary>
    internal string SecretNameFor(bool readOnly) => readOnly ? ReadOnlyConnectionSecretName ?? ConnectionSecretName : ConnectionSecretName;

    /// <summary>Valida as opções; lança <see cref="InvalidConfigurationException"/> (sem valores) se algo estiver fora do limite.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionSecretName))
            throw new InvalidConfigurationException("TecOrm:" + nameof(ConnectionSecretName));
        if (ReadOnlyConnectionSecretName is not null && string.IsNullOrWhiteSpace(ReadOnlyConnectionSecretName))
            throw new InvalidConfigurationException("TecOrm:" + nameof(ReadOnlyConnectionSecretName));
        if (string.IsNullOrWhiteSpace(ApplicationName) || ApplicationName.Length > 128)
            throw new InvalidConfigurationException("TecOrm:" + nameof(ApplicationName));
        if (CommandTimeoutSeconds is < 1 or > 600)
            throw new InvalidConfigurationException("TecOrm:" + nameof(CommandTimeoutSeconds));
        if (MaxPageSize is < 1 or > 10_000)
            throw new InvalidConfigurationException("TecOrm:" + nameof(MaxPageSize));
        if (MaxFindResults is < 1 or > 100_000)
            throw new InvalidConfigurationException("TecOrm:" + nameof(MaxFindResults));
        if (MaxQueryRows is < 1 or > 1_000_000)
            throw new InvalidConfigurationException("TecOrm:" + nameof(MaxQueryRows));
        if (TransientRetryCount is < 0 or > 5)
            throw new InvalidConfigurationException("TecOrm:" + nameof(TransientRetryCount));
        if (TransientRetryDelay < TimeSpan.FromMilliseconds(10) || TransientRetryDelay > TimeSpan.FromSeconds(10))
            throw new InvalidConfigurationException("TecOrm:" + nameof(TransientRetryDelay));
        if (!Enum.IsDefined(IdentifierLogMode))
            throw new InvalidConfigurationException("TecOrm:" + nameof(IdentifierLogMode));
    }
}
