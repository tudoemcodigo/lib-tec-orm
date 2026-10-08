using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using TEC.Core.Common.Results;
using TEC.Core.Responses.Pagination;
using TEC.ORM.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.Entities;
using TEC.ORM.Mapping;
using TEC.ORM.Paging;
using TEC.ORM.Specifications;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.Diagnostics;
using TEC.ORM.SqlServer.SoftDelete;

namespace TEC.ORM.SqlServer;

/// <summary>
/// Implementação de <see cref="IOrmRepository{TEntity, TKey}"/> com EF Core. Genérica para qualquer entidade e tipo de chave;
/// herde para acrescentar operações específicas (os membros são <c>virtual</c> e o <see cref="Context"/> fica acessível).
/// </summary>
/// <remarks>
/// <para>Leituras sem rastreamento (<c>AsNoTracking</c>). Escritas gravadas na hora (<c>SaveChangesAsync</c>); se a gravação
/// falhar, a entidade é desanexada do contexto, para não ser regravada pela próxima operação do mesmo escopo.</para>
/// <para>Segurança: toda consulta é LINQ (parâmetros gerados pelo EF); a ordenação por texto (<see cref="PageRequest.SortBy"/>)
/// só aceita propriedades escalares mapeadas e permitidas (<see cref="SortableAttribute"/>; nunca sombra, exclusão lógica ou
/// token de concorrência).</para>
/// <para>O construtor exige o filtro global de exclusão lógica no modelo (<c>AddTecOrm</c>, <see cref="SoftDelete.OrmDbContext"/>
/// ou <c>AddTecOrmConventions</c>): sem ele, registros excluídos voltariam nas consultas, então a ausência é
/// <see cref="InvalidOperationException"/>.</para>
/// </remarks>
/// <typeparam name="TEntity">Entidade (com exclusão lógica).</typeparam>
/// <typeparam name="TKey">Tipo da chave primária.</typeparam>
public class OrmRepository<TEntity, TKey>(DbContext context, IOrmOperationRunner runner, OrmOptions options)
    : IOrmRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>, ISoftDelete
    where TKey : notnull, IEquatable<TKey>
{
    private static readonly string Target = typeof(TEntity).Name;

    /// <summary>Contexto do EF Core.</summary>
    protected DbContext Context { get; } = SoftDeleteQueryFilterConvention.EnsureFilter(context ?? throw new ArgumentNullException(nameof(context)), typeof(TEntity));

    /// <summary>Conjunto da entidade (com o filtro de exclusão lógica).</summary>
    protected DbSet<TEntity> Set => Context.Set<TEntity>();

    /// <inheritdoc />
    public virtual Task<Result<TEntity>> CreateAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        var operation = Write("create");
        return runner.ExecuteAsync(operation, async Task<Result<TEntity>> (token) =>
        {
            if (entity is null)
                return OrmErrors.InvalidInput(nameof(entity), "A entidade é obrigatória.");

            var entry = Set.Add(entity);
            // Criação nunca nasce excluída, mesmo que o objeto venha com os campos preenchidos
            entry.Property(nameof(ISoftDelete.IsDeleted)).CurrentValue = false;
            entry.Property(nameof(ISoftDelete.DeletedAt)).CurrentValue = null;
            await SaveAsync(entry, token).ConfigureAwait(false);
            operation.Identifier = entity.Id;
            return entity;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public virtual Task<Result<TEntity>> GetByIdAsync(TKey id, CancellationToken cancellationToken = default)
    {
        var operation = Read("get", id);
        return runner.ExecuteAsync(operation, async Task<Result<TEntity>> (token) =>
        {
            if (id is null)
                return OrmErrors.InvalidInput(nameof(id), "O identificador é obrigatório.");

            var entity = await Set.AsNoTracking().FirstOrDefaultAsync(HasId(id), token).ConfigureAwait(false);
            return entity is null ? OrmErrors.NotFound() : Result<TEntity>.Success(entity);
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>Grava <b>só a raiz</b>: a entidade é anexada como <c>Modified</c> junto com os seus tipos <i>owned</i> (que fazem
    /// parte dela; itens de <c>OwnsMany</c> sem chave são inseridos, como no <c>DbSet.Update</c>). Navegações para outras
    /// entidades são ignoradas: não são gravadas nem reativadas (um filho excluído logicamente continua excluído). Grave-as
    /// pelo repositório de cada uma.</para>
    /// <para>Concorrência otimista: os tokens (<c>rowversion</c>, <c>[ConcurrencyCheck]</c>) do objeto recebido são o valor
    /// esperado no banco. Se outra instância com o mesmo <c>Id</c> já estiver rastreada no contexto, ela é desanexada (com os
    /// seus <i>owned</i>) e a recebida entra no lugar, para que a verificação use os tokens recebidos. Versão antiga resulta em
    /// <c>ORM_CONCORRENCIA</c> com a entidade rastreada ou desanexada.</para>
    /// <para>Se a instância recebida já é a rastreada, vale o rastreador: o salvamento é o do contexto (o que mais estiver
    /// alterado nele também é gravado) e os tokens esperados são os carregados.</para>
    /// </remarks>
    public virtual Task<Result<TEntity>> UpdateAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        var operation = Write("update", entity is null ? null : entity.Id);
        return runner.ExecuteAsync(operation, async Task<Result<TEntity>> (token) =>
        {
            if (entity is null)
                return OrmErrors.InvalidInput(nameof(entity), "A entidade é obrigatória.");

            // Não existe ou já foi excluído: não atualiza (e não "ressuscita" o registro)
            if (!await Set.AnyAsync(HasId(entity.Id), token).ConfigureAwait(false))
                return OrmErrors.NotFound();

            var tracked = Set.Local.FindEntry(entity.Id);
            if (tracked is not null && !ReferenceEquals(tracked.Entity, entity))
            {
                // Outra instância rastreada: sai do rastreador e a recebida entra no lugar (tokens de concorrência recebidos)
                DetachWithOwned(tracked);
                tracked = null;
            }
            var entry = tracked ?? AttachRootAsModified(entity);

            // Exclusão lógica só pelo DeleteAsync
            ProtectSoftDeleteColumns(entry);
            await SaveAsync(entry, token).ConfigureAwait(false);
            return entry.Entity;
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Propaga a exclusão lógica: dependentes em cascata que também são <see cref="ISoftDelete"/> são excluídos logicamente
    /// (mesmo <c>DeletedAt</c>; carregados pela navegação se não estiverem rastreados); tipos <i>owned</i> e dependentes que
    /// não são <see cref="ISoftDelete"/> ficam como estão. A cascata do rastreador do EF Core (<c>CascadeDeleteTiming</c>) fica
    /// desligada durante a operação, para que nada seja marcado como excluído nem tenha a chave estrangeira anulada.
    /// </remarks>
    public virtual Task<Result> DeleteAsync(TKey id, CancellationToken cancellationToken = default)
    {
        var operation = Write("delete", id);
        return runner.ExecuteAsync(operation, async Task<Result> (token) =>
        {
            if (id is null)
                return OrmErrors.InvalidInput(nameof(id), "O identificador é obrigatório.");

            var entity = Set.Local.FindEntry(id)?.Entity
                         ?? await Set.FirstOrDefaultAsync(HasId(id), token).ConfigureAwait(false);
            if (entity is null || entity.IsDeleted)
                return OrmErrors.NotFound();

            var tracker = Context.ChangeTracker;
            var cascadeTiming = tracker.CascadeDeleteTiming;
            tracker.CascadeDeleteTiming = CascadeTiming.Never;
            try
            {
                // O SoftDeleteInterceptor converte em exclusão lógica (e propaga) ao salvar
                var entry = Set.Remove(entity);
                await SaveAsync(entry, token, discardPropagation: true).ConfigureAwait(false);
            }
            finally
            {
                tracker.CascadeDeleteTiming = cascadeTiming;
            }
            return Result.Success();
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para><c>DELETE</c> direto no banco (<c>ExecuteDeleteAsync</c>): não carrega a entidade, não passa pelo rastreador nem
    /// pela conversão em exclusão lógica e participa da transação corrente (<c>IUnitOfWork</c>). Só o filtro de exclusão lógica
    /// é desligado; os demais filtros globais da aplicação continuam valendo. Se a instância estiver rastreada no contexto,
    /// ela é desanexada (com os <i>owned</i>); dependentes rastreados removidos pela cascata do banco não são desanexados.</para>
    /// <para>Exige um provedor relacional (o InMemory não executa <c>ExecuteDelete</c>) e não suporta herança TPT/TPC.</para>
    /// </remarks>
    [HardDelete]
    public virtual Task<Result> HardDeleteAsync(TKey id, CancellationToken cancellationToken = default)
    {
        var operation = HardDelete("hard-delete", id);
        return runner.ExecuteAsync(operation, async Task<Result> (token) =>
        {
            if (id is null)
                return OrmErrors.InvalidInput(nameof(id), "O identificador é obrigatório.");

            int rows = await IncludingSoftDeleted().Where(HasId(id)).ExecuteDeleteAsync(token).ConfigureAwait(false);
            if (rows == 0)
                return OrmErrors.NotFound();

            // A linha não existe mais: a instância rastreada não pode ser regravada pela próxima operação do escopo
            if (Set.Local.FindEntry(id) is { } tracked)
                DetachWithOwned(tracked);
            return Result.Success();
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Um único <c>DELETE ... WHERE</c> no banco, como em <see cref="HardDeleteAsync(TKey, CancellationToken)"/>. Instâncias
    /// rastreadas no contexto não são desanexadas (o critério pode não ser avaliável em memória): não as regrave.
    /// </remarks>
    [HardDelete]
    public virtual Task<Result<long>> HardDeleteAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default) =>
        runner.ExecuteAsync(HardDelete("hard-delete-many"), async Task<Result<long>> (token) =>
        {
            // Sem critério seria "excluir tudo": recusado
            if (specification?.Criteria is null)
                return OrmErrors.InvalidInput(nameof(specification), "O critério é obrigatório na exclusão física.");

            long rows = await IncludingSoftDeleted().Where(specification.Criteria).ExecuteDeleteAsync(token).ConfigureAwait(false);
            return Result<long>.Success(rows);
        }, cancellationToken);

    /// <inheritdoc />
    public virtual Task<Result<IReadOnlyList<TEntity>>> FindAsync(ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default) =>
        runner.ExecuteAsync(Read("find"), token => FindCoreAsync(specification, static query => query, token), cancellationToken);

    /// <inheritdoc />
    public virtual Task<Result<PagedResult<TEntity>>> ListAsync(PageRequest page, ISpecification<TEntity>? specification = null,
        CancellationToken cancellationToken = default) =>
        runner.ExecuteAsync(Read("list"), token => ListCoreAsync(page, specification, static query => query, token), cancellationToken);

    // ---------- Projeção para DTO (mapeamento gerado: SELECT só das colunas do DTO) ----------

    /// <inheritdoc />
    public virtual Task<Result<TDto>> GetByIdAsync<TDto>(TKey id, CancellationToken cancellationToken = default)
        where TDto : class, IDtoMap<TEntity, TDto> =>
        runner.ExecuteAsync(Read("get-dto", id), async Task<Result<TDto>> (token) =>
        {
            if (id is null)
                return OrmErrors.InvalidInput(nameof(id), "O identificador é obrigatório.");

            var dto = await Set.AsNoTracking().Where(HasId(id)).Select(TDto.Projection).FirstOrDefaultAsync(token).ConfigureAwait(false);
            return dto is null ? OrmErrors.NotFound() : Result<TDto>.Success(dto);
        }, cancellationToken);

    /// <inheritdoc />
    public virtual Task<Result<IReadOnlyList<TDto>>> FindAsync<TDto>(ISpecification<TEntity> specification,
        CancellationToken cancellationToken = default) where TDto : class, IDtoMap<TEntity, TDto> =>
        runner.ExecuteAsync(Read("find-dto"), token => FindCoreAsync(specification, static query => query.Select(TDto.Projection), token),
            cancellationToken);

    /// <inheritdoc />
    public virtual Task<Result<PagedResult<TDto>>> ListAsync<TDto>(PageRequest page, ISpecification<TEntity>? specification = null,
        CancellationToken cancellationToken = default) where TDto : class, IDtoMap<TEntity, TDto> =>
        runner.ExecuteAsync(Read("list-dto"), token => ListCoreAsync(page, specification, static query => query.Select(TDto.Projection), token),
            cancellationToken);

    /// <summary>Busca: filtro e ordenação da especificação, limite de resultados e projeção (entidade ou DTO).</summary>
    private async Task<Result<IReadOnlyList<TResult>>> FindCoreAsync<TResult>(ISpecification<TEntity>? specification,
        Func<IQueryable<TEntity>, IQueryable<TResult>> project, CancellationToken cancellationToken)
    {
        if (specification is null)
            return OrmErrors.InvalidInput(nameof(specification), "A especificação é obrigatória.");

        int limit = options.MaxFindResults;
        var query = project(ApplyOrdering(ApplyCriteria(specification), specification.OrderBy));
        var items = await query.Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (items.Count > limit)
            return OrmErrors.TooManyResults(limit);
        return Result<IReadOnlyList<TResult>>.Success(items);
    }

    /// <summary>
    /// Listagem: valida a página e a ordenação (sempre por propriedades da entidade), conta, ordena, pagina e projeta.
    /// </summary>
    /// <remarks>
    /// A contagem e a página são duas consultas, fora de transação (de propósito: uma transação só para a leitura seguraria
    /// bloqueios). Sob escrita concorrente, o total pode divergir dos itens da página (ex.: total 10 e a última página vazia,
    /// ou um item a mais); trate <c>TotalItems</c> como aproximado nesse cenário.
    /// </remarks>
    private async Task<Result<PagedResult<TResult>>> ListCoreAsync<TResult>(PageRequest? page, ISpecification<TEntity>? specification,
        Func<IQueryable<TEntity>, IQueryable<TResult>> project, CancellationToken cancellationToken)
    {
        if (page is null)
            return OrmErrors.InvalidInput(nameof(page), "A página é obrigatória.");
        if (page.Validate(options.MaxPageSize) is { } invalid)
            return invalid;

        LambdaExpression? sortBy = null;
        if (page.SortBy is not null)
        {
            sortBy = SortablePropertySelector(page.SortBy);
            if (sortBy is null)
                return OrmErrors.InvalidInput("sortBy", "Ordenação por um campo não permitido.");
        }

        var filtered = ApplyCriteria(specification);
        long total = await filtered.LongCountAsync(cancellationToken).ConfigureAwait(false);
        if (total == 0 || page.Offset >= total)
            return new PagedResult<TResult>([], page.Page, page.PageSize, total);

        var ordering = sortBy is null ? [] : new List<SortExpression> { new(sortBy, page.Descending) };
        if (specification is not null)
            ordering.AddRange(specification.OrderBy);
        var items = await project(ApplyOrdering(filtered, ordering).Skip(page.Offset).Take(page.PageSize))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<TResult>(items, page.Page, page.PageSize, total);
    }

    /// <inheritdoc />
    public virtual Task<Result<bool>> ExistsAsync(TKey id, CancellationToken cancellationToken = default) =>
        runner.ExecuteAsync(Read("exists", id), async Task<Result<bool>> (token) =>
        {
            if (id is null)
                return OrmErrors.InvalidInput(nameof(id), "O identificador é obrigatório.");
            return Result<bool>.Success(await Set.AnyAsync(HasId(id), token).ConfigureAwait(false));
        }, cancellationToken);

    /// <inheritdoc />
    public virtual Task<Result<bool>> ExistsAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default) =>
        runner.ExecuteAsync(Read("exists"), async Task<Result<bool>> (token) =>
        {
            if (specification is null)
                return OrmErrors.InvalidInput(nameof(specification), "A especificação é obrigatória.");
            return Result<bool>.Success(await ApplyCriteria(specification).AnyAsync(token).ConfigureAwait(false));
        }, cancellationToken);

    /// <inheritdoc />
    public virtual Task<Result<long>> CountAsync(ISpecification<TEntity>? specification = null, CancellationToken cancellationToken = default) =>
        runner.ExecuteAsync(Read("count"), async Task<Result<long>> (token) =>
            Result<long>.Success(await ApplyCriteria(specification).LongCountAsync(token).ConfigureAwait(false)),
            cancellationToken);

    /// <summary>Consulta base: sem rastreamento, com filtro e navegações da especificação.</summary>
    protected virtual IQueryable<TEntity> ApplyCriteria(ISpecification<TEntity>? specification)
    {
        IQueryable<TEntity> query = Set.AsNoTracking();
        if (specification is null)
            return query;

        foreach (string include in specification.Includes)
            query = query.Include(include);
        if (specification.Criteria is not null)
            query = query.Where(specification.Criteria);
        return query;
    }

    /// <summary>
    /// Consulta que enxerga também os excluídos logicamente (só o filtro de exclusão lógica é desligado; os demais filtros
    /// globais da aplicação continuam valendo).
    /// </summary>
    protected IQueryable<TEntity> IncludingSoftDeleted() => SoftDeleteQueryFilterConvention.IgnoreSoftDeleteFilter(Set, Set.EntityType, Context);

    /// <summary>Filtro pelo identificador (o valor vira parâmetro na consulta).</summary>
    protected static Expression<Func<TEntity, bool>> HasId(TKey id)
    {
        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var member = Expression.Property(parameter, nameof(IEntity<TKey>.Id));
        // Closure: o EF trata o valor capturado como parâmetro (@__id_0), nunca como literal no SQL
        Expression<Func<TKey>> captured = () => id;
        var comparison = KeyHasEqualityOperator
            ? Expression.Equal(member, captured.Body)
            // Tipo de chave sem operador ==: IEquatable<TKey>.Equals
            : (Expression)Expression.Call(member, typeof(IEquatable<TKey>).GetMethod(nameof(IEquatable<TKey>.Equals))!, captured.Body);
        return Expression.Lambda<Func<TEntity, bool>>(comparison, parameter);
    }

    /// <summary>Decidido uma vez por tipo de chave (antes: uma exceção capturada a cada chamada para chaves sem <c>==</c>).</summary>
    private static readonly bool KeyHasEqualityOperator = HasEqualityOperator();

    private static bool HasEqualityOperator()
    {
        try
        {
            var key = Expression.Parameter(typeof(TKey));
            _ = Expression.Equal(key, key);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IQueryable<TEntity> ApplyOrdering(IQueryable<TEntity> query, IReadOnlyList<SortExpression> ordering)
    {
        bool first = true;
        foreach (var sort in ordering)
        {
            query = OrderBy(query, sort.KeySelector, sort.Descending, first);
            first = false;
        }
        // Desempate pelo identificador: paginação estável (sem itens repetidos ou pulados entre páginas)
        Expression<Func<TEntity, TKey>> byId = e => e.Id;
        return OrderBy(query, byId, descending: false, first);
    }

    private static IQueryable<TEntity> OrderBy(IQueryable<TEntity> query, LambdaExpression keySelector, bool descending, bool first)
    {
        string method = (first, descending) switch
        {
            (true, false) => nameof(Queryable.OrderBy),
            (true, true) => nameof(Queryable.OrderByDescending),
            (false, false) => nameof(Queryable.ThenBy),
            (false, true) => nameof(Queryable.ThenByDescending)
        };
        var call = Expression.Call(typeof(Queryable), method, [typeof(TEntity), keySelector.ReturnType], query.Expression,
            Expression.Quote(keySelector));
        return query.Provider.CreateQuery<TEntity>(call);
    }

    /// <summary>
    /// Seletor <c>e => EF.Property&lt;T&gt;(e, "Nome")</c> para uma propriedade escalar ordenável (sem diferenciar maiúsculas);
    /// <c>null</c> se o nome não for permitido (whitelist: o texto do usuário nunca chega ao SQL).
    /// </summary>
    /// <remarks>
    /// Com ao menos uma propriedade <see cref="SortableAttribute"/> na entidade, só as marcadas são aceitas; sem nenhuma, qualquer
    /// propriedade escalar mapeada. Sempre recusadas: propriedades sombra, colunas da exclusão lógica e tokens de concorrência.
    /// </remarks>
    private LambdaExpression? SortablePropertySelector(string name)
    {
        var candidates = Context.Model.FindEntityType(typeof(TEntity))?.GetProperties().Where(IsSortCandidate).ToList();
        if (candidates is null)
            return null;
        if (candidates.Any(IsMarkedSortable))
            candidates = candidates.Where(IsMarkedSortable).ToList();

        var property = candidates.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (property is null)
            return null;

        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var access = Expression.Call(typeof(EF), nameof(EF.Property), [property.ClrType], parameter, Expression.Constant(property.Name));
        return Expression.Lambda(access, parameter);
    }

    /// <summary>Propriedades que nunca entram na ordenação por texto: sombra, exclusão lógica e tokens de concorrência.</summary>
    private static bool IsSortCandidate(IProperty property) =>
        !property.IsShadowProperty() && !property.IsConcurrencyToken &&
        property.Name is not (nameof(ISoftDelete.IsDeleted) or nameof(ISoftDelete.DeletedAt));

    private static bool IsMarkedSortable(IProperty property) =>
        property.PropertyInfo is { } info && Attribute.IsDefined(info, typeof(SortableAttribute), inherit: true);

    /// <summary>
    /// Anexa só a raiz como <c>Modified</c>, com os tipos <i>owned</i> dela (referência: <c>Modified</c>; item de coleção:
    /// <c>Modified</c> se tiver chave, senão <c>Added</c>). Outras entidades alcançáveis não são rastreadas nem percorridas.
    /// </summary>
    private EntityEntry<TEntity> AttachRootAsModified(TEntity entity)
    {
        Context.ChangeTracker.TrackGraph(entity, node =>
        {
            if (ReferenceEquals(node.Entry.Entity, entity))
                node.Entry.State = EntityState.Modified;
            else if (node.InboundNavigation is INavigation { ForeignKey.IsOwnership: true, IsOnDependent: false } ownership)
                node.Entry.State = !ownership.IsCollection || HasOwnKey(node.Entry, ownership.ForeignKey) ? EntityState.Modified : EntityState.Added;
            // Demais navegações: sem estado definido, o EF não rastreia nem continua por elas
        });
        return Context.Entry(entity);
    }

    /// <summary>
    /// Item de coleção <i>owned</i> com chave própria preenchida (a parte da chave que não vem do dono, que ainda não foi
    /// propagada quando o item é visitado): já existe no banco.
    /// </summary>
    private static bool HasOwnKey(EntityEntry entry, IForeignKey ownership) =>
        entry.Metadata.FindPrimaryKey()!.Properties
            .Where(p => !ownership.Properties.Contains(p))
            .All(p => entry.Property(p.Name).CurrentValue is { } value && !Equals(value, p.Sentinel));

    /// <summary>Desanexa a entrada e, antes, os tipos <i>owned</i> dela (recursivamente).</summary>
    private void DetachWithOwned(EntityEntry entry)
    {
        foreach (var owned in OwnedEntries(entry).ToList())
            DetachWithOwned(owned);
        entry.State = EntityState.Detached;
    }

    private IEnumerable<EntityEntry> OwnedEntries(EntityEntry entry)
    {
        foreach (var navigation in entry.Navigations)
        {
            if (navigation.Metadata is not INavigation { ForeignKey.IsOwnership: true, IsOnDependent: false })
                continue;
            if (navigation is ReferenceEntry { TargetEntry: { State: not EntityState.Detached } target })
                yield return target;
            else if (navigation is CollectionEntry { CurrentValue: { } items })
            {
                foreach (object item in items)
                {
                    var itemEntry = Context.Entry(item);
                    if (itemEntry.State != EntityState.Detached)
                        yield return itemEntry;
                }
            }
        }
    }

    /// <summary>
    /// Mantém os campos de exclusão lógica fora do UPDATE. A existência já foi verificada com o filtro global, então o
    /// registro não está excluído: o objeto devolvido reflete isso mesmo que o chamador tenha preenchido os campos.
    /// </summary>
    private static void ProtectSoftDeleteColumns(EntityEntry<TEntity> entry)
    {
        var isDeleted = entry.Property(nameof(ISoftDelete.IsDeleted));
        isDeleted.OriginalValue = false;
        isDeleted.CurrentValue = false;
        isDeleted.IsModified = false;

        var deletedAt = entry.Property(nameof(ISoftDelete.DeletedAt));
        deletedAt.OriginalValue = null;
        deletedAt.CurrentValue = null;
        deletedAt.IsModified = false;
    }

    /// <summary>
    /// Grava; se falhar, desanexa a entidade (com os <i>owned</i>) para não ser regravada pela próxima operação do escopo.
    /// <paramref name="discardPropagation"/>: desanexa também as exclusões lógicas propagadas pelo interceptor e não gravadas.
    /// </summary>
    private async Task SaveAsync(EntityEntry<TEntity> entry, CancellationToken cancellationToken, bool discardPropagation = false)
    {
        try
        {
            await Context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (discardPropagation)
            {
                var pending = Context.ChangeTracker.Entries<ISoftDelete>()
                    .Where(e => e.State == EntityState.Modified && e.Property(nameof(ISoftDelete.IsDeleted)) is { CurrentValue: true, OriginalValue: false })
                    .ToList();
                foreach (var propagated in pending)
                    DetachWithOwned(propagated);
            }
            if (entry.State != EntityState.Detached)
                DetachWithOwned(entry);
            throw;
        }
    }

    /// <summary>
    /// Leitura: repetida em falha transitória só fora de transação (dentro dela, uma falha pode ter desfeito a transação no
    /// servidor, e repetir leria fora dela).
    /// </summary>
    private OrmOperation Read(string operation, object? identifier = null) =>
        new(OrmDiagnostics.EntityFrameworkProvider, operation, Target, isWrite: false) { Identifier = identifier, IsRetryable = !InTransaction };

    private bool InTransaction => Context.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null;

    private static OrmOperation Write(string operation, object? identifier = null) =>
        new(OrmDiagnostics.EntityFrameworkProvider, operation, Target, isWrite: true) { Identifier = identifier };

    private static OrmOperation HardDelete(string operation, object? identifier = null) =>
        new(OrmDiagnostics.EntityFrameworkProvider, operation, Target, isWrite: true) { Identifier = identifier, IsHardDelete = true };
}
