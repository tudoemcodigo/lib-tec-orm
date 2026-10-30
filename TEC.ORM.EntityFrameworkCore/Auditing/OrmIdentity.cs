using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Security;

namespace TEC.ORM.EntityFrameworkCore.Auditing;

/// <summary>
/// Extensão das opções do <c>DbContext</c> que diz de onde vem o <see cref="ICurrentUser"/> da operação (auditoria).
/// Não altera o provedor de serviços interno do EF Core (mesmo cache de modelo e de serviços para todas as instâncias).
/// </summary>
internal sealed class OrmIdentityOptionsExtension(Func<ICurrentUser?> currentUser) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public Func<ICurrentUser?> CurrentUser { get; } = currentUser;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "TecOrmIdentity ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["TecOrm:Identity"] = "1";
    }
}

/// <summary>Identidade da operação de um <c>DbContext</c> do TEC.ORM.</summary>
public static class OrmIdentity
{
    /// <summary>Identidade atual do contexto (<c>null</c> se o contexto não foi configurado pelo TEC.ORM ou não há <see cref="ICurrentUser"/>).</summary>
    public static ICurrentUser? Current(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetService<IDbContextOptions>().FindExtension<OrmIdentityOptionsExtension>()?.CurrentUser();
    }

    /// <summary>
    /// Informa a identidade da operação a um contexto montado à mão (testes, ferramentas, <c>AddDbContextFactory</c>).
    /// O registro do TEC.ORM (<c>AddTecOrm</c>, <c>AddTecOrmPostgreSql</c>, <c>AddTecOrmMySql</c>, <c>AddTecOrmOracle</c>) já
    /// configura com o <see cref="ICurrentUser"/> do escopo de DI.
    /// </summary>
    public static DbContextOptionsBuilder UseTecOrmIdentity(this DbContextOptionsBuilder builder, Func<ICurrentUser?> currentUser)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(currentUser);
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new OrmIdentityOptionsExtension(currentUser));
        return builder;
    }

    /// <summary>
    /// Resolve o <see cref="ICurrentUser"/> num escopo próprio e devolve uma cópia imutável dos dados (o escopo, e com ele uma
    /// implementação descartável, é encerrado aqui). <c>null</c> sem <see cref="ICurrentUser"/> registrado.
    /// </summary>
    /// <remarks>
    /// Usado onde não há o escopo da requisição (fábrica de contextos, serviço singleton): vale para implementações ambientais
    /// (as do TEC.Security, que leem o usuário do <c>HttpContext</c> ou do <c>AsyncLocal</c>).
    /// </remarks>
    internal static ICurrentUser? SnapshotCurrentUser(IServiceScopeFactory scopes)
    {
        using var scope = scopes.CreateScope();
        var user = scope.ServiceProvider.GetService<ICurrentUser>();
        return user is null ? null : new CurrentUserSnapshot(user.Kind, user.Id, user.TenantId);
    }

    /// <summary>Cópia imutável da identidade de uma operação.</summary>
    private sealed record CurrentUserSnapshot(PrincipalKind Kind, string? Id, string? TenantId) : ICurrentUser;
}

/// <summary>
/// Identidade da gravação em curso para o <c>DefaultOrmAuditContextProvider</c> registrado pelo TEC.ORM: durante o
/// salvamento, a trilha histórica usa o mesmo <see cref="ICurrentUser"/> do contexto (<see cref="OrmIdentity.Current"/>) que
/// preenche <c>CreatedBy</c>/<c>UpdatedBy</c>/<c>DeletedBy</c>.
/// </summary>
/// <remarks>
/// O provedor de contexto de auditoria é singleton e a pergunta (<c>GetCurrentContext()</c>) não recebe o <c>DbContext</c>:
/// o interceptor publica o contexto aqui só durante a chamada síncrona e limpa em seguida. Fora de um salvamento, o acessor
/// resolve o <see cref="ICurrentUser"/> num escopo próprio (<see cref="OrmIdentity.SnapshotCurrentUser"/>).
/// </remarks>
internal static class OrmAuditIdentity
{
    [ThreadStatic]
    private static DbContext? t_context;

    /// <summary>Acessor da identidade para o <c>DefaultOrmAuditContextProvider</c> registrado no DI.</summary>
    public static Func<ICurrentUser?> CreateAccessor(IServiceScopeFactory scopes) =>
        () => t_context is { } context ? OrmIdentity.Current(context) : OrmIdentity.SnapshotCurrentUser(scopes);

    /// <summary>Publica o contexto do salvamento até o descarte do retorno.</summary>
    public static Scope Enter(DbContext context)
    {
        var previous = t_context;
        t_context = context;
        return new Scope(previous);
    }

    /// <summary>Restaura o contexto anterior (aninhamento).</summary>
    public readonly struct Scope(DbContext? previous) : IDisposable
    {
        public void Dispose() => t_context = previous;
    }
}
