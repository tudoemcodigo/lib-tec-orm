using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using TEC.Core.Security;
using TEC.ORM.Common;
using TEC.ORM.Entities;
using TEC.ORM.Queries;
using TEC.ORM.Specifications;
using TEC.ORM.SqlServer.Auditing;
using TEC.ORM.SqlServer.SoftDelete;
using TEC.ORM.Tests.Aggregates;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests;

/// <summary>
/// Regressões dos achados da revisão técnica de 2026-10-19 (docs/revisao-tecnica.md) que não cabem num arquivo temático:
/// autoria na exclusão física (SEG-02), listas e <c>LIKE</c> no <c>SqlQuery</c> (SEG-05), corrida no <c>UpdateAsync</c>
/// (BUG-03), autoria devolvida pelo <c>UpdateAsync</c> (BUG-04), dependentes criados já excluídos (BUG-05), teto da propagação
/// da exclusão lógica (BUG-02) e filtro do índice do <c>HasSoftDelete</c> (IMP-02).
/// </summary>
public class RevisionRegressionTests
{
    public sealed class Note : AuditableEntity<int>
    {
        public string Text { get; set; } = string.Empty;
    }

    private sealed class NotesContext(DbContextOptions options) : OrmDbContext(options)
    {
        public DbSet<Note> Notes => Set<Note>();
    }

    private sealed class User(string? id) : ICurrentUser
    {
        public bool IsAuthenticated => id is not null;

        public PrincipalKind Kind => id is null ? PrincipalKind.Anonymous : PrincipalKind.User;

        public string? Id => id;

        public string? TenantId => null;
    }

    private static readonly DateTimeOffset Created = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static NotesContext Notes(string database, ICurrentUser? user, DateTimeOffset now)
    {
        var time = new FixedTimeProvider(now);
        return new NotesContext(new DbContextOptionsBuilder<NotesContext>()
            .UseInMemoryDatabase(database)
            .AddInterceptors(new SoftDeleteInterceptor(time), new AuditInterceptor(time))
            .UseTecOrmIdentity(() => user)
            .Options);
    }

    /// <summary>Setter privado declarado na classe base (<c>Entity</c>/<c>AuditableEntity</c>): simula o valor forjado pelo cliente.</summary>
    private static void SetPrivate(object entity, string property, object? value)
    {
        var declaring = entity.GetType().GetProperty(property)!.DeclaringType!;
        declaring.GetProperty(property)!.SetValue(entity, value);
    }

    // ---------- SEG-02: exclusão física de entidade auditada exige identidade ----------

    [Test]
    public async Task Hard_delete_of_auditable_entity_without_identity_is_rejected()
    {
        string db = Guid.NewGuid().ToString("N");
        await using (var creation = Notes(db, new User("oid-ana"), Created))
            await Assert.That((await TestOrm.Orm<Note, int>(creation).CreateAsync(new Note { Id = 1, Text = "a" })).IsSuccess).IsTrue();

        await using var anonymous = Notes(db, new User(null), Created);
        var notes = TestOrm.Orm<Note, int>(anonymous);

        // Recusada antes de ir ao banco (o InMemory nem executaria o ExecuteDelete)
#pragma warning disable TECORM014 // exclusão física é o objeto do teste
        var single = await notes.HardDeleteAsync(1);
        var batch = await notes.HardDeleteAsync(new Specification<Note>(n => n.Id == 1));
#pragma warning restore TECORM014

        await Assert.That(single.Error!.Code).IsEqualTo(OrmErrors.AuditIdentityRequiredCode);
        await Assert.That(batch.Error!.Code).IsEqualTo(OrmErrors.AuditIdentityRequiredCode);
        await Assert.That(await anonymous.Notes.CountAsync()).IsEqualTo(1);
    }

    // ---------- BUG-04: UpdateAsync devolve a autoria gravada ----------

