using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Internal;
using TEC.ORM.EntityFrameworkCore.Security;

namespace TEC.ORM.EntityFrameworkCore.HealthChecks;

/// <summary>
/// Health check do banco: abre uma conexão pelo <see cref="IOrmConnectionSecurity"/> (segredo do TEC.Vault + política) e executa
/// a consulta de verificação do dialeto (<c>SELECT 1</c>; <c>SELECT 1 FROM DUAL</c> no Oracle). A descrição traz só o código do
/// erro, nunca servidor, banco ou mensagem do driver.
/// </summary>
public sealed class OrmHealthCheck(
    IOrmConnectionSecurity security,
    OrmOptions options,
    ILogger<OrmHealthCheck> logger,
    ISqlDialect? dialect = null)
    : IHealthCheck
{
    private readonly ISqlDialect _dialect = dialect ?? options.Dialect ?? throw new ArgumentException("Dialeto não informado para o health check.", nameof(dialect));

    /// <inheritdoc />
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
#pragma warning disable CA2100 // consulta fixa do dialeto (ISqlDialect.HealthCheckProbeQuery), sem entrada de usuário
                    command.CommandText = _dialect.HealthCheckProbeQuery;
#pragma warning restore CA2100
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    return HealthCheckResult.Healthy();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var translation = _dialect.ExceptionTranslator.Translate(exception);
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
    /// Adiciona o health check do banco do contexto principal. Com as tags padrão (<c>ready</c> e <c>database</c>) entra no
    /// readiness do TEC.Observability sem configuração; informar <paramref name="tags"/> substitui as duas.
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
