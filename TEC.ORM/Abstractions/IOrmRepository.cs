using System.Linq.Expressions;
using TEC.Core.Common.Results;
using TEC.Core.Responses.Pagination;
using TEC.ORM.Entities;
using TEC.ORM.Mapping;
using TEC.ORM.Paging;
using TEC.ORM.Specifications;

namespace TEC.ORM.Abstractions;

/// <summary>
/// Operações padronizadas de uma entidade (CRUD simples). Na implementação SQL Server: EF Core.
/// </summary>
/// <remarks>
/// <para>Toda operação devolve <see cref="Result"/>: erros esperados (não encontrado, entrada inválida, conflito, concorrência,
/// banco indisponível) são falhas com os códigos de <see cref="Common.OrmErrors"/>, e não exceções. Só o cancelamento
/// (<see cref="OperationCanceledException"/>) e argumentos de programação (ex.: tipo de chave sem igualdade) lançam.</para>
/// <para>Exclusão lógica: <see cref="DeleteAsync"/> marca o registro como excluído; nenhuma operação enxerga registros
/// excluídos (ler, atualizar, excluir de novo, buscar, listar, existência e contagem). A exclusão física (remover a linha de
/// fato) existe só pelos métodos <see cref="HardDeleteAsync(TKey, CancellationToken)"/>, que geram alerta na compilação
/// (<c>TECORM014</c>) e no log.</para>
/// <para>Escritas são gravadas na hora. Para agrupar várias numa transação, use o <c>IUnitOfWork</c> (TEC.Cqrs) registrado
/// pela implementação.</para>
/// </remarks>
/// <typeparam name="TEntity">Entidade (com exclusão lógica).</typeparam>
/// <typeparam name="TKey">Tipo da chave primária.</typeparam>
public interface IOrmRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>, ISoftDelete
    where TKey : notnull, IEquatable<TKey>
{
    /// <summary>Cria o registro e devolve a entidade com o identificador gerado.</summary>
    Task<Result<TEntity>> CreateAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>Lê pelo identificador (sem rastreamento de alterações).</summary>
    Task<Result<TEntity>> GetByIdAsync(TKey id, CancellationToken cancellationToken = default);

    /// <summary>Atualiza o registro existente (os campos de exclusão lógica não são alterados por aqui).</summary>
    Task<Result<TEntity>> UpdateAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>Exclui logicamente pelo identificador.</summary>
    Task<Result> DeleteAsync(TKey id, CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>Exclusão física</b>: remove a linha de fato do banco (<c>DELETE</c>), inclusive se ela já estiver excluída
    /// logicamente. <b>Irreversível.</b>
    /// </summary>
    /// <remarks>
    /// <para>Alerta: toda chamada gera o aviso de compilação <c>TECORM014</c> (ver <see cref="HardDeleteAttribute"/>) e, no
    /// sucesso, um log de auditoria em <c>Warning</c>. Para o fluxo normal, use <see cref="DeleteAsync"/>.</para>
    /// <para>Dependentes seguem as chaves estrangeiras do banco: <c>ON DELETE CASCADE</c> os remove junto; com
    /// <c>NO ACTION</c>/<c>RESTRICT</c>, existir dependente resulta em <c>ORM_CONFLITO</c> e nada é excluído.</para>
    /// </remarks>
    /// <returns>Sucesso, <c>ORM_NAO_ENCONTRADO</c> se a linha não existir, ou <c>ORM_CONFLITO</c> se houver dependentes.</returns>
    [HardDelete]
    Task<Result> HardDeleteAsync(TKey id, CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>Exclusão física em lote</b>: remove de fato do banco (<c>DELETE</c>) todas as linhas que atendem ao critério, inclusive
    /// as já excluídas logicamente (ex.: expurgo de <c>e =&gt; e.IsDeleted &amp;&amp; e.DeletedAt &lt; limite</c>).
    /// <b>Irreversível.</b>
    /// </summary>
    /// <remarks>
    /// <para>Alerta: toda chamada gera o aviso de compilação <c>TECORM014</c> e, no sucesso, um log de auditoria em <c>Warning</c>.</para>
    /// <para>O critério é obrigatório (não há "excluir tudo"); navegações e ordenação da especificação são ignoradas. Os demais
    /// filtros globais definidos pela aplicação continuam valendo. Dependentes seguem as chaves estrangeiras do banco,
    /// como em <see cref="HardDeleteAsync(TKey, CancellationToken)"/>; com conflito, nenhuma linha é excluída.</para>
    /// </remarks>
    /// <returns>Quantidade de linhas excluídas (zero não é falha).</returns>
    [HardDelete]
    Task<Result<long>> HardDeleteAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default);

    /// <summary>Busca por critérios. Falha com <c>ORM_LIMITE_EXCEDIDO</c> se passar do limite configurado: use <see cref="ListAsync"/>.</summary>
    Task<Result<IReadOnlyList<TEntity>>> FindAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default);

    /// <summary>Lista com paginação, filtro e ordenação (a ordenação da página vem antes da ordenação da especificação).</summary>
    Task<Result<PagedResult<TEntity>>> ListAsync(PageRequest page, ISpecification<TEntity>? specification = null,
        CancellationToken cancellationToken = default);

    /// <summary>Verifica se existe registro com o identificador.</summary>
    Task<Result<bool>> ExistsAsync(TKey id, CancellationToken cancellationToken = default);

    /// <summary>Verifica se existe registro que atende ao critério.</summary>
    Task<Result<bool>> ExistsAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default);

    /// <summary>Conta os registros (todos, ou os que atendem ao critério).</summary>
    Task<Result<long>> CountAsync(ISpecification<TEntity>? specification = null, CancellationToken cancellationToken = default);

    // ---------- Projeção para DTO (mapeamento gerado por [MapFrom]) ----------

    /// <summary>
    /// Lê pelo identificador já como DTO: o banco devolve só as colunas que o DTO usa, com os objetos e coleções aninhados,
    /// sem <c>Include</c>.
    /// </summary>
    Task<Result<TDto>> GetByIdAsync<TDto>(TKey id, CancellationToken cancellationToken = default)
        where TDto : class, IDtoMap<TEntity, TDto>;

    /// <summary>Busca por critérios já como DTO (as navegações da especificação são ignoradas: a projeção já as traz).</summary>
    Task<Result<IReadOnlyList<TDto>>> FindAsync<TDto>(ISpecification<TEntity> specification, CancellationToken cancellationToken = default)
        where TDto : class, IDtoMap<TEntity, TDto>;

    /// <summary>
    /// Lista com paginação, filtro e ordenação já como DTO. <see cref="PageRequest.SortBy"/> e os critérios usam as
    /// propriedades da <b>entidade</b>.
    /// </summary>
    Task<Result<PagedResult<TDto>>> ListAsync<TDto>(PageRequest page, ISpecification<TEntity>? specification = null,
        CancellationToken cancellationToken = default)
        where TDto : class, IDtoMap<TEntity, TDto>;
}

