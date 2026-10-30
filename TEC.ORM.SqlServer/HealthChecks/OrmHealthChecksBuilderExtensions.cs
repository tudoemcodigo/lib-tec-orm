using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using CoreHealthChecks = TEC.ORM.EntityFrameworkCore.HealthChecks.OrmHealthChecksBuilderExtensions;

namespace TEC.ORM.SqlServer.HealthChecks;

/// <summary>
/// Registro do health check do TEC.ORM no namespace do SQL Server (o mesmo da versão 0.0.1). Encaminha para
/// <see cref="CoreHealthChecks.AddTecOrm"/>, comum a todos os bancos.
/// </summary>
public static class OrmHealthChecksBuilderExtensions
{
    /// <summary>Tag de readiness (a mesma do TEC.Observability: <c>/health/ready</c>).</summary>
    public const string ReadyTag = CoreHealthChecks.ReadyTag;

    /// <summary>Tag de banco de dados (a mesma do TEC.Observability).</summary>
    public const string DatabaseTag = CoreHealthChecks.DatabaseTag;

    /// <summary>
    /// Adiciona o health check do banco: abre uma conexão pelo <c>IOrmConnectionSecurity</c> (segredo do TEC.Vault + política)
    /// e executa <c>SELECT 1</c>. Com as tags padrão (<c>ready</c> e <c>database</c>) entra no readiness do TEC.Observability
    /// sem configuração; informar <paramref name="tags"/> substitui as duas.
    /// </summary>
    public static IHealthChecksBuilder AddTecOrm(this IHealthChecksBuilder builder, string name = "tec-orm",
        HealthStatus failureStatus = HealthStatus.Unhealthy, IEnumerable<string>? tags = null, TimeSpan? timeout = null) =>
        CoreHealthChecks.AddTecOrm(builder, name, failureStatus, tags, timeout);
}
