using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TEC.ORM.Mapping.Generator;

/// <summary>Analisa um DTO com [MapFrom]: resolve a origem de cada propriedade, as conversões e valida as regras.</summary>
internal sealed class DtoAnalyzer
{
    public const string MapFromName = "TEC.ORM.Mapping.MapFromAttribute";
    private const string MapObjectName = "TEC.ORM.Mapping.MapObjectAttribute";
    private const string MapListName = "TEC.ORM.Mapping.MapListAttribute";
    private const string MapIgnoreName = "TEC.ORM.Mapping.MapIgnoreAttribute";
    private const string MapReadOnlyName = "TEC.ORM.Mapping.MapReadOnlyAttribute";
    private const string MapConverterName = "TEC.ORM.Mapping.MapConverterAttribute";

    // MapDirection
    private const int DirectionBoth = 0;
    private const int DirectionToDto = 1;
    private const int DirectionToEntity = 2;

    private readonly Compilation _compilation;
    private readonly INamedTypeSymbol? _converterInterface;
    private readonly INamedTypeSymbol? _bidirectionalInterface;
    private readonly INamedTypeSymbol? _entityInterface;
    private readonly INamedTypeSymbol? _softDeleteInterface;
    private readonly INamedTypeSymbol? _auditableInterface;
    private readonly INamedTypeSymbol? _list;
    private readonly Dictionary<INamedTypeSymbol, DtoModel> _cache = new(SymbolEqualityComparer.Default);

    public DtoAnalyzer(Compilation compilation)
    {
        _compilation = compilation;
        _converterInterface = compilation.GetTypeByMetadataName("TEC.ORM.Mapping.IMapConverter`2");
        _bidirectionalInterface = compilation.GetTypeByMetadataName("TEC.ORM.Mapping.IBidirectionalMapConverter`2");
        _entityInterface = compilation.GetTypeByMetadataName("TEC.ORM.Entities.IEntity`1");
        _softDeleteInterface = compilation.GetTypeByMetadataName("TEC.ORM.Entities.ISoftDelete");
        _auditableInterface = compilation.GetTypeByMetadataName("TEC.ORM.Entities.IAuditable");
        _list = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
    }

    public Compilation Compilation => _compilation;

    /// <summary>DTO mapeado (com [MapFrom(typeof(...))] na classe) declarado nesta compilação.</summary>
    public static bool IsMappedDto(ITypeSymbol? type) =>
        type is INamedTypeSymbol named && named.TypeKind == TypeKind.Class && EntityOf(named) is not null;

    public static INamedTypeSymbol? EntityOf(INamedTypeSymbol dto)
    {
        var attribute = Find(dto, MapFromName);
        return attribute is { ConstructorArguments.Length: 1 } && attribute.ConstructorArguments[0].Value is INamedTypeSymbol entity
            ? entity
            : null;
    }

    public DtoModel Analyze(INamedTypeSymbol dto)
    {
        if (_cache.TryGetValue(dto, out var cached))
            return cached;

        var model = new DtoModel(dto);
        _cache[dto] = model;
        var classAttribute = Find(dto, MapFromName);
        model.Location = classAttribute?.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? dto.Locations.FirstOrDefault() ?? Location.None;

        if (!ValidateDeclaration(model, classAttribute))
            return model;

        foreach (var property in DtoProperties(dto))
            AnalyzeProperty(model, property);
        return model;
    }

    // ---------- Declaração do DTO ----------

