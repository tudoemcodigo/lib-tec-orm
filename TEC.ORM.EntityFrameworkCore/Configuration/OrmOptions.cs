using TEC.Core.Exceptions;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.EntityFrameworkCore.Configuration;

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
    Omitted = 2,

    /// <summary>
    /// Padrão: decide pelo tipo do valor. Chaves técnicas (inteiros e <c>Guid</c>) como em <see cref="Plain"/>; <c>string</c> e
    /// demais tipos (chaves naturais, que podem ser dado pessoal: CPF, e-mail) como em <see cref="Hashed"/>. O autor da exclusão
    /// física (texto) só sai em claro quando é um <c>Guid</c> ou um número.
    /// </summary>
    Auto = 3
}

/// <summary>Opções do TEC.ORM (EF Core e Dapper), comuns a SQL Server, PostgreSQL, MySQL e Oracle.</summary>
/// <remarks>
/// Segurança: aqui só ficam <b>nomes</b> de segredos do TEC.Vault, nunca a string de conexão. Pode vir de appsettings sem risco.
/// </remarks>
public sealed class OrmOptions
{
    /// <summary>Tempo limite padrão dos comandos (segundos).</summary>
    public const int DefaultCommandTimeoutSeconds = 30;

    /// <summary>Nome padrão da tabela da trilha histórica (<see cref="AuditLogTableName"/>).</summary>
    public const string DefaultAuditLogTableName = "OrmAuditRecords";

    /// <summary>Padrão de <see cref="MaxAuditValueLength"/>.</summary>
    public const int DefaultMaxAuditValueLength = 1024;

    /// <summary>Nome do segredo no TEC.Vault com a string de conexão de leitura e escrita (EF Core). Obrigatório.</summary>
    public string ConnectionSecretName { get; set; } = string.Empty;

    /// <summary>
    /// Nome do segredo com a string de conexão das leituras complexas (Dapper). Recomendado: um login só com <c>SELECT</c>.
    /// <c>null</c> usa <see cref="ConnectionSecretName"/>.
    /// </summary>
    public string? ReadOnlyConnectionSecretName { get; set; }

    /// <summary>
    /// Nome da aplicação na conexão (ex.: <c>sys.dm_exec_sessions</c> no SQL Server, <c>pg_stat_activity</c> no PostgreSQL).
    /// O MySQL (MySql.Data) e o Oracle não têm a opção na string de conexão: lá ela não é aplicada.
    /// </summary>
    public string ApplicationName { get; set; } = "TEC.ORM";

    /// <summary>
    /// Aceita, no segredo, conexão sem validação completa do servidor: <c>TrustServerCertificate=True</c> no SQL Server,
    /// <c>SslMode=Require</c> no PostgreSQL, <c>SslMode=Preferred</c>/<c>Required</c> no MySQL e transporte sem TCPS (ou
    /// <c>SSL_SERVER_DN_MATCH</c> desligado) no Oracle. Padrão <c>false</c>: habilite só em desenvolvimento local (ex.: banco no
    /// Docker com certificado autoassinado). Gera o aviso 3103 uma vez por tipo de conexão.
    /// </summary>
    public bool AllowTrustServerCertificate { get; set; }

    /// <summary>Tempo limite dos comandos, em segundos (1 a 600), no EF Core e no Dapper.</summary>
    public int CommandTimeoutSeconds { get; set; } = DefaultCommandTimeoutSeconds;

    /// <summary>Itens máximos por página na listagem (1 a 10.000).</summary>
    public int MaxPageSize { get; set; } = 100;

    /// <summary>
    /// Itens máximos pulados pela listagem paginada (<c>(Page - 1) * PageSize</c>; 0 a 100.000.000; padrão 10.000). Página além
    /// disso falha com <c>ORM_ENTRADA_INVALIDA</c> antes de ir ao banco: o <c>OFFSET</c> profundo obriga o banco a ordenar e
    /// descartar todas as linhas anteriores a cada chamada, com o custo controlado por quem pede a página. Para percorrer listas
    /// longas, filtre por chave (ex.: <c>Id &gt; último</c>) com a busca por critérios.
    /// </summary>
    public int MaxPageOffset { get; set; } = 10_000;

