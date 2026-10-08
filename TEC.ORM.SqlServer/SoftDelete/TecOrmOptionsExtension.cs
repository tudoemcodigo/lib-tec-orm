using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace TEC.ORM.SqlServer.SoftDelete;

/// <summary>
/// Extensão das opções do EF Core aplicada por <c>AddTecOrm</c>/<c>UseTecOrm</c>: registra as convenções do TEC.ORM (filtro
/// global de exclusão lógica e colunas de auditoria) no provedor de serviços interno do EF, sem depender de o contexto herdar de
/// <see cref="OrmDbContext"/> ou chamar <see cref="OrmModelConfigurationExtensions.AddTecOrmConventions(Microsoft.EntityFrameworkCore.ModelConfigurationBuilder)"/>.
/// </summary>
internal sealed class TecOrmOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) =>
        new EntityFrameworkServicesBuilder(services).TryAdd<IConventionSetPlugin, TecOrmConventionSetPlugin>();

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "TecOrmConventions ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["TecOrm:Conventions"] = "1";
    }
}

/// <summary>
/// Acrescenta as convenções do TEC.ORM (colunas de auditoria e filtro de exclusão lógica) ao conjunto de convenções do EF Core.
/// </summary>
internal sealed class TecOrmConventionSetPlugin : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        conventionSet.Add(new Auditing.AuditModelConvention());
        conventionSet.Add(new SoftDeleteQueryFilterConvention());
        return conventionSet;
    }
}
