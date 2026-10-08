using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace TEC.ORM.Mapping.Generator;

/// <summary>
/// Arranjo imutável comparado por valor (elemento a elemento). O pipeline incremental só propaga uma saída quando ela muda,
/// então o modelo que atravessa os passos não pode carregar símbolos, sintaxe ou <see cref="Diagnostic"/>/<see cref="Location"/>.
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T> where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableArray(IEnumerable<T> items)
    {
        _items = items.ToArray();
    }

    public static EquatableArray<T> Empty => new(Array.Empty<T>());

    public int Count => _items?.Length ?? 0;

    public bool Equals(EquatableArray<T> other)
    {
        var left = _items ?? Array.Empty<T>();
        var right = other._items ?? Array.Empty<T>();
        if (left.Length != right.Length)
            return false;
        for (int i = 0; i < left.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i]))
                return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            foreach (var item in _items ?? Array.Empty<T>())
                hash = (hash * 31) + EqualityComparer<T>.Default.GetHashCode(item);
            return hash;
        }
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);
}

/// <summary>Posição de um diagnóstico como dado (arquivo, trecho e linhas), sem referenciar a árvore de sintaxe.</summary>
internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public static LocationInfo? From(Location? location)
    {
        if (location is null || location.SourceTree is null)
            return null;
        return new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
    }

    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);
}

/// <summary>Diagnóstico como dado comparável: regra, posição e argumentos já em texto.</summary>
internal sealed record DiagnosticInfo(DiagnosticDescriptor Descriptor, LocationInfo? Location, EquatableArray<string> Arguments)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, Location? location, params object?[] arguments) =>
        new(descriptor, LocationInfo.From(location),
            new EquatableArray<string>(arguments.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture) ?? string.Empty)));

    public Diagnostic ToDiagnostic() =>
        Diagnostic.Create(Descriptor, Location?.ToLocation() ?? Microsoft.CodeAnalysis.Location.None, Arguments.Cast<object>().ToArray());
}

/// <summary>Saída do passo de análise de um DTO: nome do arquivo, código gerado (ou <c>null</c>) e diagnósticos.</summary>
internal sealed record GenerationResult(string HintName, string? Source, EquatableArray<DiagnosticInfo> Diagnostics);
