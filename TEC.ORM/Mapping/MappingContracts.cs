using System.Linq.Expressions;
using TEC.Core.Common.Guards;
using TEC.Core.Responses.Pagination;

namespace TEC.ORM.Mapping;

/// <summary>Conversão de um valor da entidade (<typeparamref name="TSource"/>) para o DTO (<typeparamref name="TDestination"/>).</summary>
/// <remarks>Na projeção para o banco, a conversão roda na aplicação depois da leitura (avaliação no cliente).</remarks>
public interface IMapConverter<in TSource, out TDestination>
{
    /// <summary>Converte o valor da entidade.</summary>
    TDestination Convert(TSource value);
}

/// <summary>Conversão nas duas direções: necessária para a propriedade ser gravada de volta na entidade.</summary>
public interface IBidirectionalMapConverter<TSource, TDestination> : IMapConverter<TSource, TDestination>
{
    /// <summary>Converte o valor do DTO de volta para a entidade.</summary>
    TSource ConvertBack(TDestination value);
}

/// <summary>
/// Mapeamento entidade → DTO gerado em tempo de compilação para todo DTO <c>partial</c> com <see cref="MapFromAttribute"/>.
/// Não implemente à mão.
/// </summary>
/// <typeparam name="TEntity">Entidade de origem.</typeparam>
/// <typeparam name="TDto">O próprio DTO.</typeparam>
public interface IDtoMap<TEntity, TDto>
    where TEntity : class
    where TDto : class, IDtoMap<TEntity, TDto>
{
    /// <summary>
    /// Projeção para consultas (<c>IQueryable.Select</c>): o ORM traduz para um <c>SELECT</c> só das colunas usadas pelo DTO,
    /// com os objetos e coleções aninhados, sem precisar de <c>Include</c>.
    /// </summary>
    static abstract Expression<Func<TEntity, TDto>> Projection { get; }

    /// <summary>Converte uma entidade já carregada em memória.</summary>
    static abstract TDto FromEntity(TEntity entity);
}

/// <summary>Mapeamento DTO → entidade gerado em tempo de compilação (quando a entidade tem construtor sem parâmetros).</summary>
/// <remarks>
/// Nunca copia o identificador (<c>Id</c>) nem os campos de exclusão lógica, nem as propriedades achatadas, somente leitura,
/// ignoradas ou sem conversão de volta. Objetos e coleções aninhados só com <c>Sync = true</c>.
/// </remarks>
/// <typeparam name="TEntity">Entidade de destino.</typeparam>
public interface IDtoReverseMap<in TEntity> where TEntity : class
{
    /// <summary>Copia os valores do DTO sobre uma entidade existente (atualização), preservando o que o DTO não tem.</summary>
    void ApplyTo(TEntity entity);
}

/// <summary>Criação da entidade a partir do DTO.</summary>
/// <typeparam name="TEntity">Entidade de destino.</typeparam>
public interface IDtoEntityFactory<out TEntity> where TEntity : class
{
    /// <summary>Cria uma entidade nova com os valores do DTO (criação).</summary>
    TEntity ToEntity();
}

/// <summary>Atalhos genéricos para os mapeamentos gerados.</summary>
public static class DtoMappingExtensions
{
    /// <summary>Projeta a consulta para o DTO (SELECT só das colunas usadas pelo DTO).</summary>
    public static IQueryable<TDto> ProjectToDto<TEntity, TDto>(this IQueryable<TEntity> query)
        where TEntity : class
        where TDto : class, IDtoMap<TEntity, TDto>
    {
        Guard.NotNull(query);
        return query.Select(TDto.Projection);
    }

    /// <summary>Converte uma coleção de entidades carregadas em memória.</summary>
    public static List<TDto> ToDtoList<TEntity, TDto>(this IEnumerable<TEntity> entities)
        where TEntity : class
        where TDto : class, IDtoMap<TEntity, TDto>
    {
        Guard.NotNull(entities);
        return entities.Select(TDto.FromEntity).ToList();
    }

    /// <summary>Converte uma página de entidades carregadas em memória, preservando a paginação.</summary>
    public static PagedResult<TDto> ToDtoPage<TEntity, TDto>(this PagedResult<TEntity> page)
        where TEntity : class
        where TDto : class, IDtoMap<TEntity, TDto>
    {
        Guard.NotNull(page);
        return page.Map(TDto.FromEntity);
    }
}
