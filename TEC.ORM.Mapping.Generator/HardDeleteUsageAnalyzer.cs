using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace TEC.ORM.Mapping.Generator;

/// <summary>
/// Alerta de exclusão física (TECORM014): aviso em toda chamada a um método marcado com <c>[HardDelete]</c> (ou que sobrescreve
/// ou implementa um método marcado), porque o registro será excluído de fato do banco, sem volta.
/// </summary>
/// <remarks>
/// Chamadas feitas de dentro de um método também marcado não geram o aviso: o alerta passa para quem chama esse método.
/// Para confirmar a intenção, suprima no local (<c>#pragma warning disable TECORM014</c>).
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HardDeleteUsageAnalyzer : DiagnosticAnalyzer
{
    private const string HardDeleteName = "TEC.ORM.Abstractions.HardDeleteAttribute";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(UsageDiagnostics.HardDelete);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var attribute = start.Compilation.GetTypeByMetadataName(HardDeleteName);
            if (attribute is null)
                return;   // TEC.ORM não referenciado

            start.RegisterOperationAction(operation =>
            {
                var invocation = (IInvocationOperation)operation.Operation;
                if (!IsHardDelete(invocation.TargetMethod, attribute) ||
                    operation.ContainingSymbol is IMethodSymbol caller && IsHardDelete(caller, attribute))
                    return;

                var method = invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;
                operation.ReportDiagnostic(Diagnostic.Create(UsageDiagnostics.HardDelete, invocation.Syntax.GetLocation(),
                    $"{method.ContainingType.Name}.{method.Name}"));
            }, OperationKind.Invocation);
        });
    }

    /// <summary>O método, algum que ele sobrescreve ou algum membro de interface que ele implementa tem <c>[HardDelete]</c>.</summary>
    private static bool IsHardDelete(IMethodSymbol method, INamedTypeSymbol attribute)
    {
        method = method.ReducedFrom ?? method;
        for (var current = method.OriginalDefinition; current is not null; current = current.OverriddenMethod?.OriginalDefinition)
        {
            if (HasAttribute(current, attribute) || ImplementsMarked(current, attribute))
                return true;
        }
        return false;
    }

    private static bool ImplementsMarked(IMethodSymbol method, INamedTypeSymbol attribute)
    {
        if (method.ExplicitInterfaceImplementations.Any(m => HasAttribute(m.OriginalDefinition, attribute)))
            return true;

        var type = method.ContainingType;
        if (type is null || type.TypeKind == TypeKind.Interface)
            return false;
        foreach (var contract in type.AllInterfaces)
        {
            foreach (var member in contract.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                if (HasAttribute(member.OriginalDefinition, attribute) &&
                    SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member)?.OriginalDefinition, method))
                    return true;
            }
        }
        return false;
    }

    private static bool HasAttribute(IMethodSymbol method, INamedTypeSymbol attribute) =>
        method.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));
}

/// <summary>Avisos de uso do TEC.ORM (TECORM014 em diante).</summary>
internal static class UsageDiagnostics
{
    private const string Category = "TEC.ORM.Usage";

    public static readonly DiagnosticDescriptor HardDelete = new("TECORM014",
        "Exclusão física: o registro será excluído de fato",
        "ATENÇÃO: '{0}' faz exclusão física: o registro será excluído de fato do banco (DELETE), sem exclusão lógica e sem como " +
        "desfazer. Se for intencional, suprima este aviso no local da chamada (#pragma warning disable TECORM014); para o fluxo " +
        "normal, use DeleteAsync (exclusão lógica).",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true,
        description: "Métodos marcados com [HardDelete] removem a linha do banco de forma irreversível.");
}
