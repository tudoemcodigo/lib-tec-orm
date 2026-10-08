using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using TEC.ORM.SqlServer.Internal;
using TEC.ORM.SqlServer.Security;

namespace TEC.ORM.SqlServer.HealthChecks;

/// <summary>
/// Health check do banco: abre uma conexão pelo <see cref="IOrmConnectionSecurity"/> (segredo do TEC.Vault + política) e executa
/// <c>SELECT 1</c>. A descrição traz só o código do erro, nunca servidor, banco ou mensagem do SqlClient.
/// </summary>
internal sealed class OrmHealthCheck(IOrmConnectionSecurity security, ILogger<OrmHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var opened = await security.OpenConnectionAsync(OrmConnectionKind.ReadWrite, cancellationToken).ConfigureAwait(false);
        if (opened.IsFailure)
        {
            OrmLog.HealthCheckFailed(logger, opened.Error!.Code);
            return new HealthCheckResult(context.Registration.FailureStatus, opened.Error.Code);
        }

        var connection = opened.Value;
        await using (connection.ConfigureAwait(false))
        {
            try
            {
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.CommandText = "SELECT 1";
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    return HealthCheckResult.Healthy();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var translation = OrmExceptionTranslator.Translate(exception);
                OrmLog.HealthCheckFailed(logger, translation.Error.Code);
                return new HealthCheckResult(context.Registration.FailureStatus, translation.Error.Code);
            }
        }
    }
}

/// <summary>Registro do health check do TEC.ORM.</summary>
public static class OrmHealthChecksBuilderExtensions
{
    /// <summary>Tag de readiness (a mesma do TEC.Observability: <c>/health/ready</c>).</summary>
    public const string ReadyTag = "ready";

    /// <summary>Tag de banco de dados (a mesma do TEC.Observability).</summary>
    public const string DatabaseTag = "database";

    /// <summary>
    /// Adiciona o health check do banco. Com as tags padrão (<c>ready</c> e <c>database</c>) entra no readiness do
    /// TEC.Observability sem configuração; informar <paramref name="tags"/> substitui as duas.
    /// </summary>
    public static IHealthChecksBuilder AddTecOrm(this IHealthChecksBuilder builder, string name = "tec-orm",
        HealthStatus failureStatus = HealthStatus.Unhealthy, IEnumerable<string>? tags = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(name,
            sp => ActivatorUtilities.CreateInstance<OrmHealthCheck>(sp),
            failureStatus, tags ?? [ReadyTag, DatabaseTag], timeout ?? TimeSpan.FromSeconds(5)));
    }
}
