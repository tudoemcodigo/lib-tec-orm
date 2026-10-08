namespace TEC.ORM.Paging;

/// <summary>
/// Marca uma propriedade da entidade como permitida em <see cref="PageRequest.SortBy"/> (ordenação por texto, que costuma vir
/// da requisição).
/// </summary>
/// <remarks>
/// <para>Regra da implementação:</para>
/// <list type="bullet">
/// <item><description>se a entidade tiver <b>ao menos uma</b> propriedade com <c>[Sortable]</c>, só essas são aceitas
/// (lista branca explícita);</description></item>
/// <item><description>se não tiver nenhuma, qualquer propriedade escalar mapeada é aceita, menos as sempre bloqueadas.</description></item>
/// </list>
/// <para>Sempre bloqueadas, com ou sem o atributo: propriedades sombra (<i>shadow</i>), colunas da exclusão lógica
/// (<c>IsDeleted</c>, <c>DeletedAt</c>) e tokens de concorrência (<c>rowversion</c>, <c>[ConcurrencyCheck]</c>).</para>
/// <para>Recomendado sempre que o <c>SortBy</c> vier do usuário: ordenar por uma coluna sensível (ex.: <c>SenhaHash</c>) permite
/// inferir o valor dela pela posição dos registros.</para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class Usuario : Entity&lt;Guid&gt;
/// {
///     [Sortable] public string Nome { get; set; } = "";
///     [Sortable] public DateTimeOffset CriadoEm { get; set; }
///     public string SenhaHash { get; set; } = "";   // fora da ordenação
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SortableAttribute : Attribute;