/// <summary>Atalhos de <see cref="IOrmRepository{TEntity, TKey}"/> com expressão em vez de especificação.</summary>
public static class OrmRepositoryExtensions
{
    /// <summary>Busca pelo filtro.</summary>
    public static Task<Result<IReadOnlyList<TEntity>>> FindAsync<TEntity, TKey>(this IOrmRepository<TEntity, TKey> orm,
        Expression<Func<TEntity, bool>> criteria, CancellationToken cancellationToken = default)
        where TEntity : class, IEntity<TKey>, ISoftDelete
        where TKey : notnull, IEquatable<TKey>
    {
        ArgumentNullException.ThrowIfNull(orm);
        return orm.FindAsync(new Specification<TEntity>(criteria), cancellationToken);
    }

    /// <summary>Verifica se existe registro que atende ao filtro.</summary>
    public static Task<Result<bool>> ExistsAsync<TEntity, TKey>(this IOrmRepository<TEntity, TKey> orm,
        Expression<Func<TEntity, bool>> criteria, CancellationToken cancellationToken = default)
        where TEntity : class, IEntity<TKey>, ISoftDelete
        where TKey : notnull, IEquatable<TKey>
    {
        ArgumentNullException.ThrowIfNull(orm);
        return orm.ExistsAsync(new Specification<TEntity>(criteria), cancellationToken);
    }

    /// <summary>
    /// <b>Exclusão física em lote</b> pelo filtro: remove as linhas de fato do banco, inclusive as já excluídas logicamente.
    /// <b>Irreversível</b> (gera o aviso <c>TECORM014</c>).
    /// </summary>
    [HardDelete]
    public static Task<Result<long>> HardDeleteAsync<TEntity, TKey>(this IOrmRepository<TEntity, TKey> orm,
        Expression<Func<TEntity, bool>> criteria, CancellationToken cancellationToken = default)
        where TEntity : class, IEntity<TKey>, ISoftDelete
        where TKey : notnull, IEquatable<TKey>
    {
        ArgumentNullException.ThrowIfNull(orm);
        return orm.HardDeleteAsync(new Specification<TEntity>(criteria), cancellationToken);
    }

    /// <summary>Conta os registros que atendem ao filtro.</summary>
    public static Task<Result<long>> CountAsync<TEntity, TKey>(this IOrmRepository<TEntity, TKey> orm,
        Expression<Func<TEntity, bool>> criteria, CancellationToken cancellationToken = default)
        where TEntity : class, IEntity<TKey>, ISoftDelete
        where TKey : notnull, IEquatable<TKey>
    {
        ArgumentNullException.ThrowIfNull(orm);
        return orm.CountAsync(new Specification<TEntity>(criteria), cancellationToken);
    }
}
