using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using TEC.Core.Security;
using TEC.ORM.SqlServer;
using TEC.ORM.SqlServer.Auditing;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.Diagnostics;
using TEC.ORM.SqlServer.SoftDelete;

namespace TEC.ORM.LoadTests.Infrastructure;

/// <summary>
/// TEC.ORM sobre o EF Core InMemory, montado como o <c>AddTecOrm</c> monta: opções, observabilidade e interceptores de exclusão
/// lógica e de auditoria <b>compartilhados</b> (singletons), um contexto por escopo com a identidade do escopo. Sem banco: roda
/// no CI a cada push.
/// </summary>
public sealed class InMemoryOrm
{
    private readonly InMemoryDatabaseRoot _root = new();
    private readonly string _database = Guid.NewGuid().ToString("N");
    private readonly SoftDeleteInterceptor _softDelete = new(TimeProvider.System);
    private readonly AuditInterceptor _audit = new(TimeProvider.System);

    public InMemoryOrm(Action<OrmOptions>? configure = null)
    {
        var options = new OrmOptions { ConnectionSecretName = "carga-inmemory" };
        configure?.Invoke(options);
        options.Validate();
        Options = options;
        Observability = new OrmOperationRunner(NullLogger<OrmOperationRunner>.Instance, options);
    }

    public OrmOptions Options { get; }

    public OrmOperationRunner Observability { get; }

    /// <summary>Contexto novo (um por escopo/requisição) sobre o mesmo banco em memória, com a identidade informada.</summary>
    public LoadContext NewContext(ICurrentUser? user = null)
    {
        var builder = new DbContextOptionsBuilder<LoadContext>()
            .UseInMemoryDatabase(_database, _root)
            .AddInterceptors(_softDelete, _audit);
        builder.UseTecOrmIdentity(() => user);
        return new LoadContext(builder.Options);
    }

    public OrmRepository<TEntity, TKey> Orm<TEntity, TKey>(DbContext context)
        where TEntity : class, ORM.Entities.IEntity<TKey>, ORM.Entities.ISoftDelete
        where TKey : notnull, IEquatable<TKey> =>
        new(context, Observability, Options);
}
