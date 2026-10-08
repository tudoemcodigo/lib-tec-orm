using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using TEC.ORM.Abstractions;
using TEC.ORM.Mapping.Generator;

namespace TEC.ORM.Tests.Mapping;

/// <summary>Alerta de exclusão física (TECORM014): código de exemplo compilado em memória com o Roslyn.</summary>
public class HardDeleteAnalyzerTests
{
    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        string platform = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return [.. platform.Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)),
            MetadataReference.CreateFromFile(typeof(IOrmRepository<,>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(TEC.Core.Common.Results.Result).Assembly.Location)];
    }

    private const string Usings = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using TEC.Core.Common.Results;
        using TEC.ORM.Abstractions;
        using TEC.ORM.Entities;
        using TEC.ORM.Specifications;

        namespace Sample;

        public class Customer : Entity<int> { public string Name { get; set; } = ""; }

        """;

    /// <summary>Compila com o analisador e devolve os TECORM014 (e falha se o exemplo não compilar).</summary>
    private static async Task<IReadOnlyList<Diagnostic>> RunAsync(string source)
    {
        var compilation = CSharpCompilation.Create("Sample", [CSharpSyntaxTree.ParseText(Usings + source)], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        await Assert.That(errors).IsEmpty();

        var diagnostics = await compilation.WithAnalyzers([new HardDeleteUsageAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.Where(d => d.Id == "TECORM014")];
    }

    [Test]
    public async Task Call_through_interface_raises_the_warning_with_the_alert()
    {
        var diagnostics = await RunAsync("""
            public static class Usage
            {
                public static Task<Result> Erase(IOrmRepository<Customer, int> orm) => orm.HardDeleteAsync(1);
            }
            """);

        var diagnostic = diagnostics.Single();
        await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Warning);
        await Assert.That(diagnostic.GetMessage()).Contains("será excluído de fato");
        await Assert.That(diagnostic.GetMessage()).Contains("IOrmRepository.HardDeleteAsync");
    }

    [Test]
    public async Task Batch_by_specification_and_by_expression_raise_the_warning()
    {
        var diagnostics = await RunAsync("""
            public static class Usage
            {
                public static async Task Purge(IOrmRepository<Customer, int> orm, DateTimeOffset limit)
                {
                    await orm.HardDeleteAsync(new Specification<Customer>(c => c.IsDeleted));
                    await orm.HardDeleteAsync(c => c.IsDeleted && c.DeletedAt < limit);
                }
            }
            """);

        await Assert.That(diagnostics.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Soft_delete_does_not_raise_the_warning()
    {
        var diagnostics = await RunAsync("""
            public static class Usage
            {
                public static Task<Result> Delete(IOrmRepository<Customer, int> orm) => orm.DeleteAsync(1);
            }
            """);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task Call_through_implementation_without_the_attribute_raises_the_warning()
    {
        // A implementação não repete o atributo: vale o do membro da interface que ela implementa (e o do método sobrescrito)
        var diagnostics = await RunAsync("""
            public abstract class Repository : IOrmRepository<Customer, int>
            {
                public abstract Task<Result<Customer>> CreateAsync(Customer entity, CancellationToken cancellationToken = default);
                public abstract Task<Result<Customer>> GetByIdAsync(int id, CancellationToken cancellationToken = default);
                public abstract Task<Result<Customer>> UpdateAsync(Customer entity, CancellationToken cancellationToken = default);
                public abstract Task<Result> DeleteAsync(int id, CancellationToken cancellationToken = default);
                public virtual Task<Result> HardDeleteAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
                public abstract Task<Result<long>> HardDeleteAsync(ISpecification<Customer> specification, CancellationToken cancellationToken = default);
                public abstract Task<Result<System.Collections.Generic.IReadOnlyList<Customer>>> FindAsync(ISpecification<Customer> specification, CancellationToken cancellationToken = default);
                public abstract Task<Result<TEC.Core.Responses.Pagination.PagedResult<Customer>>> ListAsync(TEC.ORM.Paging.PageRequest page, ISpecification<Customer>? specification = null, CancellationToken cancellationToken = default);
                public abstract Task<Result<bool>> ExistsAsync(int id, CancellationToken cancellationToken = default);
                public abstract Task<Result<bool>> ExistsAsync(ISpecification<Customer> specification, CancellationToken cancellationToken = default);
                public abstract Task<Result<long>> CountAsync(ISpecification<Customer>? specification = null, CancellationToken cancellationToken = default);
                public abstract Task<Result<TDto>> GetByIdAsync<TDto>(int id, CancellationToken cancellationToken = default) where TDto : class, TEC.ORM.Mapping.IDtoMap<Customer, TDto>;
                public abstract Task<Result<System.Collections.Generic.IReadOnlyList<TDto>>> FindAsync<TDto>(ISpecification<Customer> specification, CancellationToken cancellationToken = default) where TDto : class, TEC.ORM.Mapping.IDtoMap<Customer, TDto>;
                public abstract Task<Result<TEC.Core.Responses.Pagination.PagedResult<TDto>>> ListAsync<TDto>(TEC.ORM.Paging.PageRequest page, ISpecification<Customer>? specification = null, CancellationToken cancellationToken = default) where TDto : class, TEC.ORM.Mapping.IDtoMap<Customer, TDto>;
            }

            public abstract class ChildRepository : Repository
            {
                public override Task<Result> HardDeleteAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
            }

            public static class Usage
            {
                public static async Task Erase(Repository repository, ChildRepository child)
                {
                    await repository.HardDeleteAsync(1);
                    await child.HardDeleteAsync(2);
                }
            }
            """);

        await Assert.That(diagnostics.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Marked_method_passes_the_alert_to_its_caller()
    {
        var diagnostics = await RunAsync("""
            public sealed class Purger(IOrmRepository<Customer, int> orm)
            {
                [HardDelete]
                public Task<Result<long>> PurgeDeleted() => orm.HardDeleteAsync(new Specification<Customer>(c => c.IsDeleted));
            }

            public static class Usage
            {
                public static Task<Result<long>> Run(Purger purger) => purger.PurgeDeleted();
            }
            """);

        // Só a chamada a ExpurgarExcluidos: a de dentro do método marcado não gera aviso
        await Assert.That(diagnostics.Single().GetMessage()).Contains("Purger.PurgeDeleted");
    }

    [Test]
    public async Task Warning_suppressed_with_pragma_at_the_call_site()
    {
        var compilation = CSharpCompilation.Create("Sample", [CSharpSyntaxTree.ParseText(Usings + """
            public static class Usage
            {
                public static Task<Result> Erase(IOrmRepository<Customer, int> orm)
                {
            #pragma warning disable TECORM014 // exclusão física intencional
                    return orm.HardDeleteAsync(1);
            #pragma warning restore TECORM014
                }
            }
            """)], References, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new HardDeleteUsageAnalyzer()))
            .GetAllDiagnosticsAsync();

        await Assert.That(diagnostics.Any(d => d.Id == "TECORM014")).IsFalse();
    }
}