    [Test]
    public async Task Update_returns_the_stored_authorship_not_the_one_sent()
    {
        string db = Guid.NewGuid().ToString("N");
        await using (var creation = Notes(db, new User("oid-ana"), Created))
            await TestOrm.Orm<Note, int>(creation).CreateAsync(new Note { Id = 1, Text = "a" });

        var later = Created.AddDays(1);
        await using var context = Notes(db, new User("oid-bia"), later);
        var forged = new Note { Id = 1, Text = "b" };
        SetPrivate(forged, nameof(IAuditable.CreatedBy), "admin");
        SetPrivate(forged, nameof(IAuditable.CreatedAt), DateTimeOffset.UnixEpoch);
        SetPrivate(forged, nameof(IAuditable.DeletedBy), "forjado");

        var result = await TestOrm.Orm<Note, int>(context).UpdateAsync(forged);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Text).IsEqualTo("b");
        await Assert.That(result.Value.CreatedBy).IsEqualTo("oid-ana");
        await Assert.That(result.Value.CreatedAt).IsEqualTo(Created);
        await Assert.That(result.Value.DeletedBy).IsNull();
        await Assert.That(result.Value.UpdatedBy).IsEqualTo("oid-bia");
        await Assert.That(result.Value.UpdatedAt).IsEqualTo(later);

