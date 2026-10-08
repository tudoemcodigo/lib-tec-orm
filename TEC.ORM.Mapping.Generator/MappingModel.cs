using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace TEC.ORM.Mapping.Generator;

internal enum PropertyKind
{
    Scalar,
    Object,
    List
}

/// <summary>Conversão entidade → DTO de um valor escalar.</summary>
internal enum ForwardConversion
{
    Identity,
    ImplicitCast,
    NullableToValue,
    EnumToString,
    StringToEnum,
    Converter
}

/// <summary>Conversão DTO → entidade de um valor escalar.</summary>
internal enum ReverseConversion
{
    None,
    Identity,
    ImplicitCast,
    NullableKeep,
    StringToEnum,
    EnumToString,
    Converter
}

internal enum CollectionShape
{
    List,
    Array
}

/// <summary>Uma propriedade do DTO e a sua origem na entidade.</summary>
internal sealed class PropertyMap
{
    public PropertyMap(IPropertySymbol dtoProperty, Location location)
    {
        DtoProperty = dtoProperty;
        Location = location;
    }

    public IPropertySymbol DtoProperty { get; }

    public Location Location { get; }

    public string Name => DtoProperty.Name;

    public ITypeSymbol DtoType => DtoProperty.Type;

    public PropertyKind Kind { get; set; }

    /// <summary>Propriedades do caminho na entidade (a última é a origem do valor).</summary>
    public List<IPropertySymbol> Path { get; } = new();

    public ITypeSymbol SourceType => Path[Path.Count - 1].Type;

    /// <summary>Preenchida a partir da entidade (entidade → DTO).</summary>
    public bool Forward { get; set; } = true;

    public ForwardConversion Conversion { get; set; }

    /// <summary>Tipo subjacente de um <c>Nullable&lt;T&gt;</c> de origem (conversão <see cref="ForwardConversion.NullableToValue"/>).</summary>
    public ITypeSymbol? NullableUnderlying { get; set; }

    public INamedTypeSymbol? Converter { get; set; }

    public bool ConverterIsBidirectional { get; set; }

    /// <summary>DTO aninhado (objeto) ou DTO do elemento (coleção); <c>null</c> em coleção de valores simples.</summary>
    public INamedTypeSymbol? NestedDto { get; set; }

    public CollectionShape Shape { get; set; }

    public ITypeSymbol? DtoElementType { get; set; }

    public ITypeSymbol? SourceElementType { get; set; }

    /// <summary>Elemento de coleção simples precisa de cast.</summary>
    public bool ElementCast { get; set; }

    public int MaxDepth { get; set; }

    public bool Sync { get; set; }

    // ---------- DTO → entidade ----------

    public ReverseConversion Reverse { get; set; }

    /// <summary>Tipo da chave do elemento da coleção sincronizada.</summary>
    public ITypeSymbol? SyncKeyType { get; set; }

    /// <summary>A coleção/objeto da entidade pode ser atribuído (criar quando nulo).</summary>
    public bool EntityPropertySettable { get; set; }

    /// <summary>Converter em <c>Converter</c> fica num campo estático do DTO que declara a propriedade.</summary>
    public string ConverterFieldName => "__TecOrmConverter_" + Name;
}

/// <summary>Resultado da análise de um DTO.</summary>
internal sealed class DtoModel
{
    public DtoModel(INamedTypeSymbol dto)
    {
        Dto = dto;
    }

    public INamedTypeSymbol Dto { get; }

    public INamedTypeSymbol? Entity { get; set; }

    public List<PropertyMap> Properties { get; } = new();

    public List<DiagnosticInfo> Diagnostics { get; } = new();

    public bool IsValid => Diagnostics.Count == 0 && Entity is not null;

    /// <summary>A entidade tem construtor sem parâmetros acessível: gera <c>ToEntity()</c>.</summary>
    public bool CanCreateEntity { get; set; }

    public Location Location { get; set; } = Location.None;
}
