namespace TEC.ORM.Auditing;

/// <summary>
/// Informações contextuais do ator e ambiente que executa a operação para registro na trilha de auditoria.
/// </summary>
public sealed record OrmAuditActorContext
{
    /// <summary>Identificador do autor (UserId, ClientId, ApiKeyId, WorkerId).</summary>
    public string? ActorId { get; init; }

    /// <summary>Tipo do autor: "User", "M2M", "ApiKey", "System" ou "Anonymous".</summary>
    public string ActorType { get; init; } = "Anonymous";

    /// <summary>Nome amigável, razão social do client ou e-mail do autor quando disponível.</summary>
    public string? ActorName { get; init; }

    /// <summary>Tenant da operação.</summary>
    public string? TenantId { get; init; }

    /// <summary>W3C TraceId distribuído.</summary>
    public string? TraceId { get; init; }

    /// <summary>CorrelationId da requisição.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>IP do cliente chamador.</summary>
    public string? ClientIp { get; init; }

    /// <summary>Rota HTTP, tópico ou método disparador.</summary>
    public string? Endpoint { get; init; }
}

/// <summary>
/// Fornecedor de contexto de auditoria (quem está chamando, de onde e por qual canal).
/// </summary>
public interface IOrmAuditContextProvider
{
    /// <summary>Obtém as informações contextuais do ator atual para auditoria.</summary>
    OrmAuditActorContext GetCurrentContext();
}