        await using var reading = Notes(db, new User("oid-ana"), later);
        var stored = await reading.Notes.AsNoTracking().SingleAsync();
        await Assert.That(stored.CreatedBy).IsEqualTo("oid-ana");
        await Assert.That(stored.Text).IsEqualTo("b");
    }

    // ---------- BUG-03: exclusão lógica entre a verificação e o UPDATE ----------

    /// <summary>Executa uma ação uma única vez, logo antes do primeiro salvamento com entidade alterada.</summary>
    private sealed class BeforeUpdate(Func<Task> action) : SaveChangesInterceptor
    {
        private int _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries().Any(e => e.State == EntityState.Modified) &&
                Interlocked.Exchange(ref _done, 1) == 0)
                await action();
            return result;
        }
    }

    [Test]
    public async Task Concurrent_soft_delete_between_check_and_update_is_concurrency_and_keeps_the_record()
    {
        string db = Guid.NewGuid().ToString("N");
        Guid id;
        await using (var setup = TestOrm.InMemoryContext(database: db))
            id = (await TestOrm.Orm<Customer, Guid>(setup).CreateAsync(new Customer { Name = "Ana", Email = "ana@exemplo.com" })).Value.Id;

        // Outra requisição exclui o cliente depois do AnyAsync e antes do UPDATE desta
        var race = new BeforeUpdate(async () =>
        {
            await using var other = TestOrm.InMemoryContext(database: db);
            await Assert.That((await TestOrm.Orm<Customer, Guid>(other).DeleteAsync(id)).IsSuccess).IsTrue();
        });
        await using var context = new CodeFirstContext(new DbContextOptionsBuilder<CodeFirstContext>()
            .UseInMemoryDatabase(db)
            .AddInterceptors(new SoftDeleteInterceptor(TimeProvider.System), race)
            .Options);

        var result = await TestOrm.Orm<Customer, Guid>(context).UpdateAsync(new Customer { Id = id, Name = "Alterado", Email = "x@exemplo.com" });

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConcurrencyCode);
        await using var reading = TestOrm.InMemoryContext(database: db);
        var stored = await reading.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == id);
        await Assert.That(stored.IsDeleted).IsTrue();
        await Assert.That(stored.Name).IsEqualTo("Ana");
    }

    [Test]
    public async Task Is_deleted_is_a_concurrency_token_of_soft_delete_entities()
    {
        await using var context = TestOrm.InMemoryContext();

        await Assert.That(context.Model.FindEntityType(typeof(Customer))!.FindProperty(nameof(ISoftDelete.IsDeleted))!.IsConcurrencyToken).IsTrue();
    }

    // ---------- BUG-05: dependentes novos do grafo nunca nascem excluídos ----------

    [Test]
    public async Task Create_never_inserts_graph_dependents_already_deleted()
    {
        string db = Guid.NewGuid().ToString("N");
        var order = new Order { ProductId = 1, Quantity = 1, Amount = 10m };
        SetPrivate(order, nameof(ISoftDelete.IsDeleted), true);
        SetPrivate(order, nameof(ISoftDelete.DeletedAt), DateTimeOffset.UnixEpoch);

        await using (var context = TestOrm.InMemoryContext(database: db))
        {
            var created = await TestOrm.Orm<Customer, Guid>(context)
                .CreateAsync(new Customer { Name = "Ana", Email = "ana@exemplo.com", Orders = [order] });
            await Assert.That(created.IsSuccess).IsTrue();
        }

        await using var reading = TestOrm.InMemoryContext(database: db);
        var stored = await reading.Orders.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        await Assert.That(stored.IsDeleted).IsFalse();
        await Assert.That(stored.DeletedAt).IsNull();
    }

    // ---------- BUG-02: teto da propagação da exclusão lógica ----------

    private static AggregateContext Aggregates(string db, int limit = SoftDeleteInterceptor.DefaultMaxPropagation) =>
        new(new DbContextOptionsBuilder<AggregateContext>()
            .UseInMemoryDatabase(db)
            .AddInterceptors(new SoftDeleteInterceptor(TimeProvider.System, limit))
            .Options);

    /// <summary>Venda com <paramref name="installments"/> parcelas (cascata, exclusão lógica), cada uma com um lançamento.</summary>
    private static async Task<(string Db, int Sale)> SaleWithInstallmentsAsync(int installments)
    {
        string db = Guid.NewGuid().ToString("N");
        await using var context = Aggregates(db);
        var sale = new Sale { Name = "Venda" };
        for (int i = 0; i < installments; i++)
            sale.Installments.Add(new Installment { Amount = i, LedgerEntries = [new LedgerEntry { Memo = "l" + i }] });
        await Assert.That((await TestOrm.Orm<Sale, int>(context).CreateAsync(sale)).IsSuccess).IsTrue();
        return (db, sale.Id);
    }

    [Test]
    public async Task Propagation_beyond_the_limit_fails_without_saving_or_loading_everything()
    {
        var (db, id) = await SaleWithInstallmentsAsync(20);

        await using (var context = Aggregates(db, limit: 5))
        {
            var result = await TestOrm.Orm<Sale, int>(context).DeleteAsync(id);

            await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.TooManyResultsCode);
            // A carga lê no máximo o que cabe no teto (+1), não as 20 parcelas
            await Assert.That(context.ChangeTracker.Entries<Installment>().Count()).IsLessThanOrEqualTo(6);
        }

        await using var reading = Aggregates(db);
        await Assert.That(await reading.Sales.CountAsync()).IsEqualTo(1);
        await Assert.That(await reading.Installments.CountAsync()).IsEqualTo(20);
        await Assert.That(await reading.LedgerEntries.CountAsync()).IsEqualTo(20);
    }

    [Test]
    public async Task Propagation_up_to_the_limit_deletes_all_dependents()
    {
        // 4 parcelas + 4 lançamentos = 8 dependentes, exatamente o teto
        var (db, id) = await SaleWithInstallmentsAsync(4);

        await using (var context = Aggregates(db, limit: 8))
            await Assert.That((await TestOrm.Orm<Sale, int>(context).DeleteAsync(id)).IsSuccess).IsTrue();

        await using var reading = Aggregates(db);
        await Assert.That(await reading.Sales.CountAsync()).IsEqualTo(0);
        await Assert.That(await reading.Installments.CountAsync()).IsEqualTo(0);
        await Assert.That(await reading.LedgerEntries.CountAsync()).IsEqualTo(0);
        await Assert.That(await reading.LedgerEntries.IgnoreQueryFilters().CountAsync(l => l.IsDeleted)).IsEqualTo(4);
    }

    [Test]
    public async Task Propagation_limit_also_applies_to_tracked_dependents()
    {
        var (db, id) = await SaleWithInstallmentsAsync(4);

        await using (var context = Aggregates(db, limit: 7))
        {
            // Tudo já rastreado: nada a carregar, o teto vale na marcação (8 dependentes > 7)
            await context.Sales.Include(s => s.Installments).ThenInclude(i => i.LedgerEntries).SingleAsync();
            var result = await TestOrm.Orm<Sale, int>(context).DeleteAsync(id);

            await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.TooManyResultsCode);
        }

        await using var reading = Aggregates(db);
        await Assert.That(await reading.Installments.CountAsync()).IsEqualTo(4);
    }

    // ---------- IMP-02: filtro do índice com o nome real da coluna ----------

    private sealed class RenamedContext(DbContextOptions options) : OrmDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Category>(e =>
            {
                e.HasSoftDelete();
                e.Property(c => c.IsDeleted).HasColumnName("Excluido");
            });
    }

    [Test]
    public async Task Soft_delete_index_filter_uses_the_mapped_column_name()
    {
        await using var context = new RenamedContext(new DbContextOptionsBuilder<RenamedContext>().UseSqlServer().Options);
        var model = context.GetService<IDesignTimeModel>().Model;

        var index = model.FindEntityType(typeof(Category))!.GetIndexes()
            .Single(i => i.Properties.Single().Name == nameof(ISoftDelete.IsDeleted));

        await Assert.That(index.GetFilter()).IsEqualTo("[Excluido] = 0");
        await Assert.That(index.FindAnnotation(SoftDeleteQueryFilterConvention.SoftDeleteIndexAnnotation)).IsNull();
    }

    // ---------- SEG-05: listas contam no limite de parâmetros; LIKE com escape ----------

    [Test]
    public async Task List_items_count_toward_the_parameter_limit()
    {
        var withinLimit = Enumerable.Range(0, SqlQuery.MaxParameters - 1).ToList();
        var tooMany = Enumerable.Range(0, SqlQuery.MaxParameters).ToList();
        IEnumerable<int> lazy = Enumerable.Range(0, SqlQuery.MaxParameters * 10);

        var accepted = SqlQuery.Interpolated("pedidos.por-ids", $"SELECT Id FROM Orders WHERE Id IN {withinLimit} AND Quantity > {1}");

        await Assert.That(accepted.Parameters.Count).IsEqualTo(2);
        await Assert.That(() => SqlQuery.Interpolated("pedidos.por-ids", $"SELECT Id FROM Orders WHERE Id IN {tooMany} AND Quantity > {1}"))
            .Throws<ArgumentException>();
        await Assert.That(() => SqlQuery.Interpolated("pedidos.por-ids", $"SELECT Id FROM Orders WHERE Id IN {lazy}"))
            .Throws<ArgumentException>();
        await Assert.That(SqlQuery.Create("pedidos.por-ids", "SELECT Id FROM Orders WHERE Id IN @Ids", new { Ids = tooMany }).Parameters.Count)
            .IsEqualTo(1);
        tooMany.Add(-1);
        await Assert.That(() => SqlQuery.Create("pedidos.por-ids", "SELECT Id FROM Orders WHERE Id IN @Ids", new { Ids = tooMany }))
            .Throws<ArgumentException>();
        // Texto e binário são um parâmetro só
        var text = SqlQuery.Interpolated("texto", $"SELECT {new string('a', 5_000)}, {new byte[5_000]}");
        await Assert.That(text.Parameters.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("100%", @"100\%")]
    [Arguments("a_b", @"a\_b")]
    [Arguments("[x]", @"\[x]")]
    [Arguments(@"c:\pasta", @"c:\\pasta")]
    [Arguments("comum", "comum")]
    public async Task Escape_like_neutralizes_wildcards(string value, string expected)
    {
        await Assert.That(SqlQuery.EscapeLike(value)).IsEqualTo(expected);
    }
}
