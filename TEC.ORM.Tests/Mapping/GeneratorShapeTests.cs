using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TEC.ORM.Mapping;
using TEC.ORM.Mapping.Generator;

namespace TEC.ORM.Tests.Mapping;

/// <summary>
/// Formas de declaração no gerador: nomes de arquivo sem colisão, record struct, palavras-chave, colisão com membros gerados,
/// namespace global, acessibilidade, tipos aninhados e incrementalidade do pipeline.
/// </summary>
public class GeneratorShapeTests
{
    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        string platform = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return [.. platform.Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)),
            MetadataReference.CreateFromFile(typeof(MapFromAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(TEC.Core.Common.Results.Result).Assembly.Location)];
    }

    private static CSharpCompilation Compilation(params string[] sources) =>
        CSharpCompilation.Create("Sample", sources.Select(s => CSharpSyntaxTree.ParseText(s, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))),
            References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>Diagnósticos do gerador, erros de compilação do resultado e os arquivos gerados.</summary>
    private static (IReadOnlyList<Diagnostic> Generator, IReadOnlyList<string> Errors, IReadOnlyList<string> HintNames) Run(params string[] sources)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new DtoMappingGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(Compilation(sources), out var output, out var diagnostics);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Id}: {d.GetMessage()}").ToList();
        var hintNames = driver.GetRunResult().Results.Single().GeneratedSources.Select(s => s.HintName).ToList();
        return (diagnostics, errors, hintNames);
    }

    private const string Usings = "using System; using System.Collections.Generic; using TEC.ORM.Entities; using TEC.ORM.Mapping;\n";

    [Test]
    public async Task Namespaces_differing_only_by_dot_and_underscore_do_not_collide()
    {
        var (generator, errors, hintNames) = Run(Usings + """
            namespace A.B_C { public class Ent : Entity<int> { public string Name { get; set; } = ""; } [MapFrom(typeof(Ent))] public partial class Dto { public string Name { get; set; } = ""; } }
            namespace A_B.C { public class Ent : Entity<int> { public string Name { get; set; } = ""; } [MapFrom(typeof(Ent))] public partial class Dto { public string Name { get; set; } = ""; } }
            """);

        await Assert.That(generator).IsEmpty();
        await Assert.That(errors).IsEmpty();
        await Assert.That(hintNames.Count).IsEqualTo(2);
        await Assert.That(hintNames.Distinct(StringComparer.OrdinalIgnoreCase).Count()).IsEqualTo(2);
    }

    [Test]
    public async Task Record_struct_is_TECORM011()
    {
        var (generator, errors, hintNames) = Run(Usings + """
            namespace Sample;
            public class Ent : Entity<int> { public string Name { get; set; } = ""; }
            [MapFrom(typeof(Ent))] public partial record struct EntDto { public string Name { get; set; } }
            """);

        await Assert.That(generator.Select(d => d.Id)).Contains("TECORM011");
        await Assert.That(hintNames).IsEmpty();
        // Só o erro do próprio C# (MapFrom não vale em struct); nada de "partial record" gerado em conflito (CS0261)
        await Assert.That(errors.All(e => e.StartsWith("CS0592", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Record_class_is_still_supported()
    {
        var (generator, errors, _) = Run(Usings + """
            namespace Sample;
            public class Ent : Entity<int> { public string Name { get; set; } = ""; }
            [MapFrom(typeof(Ent))] public partial record EntDto { public string Name { get; set; } = ""; }
            """);

        await Assert.That(generator).IsEmpty();
        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task Keywords_as_names_generate_valid_code()
    {
        var (generator, errors, _) = Run(Usings + """
            namespace Sample.@namespace;
            public class @event : Entity<int> { public string @class { get; set; } = ""; public @event? @base { get; set; } public int @int { get; set; } }
            [MapFrom(typeof(@event))] public partial class @struct
            {
                public string @class { get; set; } = "";
                [MapFrom("base.class")] public string? BaseClass { get; set; }
                public int @int { get; set; }
            }
            public static class Usage { public static void Use(@event e) { var d = new[] { e }.TostructList(); var um = e.Tostruct(); var p = @struct.Projection; new @struct().ApplyTo(e); } }
            """);

        await Assert.That(generator).IsEmpty();
        await Assert.That(errors).IsEmpty();
    }

    [Test]
    [Arguments("public string Projection { get; set; } = \"\";")]
    [Arguments("[MapIgnore] public int ApplyTo { get; set; }")]
    [Arguments("public static Ent FromEntity() => null!;")]
    [Arguments("public void ToEntity(int x) { }")]
    [Arguments("private int __TecOrmApply;")]
    public async Task Member_named_like_a_generated_member_is_TECORM013(string member)
    {
        var (generator, errors, hintNames) = Run(Usings + """
            namespace Sample;
            public class Ent : Entity<int> { public string Name { get; set; } = ""; public string Projection { get; set; } = ""; }
            [MapFrom(typeof(Ent))] public partial class EntDto { public string Name { get; set; } = ""; MEMBER }
            """.Replace("MEMBER", member, StringComparison.Ordinal));

        await Assert.That(generator.Select(d => d.Id)).Contains("TECORM013");
        await Assert.That(hintNames).IsEmpty();
        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task Global_namespace()
    {
        var (generator, errors, hintNames) = Run(Usings + """
            public class EntGlobal : Entity<int> { public string Name { get; set; } = ""; }
            [MapFrom(typeof(EntGlobal))] public partial class EntGlobalDto { public int Id { get; set; } public string Name { get; set; } = ""; }
            public static class GlobalUsage { public static EntGlobalDto Use(EntGlobal e) => e.ToEntGlobalDto(); }
            """);

        await Assert.That(generator).IsEmpty();
        await Assert.That(errors).IsEmpty();
        await Assert.That(hintNames.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Internal_entity_with_public_DTO_generates_valid_code()
    {
        var (generator, errors, _) = Run(Usings + """
            namespace Sample;
            internal class Inner : Entity<int> { public string Name { get; set; } = ""; }
            [MapFrom(typeof(Inner))] public partial class InnerDto { public int Id { get; set; } public string Name { get; set; } = ""; }
            internal static class Usage
            {
                public static void Use(Inner e)
                {
                    InnerDto d = e.ToInnerDto();
                    d = InnerDto.FromEntity(e);
                    var p = InnerDto.Projection;
                    d.ApplyTo(e);
                    Inner fresh = d.ToEntity();
                    IDtoMap<Inner, InnerDto> contrato = d;   // a interface continua implementada
                }
            }
            """);

        await Assert.That(generator).IsEmpty();
        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task Entity_nested_in_another_type_is_supported()
    {
        var (generator, errors, _) = Run(Usings + """
            namespace Sample;
            public static class Module { public class Ent : Entity<int> { public string Name { get; set; } = ""; } }
            [MapFrom(typeof(Module.Ent))] public partial class EntDto { public string Name { get; set; } = ""; }
            """);

        await Assert.That(generator).IsEmpty();
        await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task DTO_nested_in_another_type_is_TECORM011()
    {
        var (generator, _, hintNames) = Run(Usings + """
            namespace Sample;
            public class Ent : Entity<int> { public string Name { get; set; } = ""; }
            public static partial class Outer { [MapFrom(typeof(Ent))] public partial class EntDto { public string Name { get; set; } = ""; } }
            """);

        await Assert.That(generator.Select(d => d.Id)).Contains("TECORM011");
        await Assert.That(hintNames).IsEmpty();
    }

    // ---------- Incrementalidade ----------

    [Test]
    public async Task Irrelevant_change_neither_regenerates_nor_reports_again()
    {
        var compilation = Compilation(Usings + """
            namespace Sample;
            public class Ent : Entity<int> { public string Name { get; set; } = ""; }
            [MapFrom(typeof(Ent))] public partial class EntDto { public string Name { get; set; } = ""; }
            [MapFrom(typeof(Ent))] public partial class WithErrorDto { public string Nickname { get; set; } = ""; }
            """);
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new DtoMappingGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);

        // Arquivo novo, sem relação com os DTOs
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Other; public class Something { }")));
        var result = driver.GetRunResult().Results.Single();

        var model = result.TrackedSteps[DtoMappingGenerator.ModelStepName].SelectMany(s => s.Outputs).ToList();
        await Assert.That(model.Count).IsEqualTo(2);
        await Assert.That(model.All(o => o.Reason is IncrementalStepRunReason.Unchanged or IncrementalStepRunReason.Cached)).IsTrue();
        var outputs = result.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs).ToList();
        await Assert.That(outputs.Count).IsGreaterThan(0);
        await Assert.That(outputs.All(o => o.Reason == IncrementalStepRunReason.Cached)).IsTrue();
        await Assert.That(result.Diagnostics.Select(d => d.Id)).Contains("TECORM002");
    }
}