    private bool ValidateDeclaration(DtoModel model, AttributeData? classAttribute)
    {
        var dto = model.Dto;
        string dtoName = dto.Name;

        if (classAttribute is null || classAttribute.ConstructorArguments.Length != 1 ||
            classAttribute.ConstructorArguments[0].Value is not INamedTypeSymbol entity)
        {
            Report(model, MappingDiagnostics.InvalidDeclaration, model.Location, dtoName,
                "na classe, use [MapFrom(typeof(Entidade))]; o caminho em texto é só para propriedades");
            return false;
        }

        if (dto.TypeKind != TypeKind.Class)
        {
            // record struct/struct: o gerador emite "partial class/record", que seria outra declaração (inválida)
            Report(model, MappingDiagnostics.UnsupportedDtoShape, model.Location, dtoName, "struct ou record struct (use class ou record)");
            return false;
        }
        if (dto.ContainingType is not null)
        {
            Report(model, MappingDiagnostics.UnsupportedDtoShape, model.Location, dtoName, "declarado dentro de outro tipo");
            return false;
        }
        if (dto.IsGenericType)
        {
            Report(model, MappingDiagnostics.UnsupportedDtoShape, model.Location, dtoName, "genérico");
            return false;
        }
        if (dto.IsAbstract || dto.IsStatic)
        {
            Report(model, MappingDiagnostics.UnsupportedDtoShape, model.Location, dtoName, "abstrato ou estático");
            return false;
        }

        foreach (var reference in dto.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is TypeDeclarationSyntax declaration && !declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                Report(model, MappingDiagnostics.NotPartial, declaration.Identifier.GetLocation(), dtoName);
                return false;
            }
        }

        // Membros com o nome dos gerados: a declaração gerada não compilaria (ou mudaria o sentido de uma sobrecarga)
        foreach (var member in dto.GetMembers())
        {
            if (!member.IsImplicitlyDeclared && IsGeneratedMemberName(member.Name))
            {
                Report(model, MappingDiagnostics.GeneratedMemberConflict, member.Locations.FirstOrDefault() ?? model.Location, member.Name, dtoName);
                return false;
            }
        }

