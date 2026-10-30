using TEC.Core.Domain;
using TEC.ORM.Entities;

namespace TEC.ORM.Auditing;

/// <summary>
/// Registro histórico imutável de auditoria (Audit Trail / Change Log).
/// Armazena quem realizou a operação, o que foi alterado, quando e onde ocorreu.
/// </summary>
public class OrmAuditRecord : IEntity<long>
{
    /// <summary>Identificador sequencial único do registro de auditoria.</summary>
    public long Id { get; set; }

    /// <summary>Nome da entidade auditada (ex.: Pedido, Cliente, Fatura).</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>Identificador da chave primária da entidade auditada em formato de texto.</summary>
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>Ação realizada: Created, Updated, Deleted ou SoftDeleted.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Valores das propriedades antes da alteração (formato JSON sanitizado). Nulo em criações.</summary>
    public string? OldValues { get; set; }

    /// <summary>Valores das propriedades após a alteração (formato JSON sanitizado). Nulo em exclusões físicas.</summary>
    public string? NewValues { get; set; }

    /// <summary>Lista das propriedades/colunas que foram afetadas pela operação (formato JSON).</summary>
    public string? AffectedColumns { get; set; }

    /// <summary>Identificador estável do autor (ex.: UserId, ClientId da aplicação M2M, ApiKeyId ou Nome do Processo/Worker).</summary>
    public string? ActorId { get; set; }

    /// <summary>Tipo do autor da operação: "User" (usuário final), "M2M" (aplicação cliente/tokens M2M), "ApiKey" (chave de API), "System" (job/worker em segundo plano) ou "Anonymous".</summary>
    public string ActorType { get; set; } = "Anonymous";

    /// <summary>Nome amigável, e-mail ou descrição do cliente/ator quando disponível.</summary>
    public string? ActorName { get; set; }

    /// <summary>Tenant da aplicação ao qual a operação pertence (ou nulo em sistemas single-tenant).</summary>
    public string? TenantId { get; set; }

    /// <summary>Identificador de rastreamento distribuído (W3C TraceId do OpenTelemetry / Activity).</summary>
    public string? TraceId { get; set; }

    /// <summary>Identificador de correlação da requisição (CorrelationId).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Endereço IP de origem da requisição quando originado via HTTP/Rede.</summary>
    public string? ClientIp { get; set; }

    /// <summary>Endpoint, rota HTTP ou fila de mensagens onde a operação foi disparada.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Instante UTC em que a gravação ocorreu.</summary>
    public DateTimeOffset TimestampUtc { get; set; }
}
