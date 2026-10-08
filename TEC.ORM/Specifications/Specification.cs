using System.Linq.Expressions;
using TEC.Core.Common.Guards;
using TEC.ORM.Internal;

namespace TEC.ORM.Specifications;

/// <summary>
/// Implementação fluente de <see cref="ISpecification{T}"/>. Use direto ou herde para dar nome a critérios do domínio.
/// </summary>
/// <example>
/// <code>
/// public sealed class ClientesAtivosPorNome : Specification&lt;Cliente&gt;
/// {
///     public ClientesAtivosPorNome(string prefixo)
///     {
///         Where(c => c.Ativo &amp;&amp; c.Nome.StartsWith(prefixo));
///         OrderByAscending(c => c.Nome);
///     }
/// }
///
/// var ativos = await clientes.FindAsync(new ClientesAtivosPorNome("Ana"));
/// var caros = await produtos.FindAsync(new Specification&lt;Produto&gt;(p => p.Preco > 100).OrderByDescending(p => p.Preco));
/// </code>
/// </example>
/// <typeparam name="T">Tipo da entidade.</typeparam>
public class Specification<T> : ISpecification<T> where T : class
{
    private readonly List<SortExpression> _orderBy = [];
    private readonly List<string> _includes = [];

    /// <summary>Cria a especificação, opcionalmente com um filtro inicial.</summary>
    public Specification(Expression<Func<T, bool>>? criteria = null)
    {
        Criteria = criteria;
    }

    /// <inheritdoc />
    public Expression<Func<T, bool>>? Criteria { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<SortExpression> OrderBy => _orderBy;

    /// <inheritdoc />
    public IReadOnlyList<string> Includes => _includes;

    /// <summary>Acrescenta um filtro (combinado com E ao filtro atual).</summary>
    public Specification<T> Where(Expression<Func<T, bool>> criteria)
    {
        Guard.NotNull(criteria);
        Criteria = Criteria is null ? criteria : And(Criteria, criteria);
        return this;
    }

    /// <summary>Acrescenta uma ordenação crescente.</summary>
    public Specification<T> OrderByAscending<TProperty>(Expression<Func<T, TProperty>> keySelector)
    {
        Guard.NotNull(keySelector);
        _orderBy.Add(new SortExpression(keySelector, Descending: false));
        return this;
    }

    /// <summary>Acrescenta uma ordenação decrescente.</summary>
    public Specification<T> OrderByDescending<TProperty>(Expression<Func<T, TProperty>> keySelector)
    {
        Guard.NotNull(keySelector);
        _orderBy.Add(new SortExpression(keySelector, Descending: true));
        return this;
    }

    /// <summary>Carrega uma navegação junto (ex.: <c>nameof(Pedido.Cliente)</c>).</summary>
    public Specification<T> Include(string navigationPath)
    {
        _includes.Add(Guard.NotNullOrWhiteSpace(navigationPath));
        return this;
    }

    /// <summary>Combina dois filtros com E, reaproveitando o parâmetro do primeiro.</summary>
    internal static Expression<Func<T, bool>> And(Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rightBody = ParameterReplacer.Replace(right.Body, right.Parameters[0], parameter);
        return Expression.Lambda<Func<T, bool>>(Expression.AndAlso(left.Body, rightBody), parameter);
    }
}
