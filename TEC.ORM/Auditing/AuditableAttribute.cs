namespace TEC.ORM.Auditing;

/// <summary>
/// Indica que a entidade deve ter sua trilha histórica de auditoria gravada a cada criação, alteração ou exclusão
/// (física ou lógica), registrando o diff detalhado (colunas alteradas, valores anteriores e novos) e o autor da operação.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class AuditableAttribute : Attribute
{
}

/// <summary>
/// Indica que a propriedade da entidade deve ser ignorada na trilha de auditoria (não gera diff em <c>OldValues</c> ou <c>NewValues</c>).
/// Útil para contadores de acesso, tokens de concorrência ou campos de alta frequência não críticos.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class AuditIgnoreAttribute : Attribute
{
}
