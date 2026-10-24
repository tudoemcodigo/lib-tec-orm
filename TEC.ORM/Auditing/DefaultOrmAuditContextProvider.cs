using System.Diagnostics;
using TEC.Core.Security;

namespace TEC.ORM.Auditing;

/// <summary>
/// Fornecedor padrão de contexto de auditoria baseado em <see cref="ICurrentUser"/>, <see cref="IOrmAuditActorDetails"/> e <see cref="Activity.Current"/>.
/// </summary>
public sealed class DefaultOrmAuditContextProvider : IOrmAuditContextProvider
{
    private readonly Func<ICurrentUser?> _currentUserAccessor;
    private readonly Func<OrmAuditActorContext?>? _customContextAccessor;

    /// <summary>Cria o fornecedor padrão com o acessor da identidade atual.</summary>
    public DefaultOrmAuditContextProvider(Func<ICurrentUser?>? currentUserAccessor = null)
        : this(currentUserAccessor, null)
    {
    }

    /// <summary>Cria o fornecedor padrão com suporte a sobrescrita por acessor customizado.</summary>
    public DefaultOrmAuditContextProvider(
        Func<ICurrentUser?>? currentUserAccessor,
        Func<OrmAuditActorContext?>? customContextAccessor)
    {
        _currentUserAccessor = currentUserAccessor ?? (() => null);
        _customContextAccessor = customContextAccessor;
    }

    /// <inheritdoc />
    public OrmAuditActorContext GetCurrentContext()
    {
        var custom = _customContextAccessor?.Invoke();
        if (custom != null)
            return custom;

        var user = _currentUserAccessor();
        var activity = Activity.Current;

        string actorType = "Anonymous";
        string? actorId = user?.Id;
        string? actorName = null;
        string? tenantId = user?.TenantId;
        string? clientIp = null;
        string? endpoint = null;

        if (user is IOrmAuditActorDetails details)
        {
            if (!string.IsNullOrWhiteSpace(details.ActorType))
                actorType = details.ActorType;
            actorName = details.ActorName;
            clientIp = details.ClientIp;
            endpoint = details.Endpoint;
        }

        if (user != null && user.IsAuthenticated)
        {
            if (string.Equals(actorType, "Anonymous", StringComparison.OrdinalIgnoreCase))
            {
                actorType = user.Kind switch
                {
                    PrincipalKind.User => "User",
                    PrincipalKind.Application => ResolveApplicationActorType(user.Id),
                    PrincipalKind.System => "System",
                    _ => "Anonymous"
                };
            }
        }

        return new OrmAuditActorContext
        {
            ActorId = actorId,
            ActorType = actorType,
            ActorName = actorName,
            TenantId = tenantId,
            ClientIp = clientIp,
            Endpoint = endpoint,
            TraceId = activity?.TraceId.ToString(),
            CorrelationId = activity?.GetBaggageItem("correlation_id") ?? activity?.GetBaggageItem("x-correlation-id")
        };
    }

    private static string ResolveApplicationActorType(string? actorId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            return "M2M";

        if (actorId.StartsWith("apikey", StringComparison.OrdinalIgnoreCase) ||
            actorId.StartsWith("api-key", StringComparison.OrdinalIgnoreCase) ||
            actorId.StartsWith("key-", StringComparison.OrdinalIgnoreCase))
        {
            return "ApiKey";
        }

        return "M2M";
    }
}