    /// <summary>
    /// Ordenação por texto (<c>PageRequest.SortBy</c>) em entidade <b>sem</b> nenhuma propriedade <c>[Sortable]</c>. Padrão
    /// <c>false</c> (falha fechada): só a chave e as datas de auditoria (<c>CreatedAt</c>, <c>UpdatedAt</c>) são aceitas, para
    /// que um <c>?sort=</c> vindo da API não ordene por colunas sensíveis (hash, documento, autoria) e permita inferir valores pela
    /// ordem. <c>true</c>: qualquer propriedade escalar mapeada (exceto sombra, exclusão lógica e tokens de concorrência). Com ao
    /// menos um <c>[Sortable]</c> na entidade, só as marcadas valem, nos dois casos.
    /// </summary>
    public bool AllowUnmarkedSortBy { get; set; }

    /// <summary>
    /// Registros máximos da busca por critérios (1 a 100.000); acima disso a busca falha com <c>ORM_LIMITE_EXCEDIDO</c>. Também é
    /// o teto da exclusão física em lote de entidade <c>[Auditable]</c> com trilha histórica (cada linha removida vira um
    /// registro de auditoria).
    /// </summary>
    public int MaxFindResults { get; set; } = 1_000;

    /// <summary>
    /// Linhas máximas lidas por uma leitura complexa (<c>IOrmQueryExecutor</c>; 1 a 1.000.000). Acima disso a consulta falha
    /// com <c>ORM_LIMITE_EXCEDIDO</c> e o comando é cancelado no banco, sem carregar o resto na memória.
    /// </summary>
    public int MaxQueryRows { get; set; } = 10_000;

    /// <summary>
    /// Entidades excluídas logicamente por propagação num único salvamento (1 a 1.000.000; padrão 10.000). A propagação carrega
    /// os dependentes em cascata na memória (uma consulta por navegação de cada principal) e grava um <c>UPDATE</c> por linha:
    /// acima do limite, a exclusão falha com <c>ORM_LIMITE_EXCEDIDO</c> sem gravar nada, em vez de esgotar memória e tempo.
    /// Agregados maiores: exclua os dependentes em lotes antes do principal.
    /// </summary>
    public int MaxSoftDeletePropagation { get; set; } = 10_000;

    /// <summary>
    /// Novas tentativas em falha transitória (0 a 5; padrão 2), <b>só em leituras fora de transação</b> (repositório e
    /// leituras complexas): queda de conexão, failover, banco temporariamente indisponível e vítima de deadlock.
    /// Escritas e qualquer operação dentro de transação nunca são repetidas (repetir um <c>SaveChanges</c> ou um
    /// <c>COMMIT</c> de resultado desconhecido poderia duplicar a gravação); tempo esgotado também não (repetiria a consulta cara).
    /// </summary>
    /// <remarks>
    /// Não habilite o <c>EnableRetryOnFailure</c> do EF Core (parâmetro do provedor no <c>AddTecOrm...</c>): ele é
    /// incompatível com as transações do <c>IUnitOfWork</c> e da trilha histórica (o EF recusa iniciar a transação) e repetiria
    /// escritas.
    /// </remarks>
    public int TransientRetryCount { get; set; } = 2;

    /// <summary>
    /// Espera antes da primeira nova tentativa (10 ms a 10 s; padrão 200 ms). Dobra a cada tentativa, com variação aleatória
    /// de até 50% para que muitas instâncias não tentem ao mesmo tempo depois de um failover.
    /// </summary>
    public TimeSpan TransientRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Circuit breaker da abertura de conexões (ligado por padrão): com o banco fora do ar, as aberturas seguintes falham na
    /// hora com <c>ORM_CONEXAO_INDISPONIVEL</c>, sem esperar o tempo limite de conexão a cada requisição. Um circuito por
    /// contexto e tipo de conexão. Abrir conexão não executa comando, então vale também para escritas (nada é repetido).
    /// </summary>
    public OrmCircuitBreakerOptions CircuitBreaker { get; set; } = new();

    /// <summary>
    /// Recusa, nas leituras complexas, SQL que não seja somente leitura (defesa em profundidade). Padrão <c>true</c>. No
    /// PostgreSQL, MySQL e Oracle a leitura também roda numa transação <c>READ ONLY</c>, que vale mesmo com esta opção desligada.
    /// </summary>
    public bool EnforceReadOnlyQueries { get; set; } = true;

