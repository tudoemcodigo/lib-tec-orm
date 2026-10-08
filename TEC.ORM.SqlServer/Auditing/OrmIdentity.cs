using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Security;

namespace TEC.ORM.SqlServer.Auditing;

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
    /// O <c>AddTecOrm</c> já configura com o <see cref="ICurrentUser"/> do escopo de DI.
    /// </summary>
    public static DbContextOptionsBuilder UseTecOrmIdentity(this DbContextOptionsBuilder builder, Func<ICurrentUser?> currentUser)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(currentUser);
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new OrmIdentityOptionsExtension(currentUser));
        return builder;
    }
}
