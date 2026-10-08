using System.Linq.Expressions;

namespace TEC.ORM.Specifications;

/// <summary>
/// Critério de busca reutilizável e testável: filtro, ordenação e navegações a carregar. Mantém a consulta dentro do domínio
/// sem expor <see cref="IQueryable{T}"/> (a camada de aplicação não depende do ORM).
/// </summary>
/// <typeparam name="T">Tipo da entidade.</typeparam>
public interface ISpecification<T> where T : class
{
    /// <summary>Filtro; <c>null</c> = todos (os excluídos logicamente nunca entram).</summary>
    Expression<Func<T, bool>>? Criteria { get; }

    /// <summary>Ordenação, na ordem de aplicação (a primeira é a principal).</summary>
    IReadOnlyList<SortExpression> OrderBy { get; }

    /// <summary>Caminhos de navegação a carregar junto (ex.: <c>"Itens"</c>, <c>"Itens.Produto"</c>); validados pelo modelo do ORM.</summary>
    IReadOnlyList<string> Includes { get; }
}

/// <summary>Um critério de ordenação.</summary>
/// <param name="KeySelector">Expressão da propriedade (ex.: <c>(Cliente c) => c.Nome</c>).</param>
/// <param name="Descending">Ordem decrescente.</param>
public sealed record SortExpression(LambdaExpression KeySelector, bool Descending);