        if (!dto.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal))
        {
            Report(model, MappingDiagnostics.InvalidDeclaration, model.Location, dtoName,
                "o DTO precisa de construtor público (ou internal) sem parâmetros");
            return false;
        }

        // Primeiro o tipo não encontrado/genérico aberto: TypeKind.Error também falharia na checagem de classe (TECORM012)
        if (entity.IsUnboundGenericType || entity.TypeKind == TypeKind.Error)
        {
            Report(model, MappingDiagnostics.UnsupportedEntity, model.Location, entity.Name, dtoName,
                entity.IsUnboundGenericType ? "tipo genérico aberto (informe os argumentos, ex.: typeof(Entidade<int>))" : "tipo não encontrado");
            return false;
        }

        if (entity.TypeKind != TypeKind.Class && entity.TypeKind != TypeKind.Interface)
        {
            Report(model, MappingDiagnostics.InvalidDeclaration, model.Location, dtoName,
                $"a entidade '{entity.Name}' precisa ser uma classe");
            return false;
        }

        model.Entity = entity;
        model.CanCreateEntity = entity.TypeKind == TypeKind.Class && !entity.IsAbstract &&
            entity.InstanceConstructors.Any(c => c.Parameters.Length == 0 && _compilation.IsSymbolAccessibleWithin(c, dto));
        return true;
    }

    /// <summary>Nomes que o gerador declara no DTO.</summary>
    private static bool IsGeneratedMemberName(string name) =>
        name is "Projection" or "FromEntity" or "ApplyTo" or "ToEntity" || name.StartsWith("__TecOrm", System.StringComparison.Ordinal);

    private static IEnumerable<IPropertySymbol> DtoProperties(INamedTypeSymbol dto)
    {
        var seen = new HashSet<string>();
        for (var type = dto; type is not null && type.SpecialType != SpecialType.System_Object; type = type.BaseType)
        {
            foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.IsImplicitlyDeclared ||
                    property.DeclaredAccessibility != Accessibility.Public || property.Name == "EqualityContract")
                    continue;
                if (seen.Add(property.Name))
                    yield return property;
            }
        }
    }

    // ---------- Propriedades ----------

    private void AnalyzeProperty(DtoModel model, IPropertySymbol property)
    {
        var entity = model.Entity!;
        var location = property.Locations.FirstOrDefault() ?? model.Location;

        int ignore = Find(property, MapIgnoreName) is { } ignoreAttribute
            ? ignoreAttribute.ConstructorArguments.Length == 1 && ignoreAttribute.ConstructorArguments[0].Value is int direction ? direction : DirectionBoth
            : -1;
        if (ignore == DirectionBoth)
            return;

        var mapFrom = Find(property, MapFromName);
        var mapObject = Find(property, MapObjectName);
        var mapList = Find(property, MapListName);
        var converterAttribute = Find(property, MapConverterName);
        bool readOnly = Find(property, MapReadOnlyName) is not null || ignore == DirectionToEntity;
        bool forward = ignore != DirectionToDto;

        string? explicitPath = StringArgument(mapFrom) ?? StringArgument(mapObject) ?? StringArgument(mapList);
        string path = explicitPath ?? property.Name;
        var map = new PropertyMap(property, location)
        {
            Forward = forward,
            MaxDepth = NamedInt(mapObject, "MaxDepth") ?? NamedInt(mapList, "MaxDepth") ?? 0,
            Sync = (NamedBool(mapObject, "Sync") ?? NamedBool(mapList, "Sync")) == true
        };

        if (mapObject is not null && mapList is not null)
        {
            Report(model, MappingDiagnostics.InvalidDeclaration, location, model.Dto.Name,
                $"'{property.Name}' não pode ter [MapObject] e [MapList] ao mesmo tempo");
            return;
        }
        if (map.MaxDepth < 0)
        {
            Report(model, MappingDiagnostics.InvalidDeclaration, location, model.Dto.Name, $"MaxDepth de '{property.Name}' não pode ser negativo");
            return;
        }

        // Caminho na entidade
        if (!ResolvePath(model, map, entity, path, explicitPath is not null, location))
            return;

        // Só DTO → entidade e sem origem: nada a fazer
        if (map.Path.Count == 0)
            return;

        if (forward && property.SetMethod is null)
        {
            Report(model, MappingDiagnostics.NotWritable, location, property.Name, model.Dto.Name);
            return;
        }

        bool ok = mapObject is not null || (mapList is null && converterAttribute is null && IsMappedDto(Unannotated(property.Type)))
            ? AnalyzeObject(model, map, mapObject is not null, location)
            : mapList is not null || (converterAttribute is null && IsCollection(property.Type, out _, out _) && SourceElement(map.SourceType) is not null)
                ? AnalyzeList(model, map, location)
                : AnalyzeScalar(model, map, converterAttribute, location);
        if (!ok)
            return;

        if (!readOnly)
            AnalyzeReverse(model, map, location);
        if (!map.Forward && map.Reverse == ReverseConversion.None && !(map.Sync && map.Kind != PropertyKind.Scalar))
            return;

        model.Properties.Add(map);
    }

    /// <summary>Resolve o caminho; <c>false</c> em erro. Caminho vazio em <see cref="PropertyMap.Path"/> = sem origem (permitido).</summary>
    private bool ResolvePath(DtoModel model, PropertyMap map, INamedTypeSymbol entity, string path, bool isExplicit, Location location)
    {
        string[] segments = path.Split('.');
        ITypeSymbol current = entity;
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i].Trim();
            if (segment.Length == 0)
            {
                Report(model, MappingDiagnostics.InvalidPath, location, path, map.Name, "segmento vazio");
                return false;
            }

            var source = FindProperty(current, segment, model.Dto);
            if (source is null)
            {
                if (!isExplicit && !map.Forward)
                    return true;   // [MapIgnore(ToDto)] sem origem: não participa
                if (isExplicit)
                    Report(model, MappingDiagnostics.InvalidPath, location, path, map.Name,
                        $"'{segment}' não existe (ou não é legível) em '{current.Name}'");
                else
                    Report(model, MappingDiagnostics.NoSource, location, map.Name, model.Dto.Name, entity.Name);
                return false;
            }

            if (i < segments.Length - 1 && IsNullableValue(source.Type))
            {
                Report(model, MappingDiagnostics.InvalidPath, location, path, map.Name,
                    $"'{segment}' é Nullable<T> no meio do caminho; use um conversor");
                return false;
            }

            map.Path.Add(source);
            current = source.Type;
        }
        return true;
    }

    private bool AnalyzeObject(DtoModel model, PropertyMap map, bool explicitAttribute, Location location)
    {
        map.Kind = PropertyKind.Object;
        if (Unannotated(map.DtoType) is not INamedTypeSymbol nested || !IsMappedDto(nested))
        {
            Report(model, MappingDiagnostics.NotMappedDto, location, map.Name, map.DtoType.Name,
                "não tem [MapFrom(typeof(Entidade))]");
            return false;
        }
        if (!InThisCompilation(nested))
        {
            Report(model, MappingDiagnostics.NotMappedDto, location, map.Name, nested.Name,
                "é declarado em outro assembly; DTOs aninhados precisam estar no mesmo projeto");
            return false;
        }

        var nestedEntity = EntityOf(nested)!;
        if (!IsAssignable(map.SourceType, nestedEntity))
        {
            Report(model, MappingDiagnostics.IncompatibleTypes, location, map.Name, nested.Name, string.Join(".", map.Path.Select(p => p.Name)),
                map.SourceType.Name + (explicitAttribute ? "" : $" (o DTO aninhado mapeia '{nestedEntity.Name}')"));
            return false;
        }
        map.NestedDto = nested;
        return true;
    }

    private bool AnalyzeList(DtoModel model, PropertyMap map, Location location)
    {
        map.Kind = PropertyKind.List;
        if (!IsCollection(map.DtoType, out var shape, out var dtoElement))
        {
            Report(model, MappingDiagnostics.UnsupportedCollection, location, map.Name, map.DtoType.ToDisplayString(),
                "não é uma coleção suportada no DTO: use List<T>, T[], IList<T>, ICollection<T>, IEnumerable<T>, IReadOnlyList<T> ou IReadOnlyCollection<T>");
            return false;
        }

        var sourceElement = SourceElement(map.SourceType);
        if (sourceElement is null)
        {
            Report(model, MappingDiagnostics.UnsupportedCollection, location, map.Name, map.SourceType.ToDisplayString(),
                "não é uma coleção na entidade (IEnumerable<T>)");
            return false;
        }

        map.Shape = shape;
        map.DtoElementType = dtoElement;
        map.SourceElementType = sourceElement;

        if (Unannotated(dtoElement) is INamedTypeSymbol nested && IsMappedDto(nested))
        {
            if (!InThisCompilation(nested))
            {
                Report(model, MappingDiagnostics.NotMappedDto, location, map.Name, nested.Name,
                    "é declarado em outro assembly; DTOs aninhados precisam estar no mesmo projeto");
                return false;
            }
            var nestedEntity = EntityOf(nested)!;
            if (!IsAssignable(sourceElement, nestedEntity))
            {
                Report(model, MappingDiagnostics.IncompatibleTypes, location, map.Name, nested.Name,
                    string.Join(".", map.Path.Select(p => p.Name)), $"{sourceElement.Name} (o DTO do elemento mapeia '{nestedEntity.Name}')");
                return false;
            }
            map.NestedDto = nested;
            return true;
        }

        // Coleção de valores simples
        if (SymbolEqualityComparer.Default.Equals(sourceElement, dtoElement))
            return true;
        if (_compilation.ClassifyConversion(sourceElement, dtoElement) is { IsImplicit: true, IsUserDefined: false })
        {
            map.ElementCast = true;
            return true;
        }
        Report(model, MappingDiagnostics.NotMappedDto, location, map.Name, dtoElement.ToDisplayString(),
            $"não tem [MapFrom] e não recebe '{sourceElement.ToDisplayString()}' sem conversão");
        return false;
    }

    private bool AnalyzeScalar(DtoModel model, PropertyMap map, AttributeData? converterAttribute, Location location)
    {
        map.Kind = PropertyKind.Scalar;
        var source = map.SourceType;
        var target = map.DtoType;
        string sourceName = string.Join(".", map.Path.Select(p => p.Name));

        if (converterAttribute is not null)
            return AnalyzeConverter(model, map, converterAttribute, location);

        if (SymbolEqualityComparer.Default.Equals(source, target))
            map.Conversion = ForwardConversion.Identity;
        else if (IsNullableValue(source) && !IsNullableValue(target) &&
                 Underlying(source) is { } underlying &&
                 (SymbolEqualityComparer.Default.Equals(underlying, target) ||
                  _compilation.ClassifyConversion(underlying, target) is { IsImplicit: true, IsUserDefined: false }))
        {
            map.Conversion = ForwardConversion.NullableToValue;
            map.NullableUnderlying = underlying;
        }
        else if (source.TypeKind == TypeKind.Enum && target.SpecialType == SpecialType.System_String)
            map.Conversion = ForwardConversion.EnumToString;
        else if (source.SpecialType == SpecialType.System_String && target.TypeKind == TypeKind.Enum)
            map.Conversion = ForwardConversion.StringToEnum;
        else if (_compilation.ClassifyConversion(source, target).IsImplicit)
            map.Conversion = ForwardConversion.ImplicitCast;
        else
        {
            Report(model, MappingDiagnostics.IncompatibleTypes, location, map.Name, target.ToDisplayString(), sourceName, source.ToDisplayString());
            return false;
        }
        return true;
    }

    private bool AnalyzeConverter(DtoModel model, PropertyMap map, AttributeData attribute, Location location)
    {
        if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol converter)
        {
            Report(model, MappingDiagnostics.InvalidConverter, location, "?", map.Name, "não foi informado (typeof)");
            return false;
        }

        string name = converter.ToDisplayString();
        if (converter.IsAbstract || converter.IsGenericType && converter.IsUnboundGenericType ||
            !converter.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal))
        {
            Report(model, MappingDiagnostics.InvalidConverter, location, name, map.Name, "precisa ser uma classe concreta com construtor público sem parâmetros");
            return false;
        }

        var implementation = converter.AllInterfaces.FirstOrDefault(i =>
            SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, _converterInterface) &&
            SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], map.SourceType) &&
            SymbolEqualityComparer.Default.Equals(i.TypeArguments[1], map.DtoType));
        if (implementation is null)
        {
            Report(model, MappingDiagnostics.InvalidConverter, location, name, map.Name,
                $"precisa implementar IMapConverter<{map.SourceType.ToDisplayString()}, {map.DtoType.ToDisplayString()}>");
            return false;
        }

        map.Conversion = ForwardConversion.Converter;
        map.Converter = converter;
        map.ConverterIsBidirectional = converter.AllInterfaces.Any(i =>
            SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, _bidirectionalInterface) &&
            SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], map.SourceType) &&
            SymbolEqualityComparer.Default.Equals(i.TypeArguments[1], map.DtoType));
        return true;
    }

    // ---------- DTO → entidade ----------

    private void AnalyzeReverse(DtoModel model, PropertyMap map, Location location)
    {
        map.Reverse = ReverseConversion.None;
        var entity = model.Entity!;
        if (map.Path.Count != 1)
        {
            if (map.Sync)
                Report(model, MappingDiagnostics.InvalidSync, location, map.Name, "o caminho achatado (com '.') só vai da entidade para o DTO");
            return;   // achatados: só entidade → DTO (não altera entidades relacionadas)
        }

        var entityProperty = map.Path[0];
        if (IsProtectedMember(entity, entityProperty) || map.DtoProperty.GetMethod is null)
            return;

        bool settable = entityProperty.SetMethod is { IsInitOnly: false } setter && _compilation.IsSymbolAccessibleWithin(setter, model.Dto);
        map.EntityPropertySettable = settable;

        switch (map.Kind)
        {
            case PropertyKind.Scalar:
                if (settable)
                    map.Reverse = ReverseFor(map);
                return;
            case PropertyKind.Object:
                if (map.Sync)
                    ValidateObjectSync(model, map, location);
                return;
            case PropertyKind.List:
                if (map.Sync)
                    ValidateListSync(model, map, location);
                return;
        }
    }

    private ReverseConversion ReverseFor(PropertyMap map)
    {
        var dto = map.DtoType;
        var entity = map.SourceType;
        if (map.Conversion == ForwardConversion.Converter)
            return map.ConverterIsBidirectional ? ReverseConversion.Converter : ReverseConversion.None;
        if (SymbolEqualityComparer.Default.Equals(dto, entity))
            return ReverseConversion.Identity;
        if (entity.TypeKind == TypeKind.Enum && dto.SpecialType == SpecialType.System_String)
            return ReverseConversion.StringToEnum;
        if (entity.SpecialType == SpecialType.System_String && dto.TypeKind == TypeKind.Enum)
            return ReverseConversion.EnumToString;
        if (IsNullableValue(dto) && !IsNullableValue(entity) && Underlying(dto) is { } underlying &&
            (SymbolEqualityComparer.Default.Equals(underlying, entity) ||
             _compilation.ClassifyConversion(underlying, entity) is { IsImplicit: true, IsUserDefined: false }))
            return ReverseConversion.NullableKeep;
        if (_compilation.ClassifyConversion(dto, entity) is { IsImplicit: true })
            return ReverseConversion.ImplicitCast;
        return ReverseConversion.None;   // conversão com perda: não volta
    }

    private void ValidateObjectSync(DtoModel model, PropertyMap map, Location location)
    {
        var nested = Analyze(map.NestedDto!);
        if (!nested.IsValid)
            return;   // o próprio DTO aninhado reporta os erros
        if (!nested.CanCreateEntity && map.EntityPropertySettable)
        {
            Report(model, MappingDiagnostics.InvalidSync, location, map.Name,
                $"a entidade '{nested.Entity!.Name}' não tem construtor público sem parâmetros para ser criada");
            return;
        }
        map.Reverse = ReverseConversion.Identity;
    }

    private void ValidateListSync(DtoModel model, PropertyMap map, Location location)
    {
        if (map.NestedDto is null)
        {
            Report(model, MappingDiagnostics.InvalidSync, location, map.Name, "só coleções de DTOs mapeados são sincronizadas");
            return;
        }

        var nested = Analyze(map.NestedDto);
        if (!nested.IsValid)
            return;
        var element = nested.Entity!;
        var key = element.AllInterfaces.Concat(element.TypeKind == TypeKind.Interface ? new[] { element } : System.Array.Empty<INamedTypeSymbol>())
            .FirstOrDefault(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, _entityInterface))?.TypeArguments[0];
        if (key is null)
        {
            Report(model, MappingDiagnostics.InvalidSync, location, map.Name, $"o elemento '{element.Name}' não implementa IEntity<TKey>");
            return;
        }

        var dtoId = FindProperty(map.NestedDto, "Id", model.Dto);
        if (dtoId is null || !SymbolEqualityComparer.Default.Equals(dtoId.Type, key))
        {
            Report(model, MappingDiagnostics.InvalidSync, location, map.Name,
                $"o DTO do elemento '{map.NestedDto.Name}' precisa de uma propriedade Id do tipo {key.ToDisplayString()}");
            return;
        }

        var collection = CollectionInterface(map.SourceType, element);
        if (collection is null)
        {
            Report(model, MappingDiagnostics.InvalidSync, location, map.Name,
                $"a coleção da entidade ({map.SourceType.ToDisplayString()}) precisa implementar ICollection<{element.Name}> (Add/Remove)");
            return;
        }
        if (!nested.CanCreateEntity)
        {
            Report(model, MappingDiagnostics.InvalidSync, location, map.Name,
                $"a entidade '{element.Name}' não tem construtor público sem parâmetros para criar os itens novos");
            return;
        }

        map.SyncKeyType = key;
        map.EntityPropertySettable &= map.SourceType is INamedTypeSymbol sourceCollection && _list is not null &&
            _compilation.ClassifyConversion(_list.Construct(element), sourceCollection).IsImplicit;
        map.Reverse = ReverseConversion.Identity;
    }

    /// <summary>
    /// Id (de IEntity&lt;TKey&gt;), IsDeleted/DeletedAt (de ISoftDelete) e os campos de autoria (de IAuditable) nunca são
    /// gravados pelo DTO.
    /// </summary>
    private bool IsProtectedMember(INamedTypeSymbol entity, IPropertySymbol property)
    {
        var interfaces = entity.AllInterfaces;
        if (property.Name == "Id" && interfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, _entityInterface)))
            return true;
        if (property.Name is "IsDeleted" or "DeletedAt" && interfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, _softDeleteInterface)))
            return true;
        return property.Name is "CreatedAt" or "CreatedBy" or "CreatedByTenant" or "UpdatedAt" or "UpdatedBy" or "UpdatedByTenant"
                   or "DeletedBy" or "DeletedByTenant" &&
               interfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, _auditableInterface));
    }

    // ---------- Utilitários de tipos ----------

    private IPropertySymbol? FindProperty(ITypeSymbol type, string name, INamedTypeSymbol accessFrom)
    {
        IEnumerable<ITypeSymbol> chain = type.TypeKind == TypeKind.Interface
            ? new[] { type }.Concat(type.AllInterfaces)
            : BaseChain(type);
        foreach (var current in chain)
        {
            foreach (var property in current.GetMembers(name).OfType<IPropertySymbol>())
            {
                if (!property.IsStatic && property.Parameters.Length == 0 && property.GetMethod is { } getter &&
                    _compilation.IsSymbolAccessibleWithin(getter, accessFrom))
                    return property;
            }
        }
        return null;
    }

    private static IEnumerable<ITypeSymbol> BaseChain(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            yield return current;
    }

    private bool IsCollection(ITypeSymbol type, out CollectionShape shape, out ITypeSymbol element)
    {
        shape = CollectionShape.List;
        element = null!;
        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            shape = CollectionShape.Array;
            element = array.ElementType;
            return true;
        }
        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named)
        {
            var definition = named.OriginalDefinition;
            bool supported = SymbolEqualityComparer.Default.Equals(definition, _list) || definition.SpecialType is
                SpecialType.System_Collections_Generic_IList_T or SpecialType.System_Collections_Generic_ICollection_T or
                SpecialType.System_Collections_Generic_IEnumerable_T or SpecialType.System_Collections_Generic_IReadOnlyList_T or
                SpecialType.System_Collections_Generic_IReadOnlyCollection_T;
            if (supported)
            {
                element = named.TypeArguments[0];
                return true;
            }
        }
        return false;
    }

    private static ITypeSymbol? SourceElement(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
            return null;
        if (type is IArrayTypeSymbol array)
            return array.ElementType;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T } enumerable)
            return enumerable.TypeArguments[0];
        return type.AllInterfaces
            .FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)?
            .TypeArguments[0];
    }

    private static INamedTypeSymbol? CollectionInterface(ITypeSymbol type, ITypeSymbol element)
    {
        bool Matches(INamedTypeSymbol i) =>
            i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_ICollection_T &&
            SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], element);

        if (type is INamedTypeSymbol named && Matches(named))
            return named;
        return type.AllInterfaces.FirstOrDefault(Matches);
    }

    private bool IsAssignable(ITypeSymbol source, INamedTypeSymbol target) =>
        SymbolEqualityComparer.Default.Equals(source, target) ||
        _compilation.ClassifyConversion(source, target) is { IsImplicit: true, IsReference: true };

    private bool InThisCompilation(INamedTypeSymbol type) =>
        SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, _compilation.Assembly);

    public static bool IsNullableValue(ITypeSymbol type) =>
        type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    public static ITypeSymbol? Underlying(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeArguments.Length: 1 } named && IsNullableValue(type) ? named.TypeArguments[0] : null;

    private static ITypeSymbol Unannotated(ITypeSymbol type) => type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);

    // ---------- Atributos ----------

    private static AttributeData? Find(ISymbol symbol, string fullName) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == fullName);

    private static string? StringArgument(AttributeData? attribute) =>
        attribute is { ConstructorArguments.Length: 1 } && attribute.ConstructorArguments[0].Value is string value ? value : null;

    private static int? NamedInt(AttributeData? attribute, string name) =>
        attribute?.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value is int value ? value : null;

    private static bool? NamedBool(AttributeData? attribute, string name) =>
        attribute?.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value is bool value ? value : null;

    private static void Report(DtoModel model, DiagnosticDescriptor descriptor, Location location, params object[] args) =>
        model.Diagnostics.Add(DiagnosticInfo.Create(descriptor, location, args));
}
