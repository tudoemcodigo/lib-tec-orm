// netstandard2.0 não tem o tipo que o compilador usa para "init" (records); a declaração interna basta.
// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
