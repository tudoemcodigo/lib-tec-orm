using System.Linq.Expressions;

namespace TEC.ORM.Internal;

/// <summary>
/// Troca um parâmetro de expressão por outro (ex.: combinar dois filtros num único lambda). Única implementação da família
/// TEC.ORM: o TEC.ORM.SqlServer usa esta (InternalsVisibleTo; os pacotes saem sempre na mesma versão).
/// </summary>
internal sealed class ParameterReplacer(ParameterExpression from, Expression to) : ExpressionVisitor
{
    /// <summary>Aplica a troca em <paramref name="expression"/>.</summary>
    public static Expression Replace(Expression expression, ParameterExpression from, Expression to) =>
        new ParameterReplacer(from, to).Visit(expression);

    protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
}
