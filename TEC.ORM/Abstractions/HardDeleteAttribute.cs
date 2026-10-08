namespace TEC.ORM.Abstractions;

/// <summary>
/// Marca um método que faz <b>exclusão física</b>: a linha é removida de fato do banco (<c>DELETE</c>), sem passar pela
/// exclusão lógica, e não há como desfazer.
/// </summary>
/// <remarks>
/// <para>Alerta na compilação: toda chamada a um método marcado (ou que sobrescreve/implementa um método marcado) gera o aviso
/// <c>TECORM014</c>. Depois de confirmar que a exclusão física é intencional, suprima o aviso no local da chamada
/// (<c>#pragma warning disable TECORM014</c> ou <c>[SuppressMessage("TEC.ORM.Usage", "TECORM014")]</c>).</para>
/// <para>Chamadas feitas de dentro de um método também marcado não geram o aviso: o alerta passa para quem chama esse método.
/// Use o atributo nos métodos próprios do repositório que encapsulam a exclusão física.</para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class HardDeleteAttribute : Attribute;