    /// <summary>
    /// Habilita a gravação da trilha histórica de auditoria (Audit Trail) para entidades marcadas com <c>[Auditable]</c>,
    /// inclusive na exclusão física. Padrão <c>true</c>. O modelo precisa mapear a tabela (<c>AddTecOrmHistoricalAudit</c>).
    /// </summary>
    public bool EnableHistoricalAudit { get; set; } = true;

    /// <summary>
    /// Nome da tabela da trilha histórica quando o modelo chama <c>modelBuilder.AddTecOrmHistoricalAudit()</c> sem
    /// <c>tableName</c> (1 a 128 caracteres; padrão <c>OrmAuditRecords</c>). Aplicado pelas convenções do TEC.ORM ao montar o
    /// modelo; um <c>tableName</c> passado ao <c>AddTecOrmHistoricalAudit</c> sempre vence. Trocar o nome gera migration.
    /// </summary>
    public string AuditLogTableName { get; set; } = DefaultAuditLogTableName;

    /// <summary>
    /// Tamanho máximo de cada valor gravado em <c>OldValues</c>/<c>NewValues</c> da trilha histórica (64 a 1.048.576 caracteres;
    /// padrão 1.024). Texto maior é cortado com a marca <c>…(+N)</c>; <c>byte[]</c> vira só o tamanho (<c>&lt;N bytes&gt;</c>).
    /// </summary>
    public int MaxAuditValueLength { get; set; } = DefaultMaxAuditValueLength;

    /// <summary>
    /// Identificador do registro (e autor da exclusão física) nos logs de auditoria. Padrão <see cref="IdentifierLogMode.Auto"/>:
    /// chaves técnicas em claro, chaves texto com HMAC.
    /// </summary>
    public IdentifierLogMode IdentifierLogMode { get; set; } = IdentifierLogMode.Auto;

    /// <summary>
    /// Dialeto do banco. Definido pelo registro de cada provedor (<c>AddTecOrm</c>, <c>AddTecOrmPostgreSql</c>,
    /// <c>AddTecOrmMySql</c>, <c>AddTecOrmOracle</c>); o valor informado em <c>configure</c> é substituído.
    /// </summary>
    public ISqlDialect? Dialect { get; set; }

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
        if (string.IsNullOrWhiteSpace(AuditLogTableName) || AuditLogTableName.Length > 128)
            throw new InvalidConfigurationException("TecOrm:" + nameof(AuditLogTableName));
        if (MaxAuditValueLength is < 64 or > 1_048_576)
            throw new InvalidConfigurationException("TecOrm:" + nameof(MaxAuditValueLength));
        if (!Enum.IsDefined(IdentifierLogMode))
            throw new InvalidConfigurationException("TecOrm:" + nameof(IdentifierLogMode));
        if (CircuitBreaker is null)
            throw new InvalidConfigurationException("TecOrm:" + nameof(CircuitBreaker));
        CircuitBreaker.Validate();
    }
}

/// <summary>
/// Circuit breaker da abertura de conexões (<see cref="OrmOptions.CircuitBreaker"/>).
/// </summary>
/// <remarks>
/// Contam como falha: erro do driver ao abrir (rede, failover, login, banco indisponível) e esgotamento do pool.
/// O circuito abre quando, dentro de <see cref="SamplingDuration"/>, houve pelo menos <see cref="MinimumThroughput"/> aberturas
/// e a proporção de falhas chegou a <see cref="FailureRatio"/>. Fica aberto por <see cref="BreakDuration"/>; depois deixa passar
/// uma abertura de teste: sucesso fecha o circuito, falha abre de novo.
/// </remarks>
public sealed class OrmCircuitBreakerOptions
{
    /// <summary>Liga o circuit breaker. Padrão: <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Proporção de falhas que abre o circuito (maior que 0, até 1). Padrão: 0,5.</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>Mínimo de aberturas na janela para avaliar a proporção (2 a 10.000). Padrão: 10.</summary>
    public int MinimumThroughput { get; set; } = 10;

    /// <summary>Janela de amostragem (0,5 segundo a 1 hora). Padrão: 30 segundos.</summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Tempo com o circuito aberto antes da abertura de teste (0,5 segundo a 1 hora). Padrão: 30 segundos.</summary>
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
