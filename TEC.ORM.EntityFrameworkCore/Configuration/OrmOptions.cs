using TEC.Core.Exceptions;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.EntityFrameworkCore.Configuration;

/// <summary>Como o identificador do registro aparece nos logs de auditoria.</summary>
public enum IdentifierLogMode
{
    /// <summary>Valor como está (adequado a chaves técnicas: <c>int</c>, <c>long</c>, <c>Guid</c>).</summary>
    Plain = 0,

    /// <summary>
    /// Tamanho + prefixo do HMAC-SHA256 com chave aleatória do processo.
    /// </summary>
    Hashed = 1,

    /// <summary>Não registra o identificador.</summary>
    Omitted = 2,

    /// <summary>
    /// Padrão: decide pelo tipo do valor. Chaves técnicas como Plain; strings como Hashed.
    /// </summary>
    Auto = 3
}

/// <summary>Opções do TEC.ORM para Entity Framework Core e Dapper.</summary>
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

    /// <summary>Nome da aplicação na conexão.</summary>
    public string ApplicationName { get; set; } = "TEC.ORM";

    /// <summary>
    /// Aceita certificados de servidor não validados (autoassinados). Padrão <c>false</c>.
    /// </summary>
    public bool AllowTrustServerCertificate { get; set; }

    /// <summary>Tempo limite dos comandos, em segundos (1 a 600).</summary>
    public int CommandTimeoutSeconds { get; set; } = DefaultCommandTimeoutSeconds;

    /// <summary>Itens máximos por página na listagem (1 a 10.000).</summary>
    public int MaxPageSize { get; set; } = 100;

    /// <summary>Itens máximos pulados pela listagem paginada (padrão 10.000).</summary>
    public int MaxPageOffset { get; set; } = 10_000;

    /// <summary>Ordenação por texto em entidade sem nenhuma propriedade [Sortable]. Padrão false.</summary>
    public bool AllowUnmarkedSortBy { get; set; }

    /// <summary>Registros máximos da busca por critérios (padrão 1.000).</summary>
    public int MaxFindResults { get; set; } = 1_000;

    /// <summary>Linhas máximas lidas por uma leitura complexa Dapper (padrão 10.000).</summary>
    public int MaxQueryRows { get; set; } = 10_000;

    /// <summary>Entidades excluídas logicamente por propagação num único salvamento (padrão 10.000).</summary>
    public int MaxSoftDeletePropagation { get; set; } = 10_000;

    /// <summary>Novas tentativas em falha transitória (0 a 5; padrão 2), só em leituras fora de transação.</summary>
    public int TransientRetryCount { get; set; } = 2;

    /// <summary>Espera antes da primeira nova tentativa (padrão 200 ms).</summary>
    public TimeSpan TransientRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Circuit breaker da abertura de conexões.</summary>
    public OrmCircuitBreakerOptions CircuitBreaker { get; set; } = new();

    /// <summary>Recusa, nas leituras complexas, SQL que não seja somente leitura. Padrão true.</summary>
    public bool EnforceReadOnlyQueries { get; set; } = true;

    /// <summary>Identificador do registro nos logs de auditoria.</summary>
    public IdentifierLogMode IdentifierLogMode { get; set; } = IdentifierLogMode.Auto;

    /// <summary>
    /// Habilita a gravação da Trilha Histórica de Auditoria (Audit Trail) para entidades marcadas com [Auditable]. Padrão true.
    /// </summary>
    public bool EnableHistoricalAudit { get; set; } = true;

    /// <summary>Nome da tabela de auditoria histórica. Padrão "OrmAuditRecords".</summary>
    public string AuditLogTableName { get; set; } = "OrmAuditRecords";

    /// <summary>Dialeto de banco de dados configurado pelo provedor.</summary>
    public ISqlDialect? Dialect { get; set; }

    /// <summary>Segredo usado pela conexão do tipo informado.</summary>
    internal string SecretNameFor(bool readOnly) => readOnly ? ReadOnlyConnectionSecretName ?? ConnectionSecretName : ConnectionSecretName;

    /// <summary>Valida as opções; lança <see cref="InvalidConfigurationException"/> se algo estiver incorreto.</summary>
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
        if (MaxPageOffset is < 0 or > 100_000_000)
            throw new InvalidConfigurationException("TecOrm:" + nameof(MaxPageOffset));
        if (MaxFindResults is < 1 or > 100_000)
            throw new InvalidConfigurationException("TecOrm:" + nameof(MaxFindResults));
        if (MaxQueryRows is < 1 or > 1_000_000)
            throw new InvalidConfigurationException("TecOrm:" + nameof(MaxQueryRows));
        if (MaxSoftDeletePropagation is < 1 or > 1_000_000)
            throw new InvalidConfigurationException("TecOrm:" + nameof(MaxSoftDeletePropagation));
        if (TransientRetryCount is < 0 or > 5)
            throw new InvalidConfigurationException("TecOrm:" + nameof(TransientRetryCount));
        if (TransientRetryDelay < TimeSpan.FromMilliseconds(10) || TransientRetryDelay > TimeSpan.FromSeconds(10))
            throw new InvalidConfigurationException("TecOrm:" + nameof(TransientRetryDelay));
        if (!Enum.IsDefined(IdentifierLogMode))
            throw new InvalidConfigurationException("TecOrm:" + nameof(IdentifierLogMode));
        if (CircuitBreaker is null)
            throw new InvalidConfigurationException("TecOrm:" + nameof(CircuitBreaker));
        CircuitBreaker.Validate();
    }
}

/// <summary>Circuit breaker da abertura de conexões.</summary>
public sealed class OrmCircuitBreakerOptions
{
    /// <summary>Liga o circuit breaker. Padrão: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Proporção de falhas que abre o circuito. Padrão: 0.5.</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Mínimo de aberturas na janela para avaliar a proporção. Padrão: 10.</summary>
    public int MinimumThroughput { get; set; } = 10;

    /// <summary>Janela de amostragem. Padrão: 30 segundos.</summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Tempo com o circuito aberto antes da abertura de teste. Padrão: 30 segundos.</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (!Enabled)
            return;
        if (double.IsNaN(FailureRatio) || FailureRatio is <= 0 or > 1)
            throw new InvalidConfigurationException("TecOrm:CircuitBreaker:" + nameof(FailureRatio));
        if (MinimumThroughput is < 2 or > 10_000)
            throw new InvalidConfigurationException("TecOrm:CircuitBreaker:" + nameof(MinimumThroughput));
        if (SamplingDuration < TimeSpan.FromMilliseconds(500) || SamplingDuration > TimeSpan.FromHours(1))
            throw new InvalidConfigurationException("TecOrm:CircuitBreaker:" + nameof(SamplingDuration));
        if (BreakDuration < TimeSpan.FromMilliseconds(500) || BreakDuration > TimeSpan.FromHours(1))
            throw new InvalidConfigurationException("TecOrm:CircuitBreaker:" + nameof(BreakDuration));
    }
}
