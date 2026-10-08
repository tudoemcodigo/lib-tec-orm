using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TEC.ORM.Mapping;
using TEC.ORM.Mapping.Generator;

namespace TEC.ORM.Tests.Mapping;

/// <summary>Erros de compilação do gerador (TECORMxxx): código de exemplo compilado em memória com o Roslyn.</summary>
public class GeneratorDiagnosticsTests
{
    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        string platform = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return [.. platform.Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)),
            MetadataReference.CreateFromFile(typeof(MapFromAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(TEC.Core.Common.Results.Result).Assembly.Location)];
    }

    private const string Entities = """
        using System;
        using System.Collections.Generic;
        using TEC.ORM.Entities;
        using TEC.ORM.Mapping;

        namespace Sample;

        public class Customer : Entity<int> { public string Name { get; set; } = ""; public List<Order> Orders { get; set; } = new(); public Address Address { get; set; } }
        public class Order : Entity<long> { public decimal Amount { get; set; } public Customer Customer { get; set; } public int? Code { get; set; } }
        public class Address { public string City { get; set; } = ""; }
        """;

    /// <summary>Roda o gerador e devolve os diagnósticos do gerador e os erros de compilação do resultado.</summary>
    private static (IReadOnlyList<Diagnostic> Generator, IReadOnlyList<Diagnostic> Compilation) Run(string source)
    {
        var compilation = CSharpCompilation.Create("Sample",
            [CSharpSyntaxTree.ParseText(Entities), CSharpSyntaxTree.ParseText("using System; using System.Collections.Generic; using TEC.ORM.Mapping; namespace Sample;\n" + source)],
            References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new DtoMappingGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        return (diagnostics, errors);
    }

    private static async Task AssertDiagnostic(string source, string id)
    {
        var (generator, _) = Run(source);
        await Assert.That(generator.Select(d => d.Id)).Contains(id);
    }

    [Test]
    public async Task Valid_dto_generates_code_that_compiles()
    {
        var (generator, compilation) = Run("""
            [MapFrom(typeof(Customer))]
            public partial class CustomerDto
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
                [MapFrom("Address.City")] public string? City { get; set; }
                [MapList(Sync = true)] public List<OrderDto> Orders { get; set; } = new();
            }

            [MapFrom(typeof(Order))]
            public partial class OrderDto
            {
                public long Id { get; set; }
                public decimal Amount { get; set; }
                public int Code { get; set; }
                [MapObject(MaxDepth = 1)] public CustomerDto? Customer { get; set; }
            }

            public static class Usage
            {
                public static void Use(Customer c)
                {
                    CustomerDto dto = c.ToCustomerDto();
                    Customer fresh = dto.ToEntity();
                    dto.ApplyTo(c);
                    var projection = CustomerDto.Projection;
                }
            }
            """);

        await Assert.That(generator.Count).IsEqualTo(0);
        await Assert.That(compilation.Select(d => $"{d.Id}: {d.GetMessage()}")).IsEmpty();
    }

    [Test]
    public async Task TECORM001_dto_not_partial() =>
        await AssertDiagnostic("[MapFrom(typeof(Customer))] public class CustomerDto { public string Name { get; set; } = \"\"; }", "TECORM001");

    [Test]
    public async Task TECORM002_property_without_source() =>
        await AssertDiagnostic("[MapFrom(typeof(Customer))] public partial class CustomerDto { public string Nickname { get; set; } = \"\"; }", "TECORM002");

    [Test]
    public async Task TECORM002_solved_with_MapIgnore()
    {
        var (generator, compilation) = Run("[MapFrom(typeof(Customer))] public partial class CustomerDto { [MapIgnore] public string Nickname { get; set; } = \"\"; }");
        await Assert.That(generator.Count).IsEqualTo(0);
        await Assert.That(compilation.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TECORM003_missing_path() =>
        await AssertDiagnostic("[MapFrom(typeof(Customer))] public partial class CustomerDto { [MapFrom(\"Address.District\")] public string? District { get; set; } }", "TECORM003");

    [Test]
    public async Task TECORM004_incompatible_types() =>
        await AssertDiagnostic("[MapFrom(typeof(Order))] public partial class OrderDto { public int Amount { get; set; } }", "TECORM004");   // decimal → int perde

    [Test]
    public async Task TECORM005_cycle_without_MaxDepth() =>
        await AssertDiagnostic("""
            [MapFrom(typeof(Customer))] public partial class CustomerDto { public List<OrderDto> Orders { get; set; } = new(); }
            [MapFrom(typeof(Order))] public partial class OrderDto { public CustomerDto? Customer { get; set; } }
            """, "TECORM005");

    [Test]
    public async Task TECORM006_object_without_MapFrom() =>
        await AssertDiagnostic("""
            public class AddressDto { public string City { get; set; } = ""; }
            [MapFrom(typeof(Customer))] public partial class CustomerDto { [MapObject] public AddressDto? Address { get; set; } }
            """, "TECORM006");

    [Test]
    public async Task TECORM007_converter_of_wrong_type() =>
        await AssertDiagnostic("""
            public sealed class Conv : IMapConverter<int, string> { public string Convert(int v) => v.ToString(); }
            [MapFrom(typeof(Order))] public partial class OrderDto { [MapConverter(typeof(Conv))] public string Amount { get; set; } = ""; }
            """, "TECORM007");

    [Test]
    public async Task TECORM008_sync_on_flattened_path() =>
        await AssertDiagnostic("""
            [MapFrom(typeof(Address))] public partial class AddressDto { public string City { get; set; } = ""; }
            [MapFrom(typeof(Order))] public partial class OrderDto { [MapObject("Customer.Address", Sync = true)] public AddressDto? Address { get; set; } }
            """, "TECORM008");

    [Test]
    public async Task TECORM008_sync_without_Id_in_the_element_dto() =>
        await AssertDiagnostic("""
            [MapFrom(typeof(Order))] public partial class OrderWithoutIdDto { public decimal Amount { get; set; } }
            [MapFrom(typeof(Customer))] public partial class CustomerDto { [MapList(Sync = true)] public List<OrderWithoutIdDto> Orders { get; set; } = new(); }
            """, "TECORM008");

    [Test]
    public async Task TECORM009_property_without_setter() =>
        await AssertDiagnostic("[MapFrom(typeof(Customer))] public partial class CustomerDto { public string Name => \"x\"; }", "TECORM009");

    [Test]
    public async Task TECORM010_unsupported_collection() =>
        await AssertDiagnostic("""
            [MapFrom(typeof(Order))] public partial class OrderDto { public long Id { get; set; } }
            [MapFrom(typeof(Customer))] public partial class CustomerDto { [MapList] public HashSet<OrderDto> Orders { get; set; } = new(); }
            """, "TECORM010");

    [Test]
    public async Task TECORM011_dto_nested_in_another_type() =>
        await AssertDiagnostic("public static class Outer { [MapFrom(typeof(Customer))] public partial class CustomerDto { public string Name { get; set; } = \"\"; } }", "TECORM011");

    [Test]
    public async Task TECORM012_dto_without_parameterless_constructor() =>
        await AssertDiagnostic("[MapFrom(typeof(Customer))] public partial class CustomerDto(string name) { public string Name { get; set; } = name; }", "TECORM012");

    [Test]
    public async Task Diagnostic_points_to_the_faulty_property()
    {
        var (generator, _) = Run("[MapFrom(typeof(Customer))] public partial class CustomerDto { public string Nickname { get; set; } = \"\"; }");
        var diagnostic = generator.Single(d => d.Id == "TECORM002");

        await Assert.That(diagnostic.GetMessage()).Contains("Nickname");
        await Assert.That(diagnostic.Location.SourceSpan.Length).IsGreaterThan(0);
    }
    // ---------- Robustez dentro do compilador do consumidor ----------

    /// <summary>Texto gerado (todas as saídas) de uma execução do gerador.</summary>
    private static string GeneratedText(string source)
    {
        var compilation = CSharpCompilation.Create("Sample",
            [CSharpSyntaxTree.ParseText(Entities), CSharpSyntaxTree.ParseText("using System; using System.Collections.Generic; using TEC.ORM.Mapping; namespace Sample;\n" + source)],
            References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver.Create(new DtoMappingGenerator()).RunGenerators(compilation);
        return string.Join("\n", driver.GetRunResult().Results.Single().GeneratedSources.Select(s => s.SourceText.ToString()));
    }

    [Test]
    public async Task MapFrom_repeated_on_two_partial_declarations_does_not_crash_the_generator()
    {
        var (generator, compilation) = Run("""
            [MapFrom(typeof(Customer))] public partial class CustomerDto { public string Name { get; set; } = ""; }
            [MapFrom(typeof(Customer))] public partial class CustomerDto { }
            """);

        // CS8785 = exceção no gerador (todo o código gerado some); o atributo duplicado é só o erro normal CS0579
        await Assert.That(generator.Select(d => d.Id)).DoesNotContain("CS8785");
        await Assert.That(compilation.Select(d => d.Id)).Contains("CS0579");
    }

    [Test]
    public async Task TECORM015_open_generic_entity()
    {
        await AssertDiagnostic("""
            public class Box<T> : TEC.ORM.Entities.Entity<int> { public T? Value { get; set; } }
            [MapFrom(typeof(Box<>))] public partial class BoxDto { public int Id { get; set; } }
            """, "TECORM015");
    }

    [Test]
    public async Task TECORM015_entity_type_not_found()
    {
        // Antes caía na checagem de classe (TECORM012, "precisa ser uma classe"), que não explica o problema real
        await AssertDiagnostic("""
            [MapFrom(typeof(DoesNotExist))] public partial class GhostDto { public int Id { get; set; } }
            """, "TECORM015");
    }

    [Test]
    public async Task Generated_code_is_marked_nullable_enabled_qualified_and_deterministic()
    {
        const string source = """
            [MapFrom(typeof(Customer))] public partial class CustomerDto { public int Id { get; set; } public string Name { get; set; } = ""; }
            """;

        string first = GeneratedText(source);
        string second = GeneratedText(source);

        await Assert.That(first).StartsWith("// <auto-generated/>");
        await Assert.That(first).Contains("#nullable enable");
        await Assert.That(first).Contains("global::Sample.Customer");
        await Assert.That(second).IsEqualTo(first);
    }
}
