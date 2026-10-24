namespace TEC.ORM.Auditing;

/// <summary>
/// Marca propriedades que contêm dados sensíveis ou confidenciais (senhas, segredos, documentos LGPD),
/// instruindo o interceptor de auditoria a mascarar o valor com '***' nos logs históricos.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SensitiveDataAttribute : Attribute
{
}
