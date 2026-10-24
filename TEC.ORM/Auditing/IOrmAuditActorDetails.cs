namespace TEC.ORM.Auditing;

/// <summary>
/// Contrato opcional que a identidade da requisição pode implementar
/// para fornecer detalhes específicos de ator à trilha de auditoria (ex.: "ApiKey", "M2M", "System", "ClientIp", "Endpoint").
/// </summary>
public interface IOrmAuditActorDetails
{
    /// <summary>Tipo específico de ator (ex.: "User", "M2M", "ApiKey", "System").</summary>
    string? ActorType { get; }

    /// <summary>Nome de exibição amigável do ator ou da aplicação chamadora.</summary>
    string? ActorName { get; }

    /// <summary>Endereço IP do cliente chamador.</summary>
    string? ClientIp { get; }

    /// <summary>Endpoint ou rota HTTP executada.</summary>
    string? Endpoint { get; }
}
