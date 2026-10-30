using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using TEC.ORM.EntityFrameworkCore.Auditing;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Idempotency;

namespace TEC.ORM.EntityFrameworkCore.SoftDelete;

/// <summary>
/// Extensão das opções do EF Core aplicada pelo registro do TEC.ORM: registra as convenções (filtro global de exclusão lógica,
/// colunas de auditoria, tabela da trilha histórica e collation da idempotência) no provedor de serviços interno do EF, sem
/// depender de o contexto herdar de <see cref="OrmDbContext"/> ou chamar
/// <see cref="OrmModelConfigurationExtensions.AddTecOrmConventions(Microsoft.EntityFrameworkCore.ModelConfigurationBuilder)"/>, e
/// guarda as opções e o dialeto do contexto (usados pelas convenções e pela idempotência).
/// </summary>
/// <param name="options">Opções do contexto (<c>null</c>: só as convenções).</param>
internal sealed class TecOrmOptionsExtension(OrmOptions? options = null) : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    /// <summary>Opções do TEC.ORM do contexto.</summary>
    public OrmOptions? Options { get; } = options;

    /// <summary>Dialeto do contexto.</summary>
    public ISqlDialect? Dialect => Options?.Dialect;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) =>
        new EntityFrameworkServicesBuilder(services).TryAdd<IConventionSetPlugin, TecOrmConventionSetPlugin>();

    public void Validate(IDbContextOptions options)
    {
    }

    /// <summary>Nome da tabela da trilha das opções do contexto, se ele foi registrado pelo TEC.ORM.</summary>
    internal static string? AuditTableNameFrom(IDbContextOptions? options) =>
        options?.FindExtension<TecOrmOptionsExtension>()?.Options?.AuditLogTableName;

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
/// Acrescenta as convenções do TEC.ORM ao conjunto de convenções do EF Core, com as opções e o banco do contexto.
/// </summary>
internal sealed class TecOrmConventionSetPlugin(IDbContextOptions options) : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        var database = OrmDatabaseProvider.Detect(options);
        conventionSet.Add(new AuditModelConvention(TecOrmOptionsExtension.AuditTableNameFrom(options), database));
        conventionSet.Add(new SoftDeleteQueryFilterConvention(database));
        conventionSet.Add(new IdempotencyModelConvention(database));
        return conventionSet;
    }
}
