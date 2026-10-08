namespace TEC.ORM.Mapping;

/// <summary>
/// Origem do mapeamento. Na classe: a entidade de origem do DTO (o DTO deve ser <c>partial</c>; o gerador do TEC.ORM cria a
/// conversão em tempo de compilação). Na propriedade: o caminho na entidade, com navegação por ponto
/// (ex.: <c>"Cliente.Endereco.Cidade"</c>), seguro contra nulos no caminho.
/// </summary>
/// <example>
/// <code>
/// [MapFrom(typeof(Pedido))]
/// public partial class PedidoDto
/// {
///     public long Id { get; set; }                       // mesmo nome: automático
///     [MapFrom("Cliente.Nome")]
///     public string? ClienteNome { get; set; }           // achatamento (só entidade → DTO)
///     public ClienteDto? Cliente { get; set; }           // DTO aninhado: automático ([MapObject] para opções)
///     public List&lt;ItemDto&gt; Itens { get; set; } = []; // coleção de DTO: automático ([MapList] para opções)
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class MapFromAttribute : Attribute
{
    /// <summary>Entidade de origem do DTO (uso na classe).</summary>
    public MapFromAttribute(Type entityType)
    {
        EntityType = entityType;
    }

    /// <summary>Caminho da propriedade na entidade (uso na propriedade).</summary>
    public MapFromAttribute(string sourcePath)
    {
        SourcePath = sourcePath;
    }

    /// <summary>Entidade de origem.</summary>
    public Type? EntityType { get; }

    /// <summary>Caminho na entidade.</summary>
    public string? SourcePath { get; }
}

/// <summary>
/// Objeto complexo → DTO aninhado (o tipo da propriedade também tem <see cref="MapFromAttribute"/>). Opcional quando o nome e
/// o tipo casam; use para indicar outro caminho, limitar a profundidade em ciclos ou gravar de volta.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class MapObjectAttribute : Attribute
{
    /// <summary>Mesmo nome na entidade.</summary>
    public MapObjectAttribute()
    {
    }

    /// <summary>Caminho na entidade.</summary>
    public MapObjectAttribute(string sourcePath)
    {
        SourcePath = sourcePath;
    }

    /// <summary>Caminho na entidade (<c>null</c> = mesmo nome).</summary>
    public string? SourcePath { get; }

    /// <summary>
    /// Ciclos entre DTOs (ex.: Cliente → Pedidos → Cliente): quantas vezes esta propriedade é expandida num mesmo caminho;
    /// além disso fica <c>null</c>. Todo ciclo precisa de ao menos uma propriedade com <see cref="MaxDepth"/>, senão é erro de
    /// compilação (TECORM005).
    /// </summary>
    public int MaxDepth { get; set; }

    /// <summary>
    /// Grava de volta na entidade (<c>ToEntity</c>/<c>ApplyTo</c>): cria o objeto ou aplica sobre o existente. Padrão
    /// <c>false</c>: o objeto aninhado só vai da entidade para o DTO (evita alterar entidades relacionadas pelo DTO).
    /// </summary>
    public bool Sync { get; set; }
}

/// <summary>
/// Coleção de entidades → coleção de DTOs (<c>List</c>, array, <c>IReadOnlyList</c>, <c>IEnumerable</c>...). Opcional quando
/// o nome e o tipo casam; use para indicar outro caminho, limitar a profundidade em ciclos ou sincronizar de volta.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class MapListAttribute : Attribute
{
    /// <summary>Mesmo nome na entidade.</summary>
    public MapListAttribute()
    {
    }

    /// <summary>Caminho na entidade.</summary>
    public MapListAttribute(string sourcePath)
    {
        SourcePath = sourcePath;
    }

    /// <summary>Caminho na entidade (<c>null</c> = mesmo nome).</summary>
    public string? SourcePath { get; }

    /// <inheritdoc cref="MapObjectAttribute.MaxDepth" />
    public int MaxDepth { get; set; }

    /// <summary>
    /// Sincroniza a coleção da entidade no <c>ApplyTo</c>, pelo <c>Id</c>: item com Id existente é atualizado, item sem Id
    /// (ou com Id desconhecido) é criado, item ausente é removido (vira exclusão lógica ao salvar, em relação obrigatória).
    /// No <c>ToEntity</c>, os itens são criados. Padrão <c>false</c>: a coleção só vai da entidade para o DTO.
    /// </summary>
    public bool Sync { get; set; }
}

/// <summary>Direção de um mapeamento.</summary>
public enum MapDirection
{
    /// <summary>Entidade → DTO e DTO → entidade.</summary>
    Both = 0,

    /// <summary>Só entidade → DTO.</summary>
    ToDto = 1,

    /// <summary>Só DTO → entidade.</summary>
    ToEntity = 2
}

/// <summary>Ignora a propriedade no mapeamento (nas duas direções, ou só na indicada).</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class MapIgnoreAttribute(MapDirection direction = MapDirection.Both) : Attribute
{
    /// <summary>Direção ignorada.</summary>
    public MapDirection Direction { get; } = direction;
}

/// <summary>A propriedade só vai da entidade para o DTO (ex.: <c>CriadoEm</c>, campos calculados).</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class MapReadOnlyAttribute : Attribute;

/// <summary>
/// Conversão customizada: <paramref name="converterType"/> implementa <see cref="IMapConverter{TSource, TDestination}"/>
/// (entidade → DTO) e, para gravar de volta, <see cref="IBidirectionalMapConverter{TSource, TDestination}"/>. Precisa de
/// construtor público sem parâmetros e não pode guardar estado (uma instância por propriedade).
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class MapConverterAttribute(Type converterType) : Attribute
{
    /// <summary>Tipo do conversor.</summary>
    public Type ConverterType { get; } = converterType;
}
