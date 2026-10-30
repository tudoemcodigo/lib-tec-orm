using Microsoft.EntityFrameworkCore;
using TEC.Core.Domain;
using TEC.Core.Security;
using TEC.ORM.Auditing;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore;
using TEC.ORM.EntityFrameworkCore.Auditing;
using TEC.ORM.EntityFrameworkCore.SoftDelete;
using TEC.ORM.Paging;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests;

/// <summary>
/// Entidade de domínio puro (só TEC.Core, sem atributos do TEC.ORM) configurada pela Fluent API: trilha histórica, dado
/// sensível, propriedade ignorada e ordenação equivalem aos atributos.
/// </summary>
public class FluentMetadataTests
{
    private static readonly DateTimeOffset Moment = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    public sealed class Assinante : AuditableAggregateRoot<int>
    {
        private Assinante()
        {
        }

        public string Nome { get; private set; } = string.Empty;

        public string Chave { get; private set; } = string.Empty;

        public string Nota { get; private set; } = string.Empty;

        public static Assinante Criar(int id, string nome, string chave, string nota) =>
            new() { Id = id, Nome = nome, Chave = chave, Nota = nota };
    }

    private sealed class FluentContext(DbContextOptions options) : OrmDbContext(options)
    {
        public DbSet<Assinante> Assinantes => Set<Assinante>();

        public DbSet<OrmAuditRecord> AuditRecords => Set<OrmAuditRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Assinante>(b =>
            {
                b.IsAuditable();
                b.Property(a => a.Nome).IsSortable();
                b.Property(a => a.Chave).IsSensitiveData();
                b.Property(a => a.Nota).IsAuditIgnored();
                b.HasSoftDelete(index: false);
            });
            modelBuilder.ApplyAuditTrailConfiguration();
        }
    }

    private sealed class Ator : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public PrincipalKind Kind => PrincipalKind.User;
        public string? Id => "usr_1";
        public string? TenantId => null;
    }

    private static FluentContext Criar(string banco, bool comTrilha = true)
    {
        var builder = new DbContextOptionsBuilder<FluentContext>()
            .UseInMemoryDatabase(banco)
            .AddInterceptors(new SoftDeleteInterceptor(new FixedTimeProvider(Moment)));
        if (comTrilha)
            builder.AddInterceptors(new HistoricalAuditInterceptor(new DefaultOrmAuditContextProvider(() => new Ator()), new FixedTimeProvider(Moment)));
        return new FluentContext(builder.Options);
    }

    [Test]
    public async Task Fluent_audit_records_trail_masks_sensitive_and_skips_ignored()
    {
        var banco = Guid.NewGuid().ToString("N");
        await using (var ctx = Criar(banco))
        {
            ctx.Assinantes.Add(Assinante.Criar(1, "Ana", "segredo-123", "nota interna"));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = Criar(banco))
        {
            var registro = await ctx.AuditRecords.SingleAsync();

            await Assert.That(registro.EntityName).IsEqualTo(nameof(Assinante));
            await Assert.That(registro.Action).IsEqualTo("Created");
            await Assert.That(registro.NewValues).Contains("***");
            await Assert.That(registro.NewValues).DoesNotContain("segredo-123");
            await Assert.That(registro.AffectedColumns).Contains(nameof(Assinante.Nome));
            await Assert.That(registro.AffectedColumns).DoesNotContain(nameof(Assinante.Nota));
            await Assert.That(registro.NewValues).DoesNotContain("nota interna");
        }
    }

    [Test]
    [Arguments("chave", false)]
    [Arguments("Nota", false)]
    [Arguments("Id", false)]
    [Arguments("nome", true)]
    public async Task Fluent_sortable_is_the_only_accepted_sort(string ordenarPor, bool aceito)
    {
        await using var ctx = Criar(Guid.NewGuid().ToString("N"), comTrilha: false);
        var repositorio = TestOrm.Orm<Assinante, int>(ctx);
        await repositorio.CreateAsync(Assinante.Criar(1, "Ana", "k", "n"));

        var resultado = await repositorio.ListAsync(new PageRequest(1, 10, ordenarPor));

        await Assert.That(resultado.IsSuccess).IsEqualTo(aceito);
        if (!aceito)
            await Assert.That(resultado.Error!.Code).IsEqualTo(OrmErrors.InvalidInputCode);
    }

    [Test]
    public async Task Repository_soft_deletes_a_core_only_entity()
    {
        await using var ctx = Criar(Guid.NewGuid().ToString("N"), comTrilha: false);
        var repositorio = TestOrm.Orm<Assinante, int>(ctx);
        await repositorio.CreateAsync(Assinante.Criar(7, "Bia", "k", "n"));

        var excluido = await repositorio.DeleteAsync(7);
        var depois = await repositorio.GetByIdAsync(7);
        var linha = await ctx.Assinantes.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == 7);

        await Assert.That(excluido.IsSuccess).IsTrue();
        await Assert.That(depois.Error!.Code).IsEqualTo(OrmErrors.NotFoundCode);
        await Assert.That(linha.IsDeleted).IsTrue();
        await Assert.That(linha.DeletedAt).IsEqualTo(Moment);
    }

    [Test]
    public async Task Fluent_audit_marks_only_the_configured_entity()
    {
        var banco = Guid.NewGuid().ToString("N");
        await using var ctx = Criar(banco);
        var tipo = ctx.Model.FindEntityType(typeof(OrmAuditRecord))!;

        await Assert.That(tipo.FindAnnotation("TEC.ORM:Auditable")).IsNull();
        await Assert.That(ctx.Model.FindEntityType(typeof(Assinante))!.FindAnnotation("TEC.ORM:Auditable")?.Value is true).IsTrue();
    }
}
