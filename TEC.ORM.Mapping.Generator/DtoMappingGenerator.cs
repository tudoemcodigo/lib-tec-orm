using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TEC.ORM.Mapping.Generator;

/// <summary>
/// Gera o mapeamento entidade ↔ DTO para toda classe <c>partial</c> com <c>[MapFrom(typeof(Entidade))]</c>: projeção para o
/// banco, conversão em memória, <c>ApplyTo</c>/<c>ToEntity</c> e extensões. Sem reflexão em tempo de execução (AOT).
/// </summary>
/// <remarks>
/// Pipeline incremental: a análise semântica e a geração rodam no <c>transform</c>, que devolve só dados comparáveis por
/// valor (<see cref="GenerationResult"/>: texto gerado e <see cref="DiagnosticInfo"/>). Uma mudança que não altera o
/// resultado de um DTO (ex.: outro arquivo) para no passo <see cref="ModelStepName"/>: o código não é regerado nem os
/// diagnósticos relatados de novo. Código e diagnósticos saem por saídas separadas.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class DtoMappingGenerator : IIncrementalGenerator
{
    /// <summary>Nome do passo com o resultado de cada DTO (para os testes de incrementalidade).</summary>
    public const string ModelStepName = "TecOrmMappingModel";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var results = context.SyntaxProvider.ForAttributeWithMetadataName(
                DtoAnalyzer.MapFromName,
                predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: static (syntax, cancellationToken) => Generate(syntax, cancellationToken))
            .WithTrackingName(ModelStepName);

        context.RegisterSourceOutput(results.Where(static r => r.Source is not null), static (output, result) =>
            output.AddSource(result.HintName, result.Source!));

        context.RegisterSourceOutput(results.Select(static (r, _) => r.Diagnostics).Where(static d => d.Count > 0), static (output, diagnostics) =>
        {
            foreach (var diagnostic in diagnostics)
                output.ReportDiagnostic(diagnostic.ToDiagnostic());
        });
    }

    /// <summary>
    /// Analisa e gera um DTO. Roda dentro do compilador do consumidor: qualquer exceção inesperada vira o diagnóstico
    /// <c>TECORM016</c> (em vez de derrubar o gerador inteiro com o aviso CS8785 e sumir com todo o código gerado).
    /// </summary>
    private static GenerationResult Generate(GeneratorAttributeSyntaxContext syntax, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dto = (INamedTypeSymbol)syntax.TargetSymbol;
        string hintName = HintName(dto);

        // [MapFrom] repetido em mais de uma declaração partial (já é o erro CS0579): só a primeira gera, senão o mesmo
        // arquivo seria adicionado duas vezes e o gerador lançaria exceção
        if (!IsOwner(dto, syntax))
            return new GenerationResult(hintName, null, EquatableArray<DiagnosticInfo>.Empty);

        try
        {
            return GenerateCore(dto, syntax.SemanticModel.Compilation, hintName);
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // falha interna do gerador: relatada como diagnóstico, nunca derruba o compilador do consumidor
        catch (System.Exception exception)
#pragma warning restore CA1031
        {
            var location = dto.Locations.FirstOrDefault();
            return new GenerationResult(hintName, null, new EquatableArray<DiagnosticInfo>(new[]
            {
                DiagnosticInfo.Create(MappingDiagnostics.InternalError, location, dto.Name, exception.GetType().Name, exception.Message)
            }));
        }
    }

    private static GenerationResult GenerateCore(INamedTypeSymbol dto, Compilation compilation, string hintName)
    {

        var analyzer = new DtoAnalyzer(compilation);
        var model = analyzer.Analyze(dto);
        if (!model.IsValid)
            return new GenerationResult(hintName, null, new EquatableArray<DiagnosticInfo>(model.Diagnostics));

        var emitter = new MappingEmitter(analyzer, model);
        string? source = emitter.Emit();
        return emitter.Error is not null
            ? new GenerationResult(hintName, null, new EquatableArray<DiagnosticInfo>(new[] { emitter.Error }))
            : new GenerationResult(hintName, source, EquatableArray<DiagnosticInfo>.Empty);
    }

    /// <summary>
    /// A declaração deste nó tem o primeiro [MapFrom] do tipo (ordem estável: arquivo, depois posição), ou o tipo tem um só.
    /// </summary>
    private static bool IsOwner(INamedTypeSymbol dto, GeneratorAttributeSyntaxContext syntax)
    {
        var applications = dto.GetAttributes()
            .Where(a => a.AttributeClass?.ToDisplayString() == DtoAnalyzer.MapFromName && a.ApplicationSyntaxReference is not null)
            .Select(a => a.ApplicationSyntaxReference!)
            .ToList();
        if (applications.Count <= 1)
            return true;

        var first = applications
            .OrderBy(r => r.SyntaxTree.FilePath, System.StringComparer.Ordinal)
            .ThenBy(r => r.Span.Start)
            .First();
        return syntax.Attributes.Any(a => a.ApplicationSyntaxReference is { } own &&
            own.SyntaxTree == first.SyntaxTree && own.Span == first.Span);
    }

    /// <summary>
    /// Nome do arquivo gerado: nome completo do DTO (pontos mantidos, demais caracteres fora de [A-Za-z0-9_] trocados) mais um
    /// hash estável (FNV-1a) do nome exato. Nomes que ficariam iguais depois da troca (<c>A.B_C.Dto</c> e <c>A_B.C.Dto</c>
    /// quando os pontos viravam "_") ou que só diferem em maiúsculas não colidem (o Roslyn compara sem diferenciar).
    /// </summary>
    internal static string HintName(INamedTypeSymbol dto)
    {
        string fullName = dto.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var sanitized = new StringBuilder(fullName.Length);
        foreach (char c in fullName.StartsWith("global::", System.StringComparison.Ordinal) ? fullName.Substring(8) : fullName)
            sanitized.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.' ? c : '_');

        uint hash = 2166136261;
        foreach (char c in fullName)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return sanitized + "." + hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + ".TecOrmMapping.g.cs";
    }
}
