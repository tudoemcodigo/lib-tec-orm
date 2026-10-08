using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TEC.ORM.Common;
using TEC.ORM.Paging;
using TEC.ORM.SqlServer;
using TEC.ORM.SqlServer.SoftDelete;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests.Aggregates;

/// <summary>
/// Exclusão lógica propagada no agregado, atualização só da raiz, concorrência otimista e lista branca da ordenação
/// (EF Core InMemory).
/// </summary>
public class AggregateTests
{
    private static readonly DateTimeOffset Moment = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Banco InMemory compartilhado por vários contextos (cada um simula um escopo).</summary>
    private sealed class InMemoryStore
    {
        private readonly string _name = Guid.NewGuid().ToString("N");

        public AggregateContext Create(DateTimeOffset? now = null) =>
            new(new DbContextOptionsBuilder<AggregateContext>()
                .UseInMemoryDatabase(_name)
                .AddInterceptors(new SoftDeleteInterceptor(new FixedTimeProvider(now ?? Moment)))
                .Options);
    }

    private static OrmRepository<Sale, int> Sales(AggregateContext context) => TestOrm.Orm<Sale, int>(context);

    /// <summary>Venda com endereço, 2 etiquetas, 1 item, 2 parcelas (uma com lançamento) e 1 nota; devolve os ids.</summary>
    private static async Task<(int Sale, int Note)> CreateSaleAsync(InMemoryStore store)
    {
        await using var context = store.Create();
        var sale = new Sale
        {
            Name = "V1",
            Version = 1,
            Address = new ShippingAddress { Street = "Street A" },
            Labels = [new Label { Value = "e1" }, new Label { Value = "e2" }],
            Items = [new SaleItem { Description = "i1" }],
            Installments = [new Installment { Amount = 10, LedgerEntries = [new LedgerEntry { Memo = "l1" }] }, new Installment { Amount = 20 }],
            Notes = [new Note { Text = "n1" }]
        };
        var created = await Sales(context).CreateAsync(sale);
        await Assert.That(created.IsSuccess).IsTrue();
        return (sale.Id, sale.Notes[0].Id);
    }

    private static async Task AssertPropagatedAsync(InMemoryStore store, int saleId, int noteId)
    {
        await using var context = store.Create();
        var sale = await context.Sales.IgnoreQueryFilters().AsNoTracking().SingleAsync(v => v.Id == saleId);
        var installments = await context.Installments.IgnoreQueryFilters().AsNoTracking().Where(p => p.SaleId == saleId).ToListAsync();
        var ledgerEntries = await context.LedgerEntries.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        var note = await context.Notes.AsNoTracking().SingleAsync(n => n.Id == noteId);

        await Assert.That(sale.IsDeleted).IsTrue();
        await Assert.That(sale.DeletedAt).IsEqualTo(Moment);
        // Owned preservados
        await Assert.That(sale.Address?.Street).IsEqualTo("Street A");
        await Assert.That(sale.Labels.Count).IsEqualTo(2);
        // Cascata sem ISoftDelete preservada; dependente opcional sem a FK anulada
        await Assert.That(await context.Items.CountAsync(i => i.SaleId == saleId)).IsEqualTo(1);
        await Assert.That(note.SaleId).IsEqualTo(saleId);
        // Cascata ISoftDelete propagada, recursivamente e com o mesmo carimbo
        await Assert.That(installments.Count).IsEqualTo(2);
        await Assert.That(installments.All(p => p.IsDeleted && p.DeletedAt == Moment)).IsTrue();
        await Assert.That(ledgerEntries.Count).IsEqualTo(1);
        await Assert.That(ledgerEntries.All(l => l.IsDeleted && l.DeletedAt == Moment)).IsTrue();
    }

    // ---------- Exclusão lógica propagada ----------

    [Test]
    public async Task Deleting_loaded_aggregate_propagates_to_ISoftDelete_and_preserves_owned_and_dependents()
    {
        var store = new InMemoryStore();
        var (saleId, noteId) = await CreateSaleAsync(store);

        await using (var context = store.Create())
        {
            // O handler carregou o agregado inteiro antes de excluir
            await context.Sales.Include(v => v.Items).Include(v => v.Notes).Include(v => v.Installments).ThenInclude(p => p.LedgerEntries)
                .SingleAsync(v => v.Id == saleId);
            var result = await Sales(context).DeleteAsync(saleId);
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(context.ChangeTracker.CascadeDeleteTiming).IsEqualTo(CascadeTiming.Immediate);   // restaurado
        }

        await AssertPropagatedAsync(store, saleId, noteId);
    }

    [Test]
    public async Task Deleting_unloaded_aggregate_loads_and_propagates_to_ISoftDelete()
    {
        var store = new InMemoryStore();
        var (saleId, noteId) = await CreateSaleAsync(store);

        await using (var context = store.Create())
            await Assert.That((await Sales(context).DeleteAsync(saleId)).IsSuccess).IsTrue();

        await AssertPropagatedAsync(store, saleId, noteId);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Direct_remove_with_immediate_cascade_undoes_the_tracker_cascade(bool async)
    {
        var store = new InMemoryStore();
        var (saleId, noteId) = await CreateSaleAsync(store);

        await using (var context = store.Create())
        {
            var sale = await context.Sales.Include(v => v.Items).Include(v => v.Notes).Include(v => v.Installments).ThenInclude(p => p.LedgerEntries)
                .SingleAsync(v => v.Id == saleId);
            // CascadeTiming.Immediate: o EF já marca owned, itens e parcelas como Deleted e anula a FK da nota
            context.Sales.Remove(sale);
            if (async)
                await context.SaveChangesAsync();
            else
                context.SaveChanges();
        }

        await AssertPropagatedAsync(store, saleId, noteId);
    }

    [Test]
    public async Task Already_deleted_dependent_keeps_the_original_timestamp()
    {
        var store = new InMemoryStore();
        var (saleId, _) = await CreateSaleAsync(store);
        var before = Moment.AddDays(-1);
        int installmentId;
        await using (var context = store.Create(before))
        {
            installmentId = (await context.Installments.FirstAsync(p => p.SaleId == saleId)).Id;
            await Assert.That((await TestOrm.Orm<Installment, int>(context).DeleteAsync(installmentId)).IsSuccess).IsTrue();
        }

        await using (var context = store.Create())
            await Assert.That((await Sales(context).DeleteAsync(saleId)).IsSuccess).IsTrue();

        await using (var context = store.Create())
        {
            var installment = await context.Installments.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == installmentId);
            await Assert.That(installment.DeletedAt).IsEqualTo(before);
        }
    }

    // ---------- Atualização só da raiz ----------

    [Test]
    public async Task Update_saves_the_root_and_owned_without_touching_or_reviving_children()
    {
        var store = new InMemoryStore();
        var (saleId, _) = await CreateSaleAsync(store);
        int deletedInstallment, itemId;
        await using (var context = store.Create())
        {
            deletedInstallment = (await context.Installments.FirstAsync(p => p.SaleId == saleId)).Id;
            itemId = (await context.Items.FirstAsync()).Id;
            await Assert.That((await TestOrm.Orm<Installment, int>(context).DeleteAsync(deletedInstallment)).IsSuccess).IsTrue();
        }

        List<Label> labels;
        await using (var context = store.Create())
            labels = (await context.Sales.AsNoTracking().SingleAsync(v => v.Id == saleId)).Labels;

        await using (var context = store.Create())
        {
            labels[0].Value = "e1-alterada";
            var detached = new Sale
            {
                Id = saleId,
                Name = "V1 alterada",
                Version = 1,
                Address = new ShippingAddress { Street = "Street B" },
                Labels = [labels[0], labels[1], new Label { Value = "e3" }],
                // Navegações para outras entidades: ignoradas
                Installments = [new Installment { Id = deletedInstallment, SaleId = saleId, Amount = 999 }],
                Items = [new SaleItem { Id = itemId, SaleId = saleId, Description = "não gravar" }]
            };
            var result = await Sales(context).UpdateAsync(detached);
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(context.ChangeTracker.Entries<Installment>().Any()).IsFalse();
            await Assert.That(context.ChangeTracker.Entries<SaleItem>().Any()).IsFalse();
        }

        await using (var context = store.Create())
        {
            var sale = await context.Sales.AsNoTracking().SingleAsync(v => v.Id == saleId);
            var installment = await context.Installments.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == deletedInstallment);
            var item = await context.Items.AsNoTracking().SingleAsync(i => i.Id == itemId);

            await Assert.That(sale.Name).IsEqualTo("V1 alterada");
            await Assert.That(sale.Address?.Street).IsEqualTo("Street B");
            await Assert.That(sale.Labels.Select(e => e.Value).Order()).IsEquivalentTo(["e1-alterada", "e2", "e3"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(installment.IsDeleted).IsTrue();
            await Assert.That(installment.Amount).IsEqualTo(10m);
            await Assert.That(item.Description).IsEqualTo("i1");
        }
    }

    [Test]
    public async Task Update_with_another_tracked_instance_swaps_the_instance_and_updates_owned()
    {
        var store = new InMemoryStore();
        var (saleId, _) = await CreateSaleAsync(store);

        await using (var context = store.Create())
        {
            var tracked = await context.Sales.SingleAsync(v => v.Id == saleId);
            var received = new Sale
            {
                Id = saleId, Name = "Nova", Version = 1, Address = new ShippingAddress { Street = "Street C" },
                Labels = [.. tracked.Labels.Select(e => new Label { Id = e.Id, Value = e.Value })]
            };
            var result = await Sales(context).UpdateAsync(received);
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsSameReferenceAs(received);
            await Assert.That(context.Entry(tracked).State).IsEqualTo(EntityState.Detached);
        }

        await using (var context = store.Create())
        {
            var sale = await context.Sales.AsNoTracking().SingleAsync(v => v.Id == saleId);
            await Assert.That(sale.Name).IsEqualTo("Nova");
            await Assert.That(sale.Address?.Street).IsEqualTo("Street C");
            await Assert.That(sale.Labels.Count).IsEqualTo(2);
        }
    }

    // ---------- Concorrência otimista ----------

    private static async Task<int> CreateWithVersion2Async(InMemoryStore store)
    {
        var (saleId, _) = await CreateSaleAsync(store);
        await using var context = store.Create();
        var sale = await context.Sales.SingleAsync(v => v.Id == saleId);
        sale.Version = 2;   // outro usuário gravou antes
        await context.SaveChangesAsync();
        return saleId;
    }

    [Test]
    public async Task Stale_detached_version_is_concurrency()
    {
        var store = new InMemoryStore();
        int saleId = await CreateWithVersion2Async(store);

        await using var context = store.Create();
        var result = await Sales(context).UpdateAsync(new Sale { Id = saleId, Name = "stale", Version = 1 });

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConcurrencyCode);
        await Assert.That(context.ChangeTracker.Entries().Any()).IsFalse();   // desanexada após a falha, com os owned
    }

    [Test]
    public async Task Stale_version_with_already_tracked_entity_is_concurrency()
    {
        var store = new InMemoryStore();
        int saleId = await CreateWithVersion2Async(store);

        await using (var context = store.Create())
        {
            _ = await context.Sales.SingleAsync(v => v.Id == saleId);   // rastreada com Versao = 2
            var result = await Sales(context).UpdateAsync(new Sale { Id = saleId, Name = "stale", Version = 1 });
            await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConcurrencyCode);
        }

        await using (var context = store.Create())
        {
            var sale = await context.Sales.AsNoTracking().SingleAsync(v => v.Id == saleId);
            await Assert.That(sale.Name).IsEqualTo("V1");
            await Assert.That(sale.Version).IsEqualTo(2);
        }
    }

    [Test]
    public async Task Current_version_with_already_tracked_entity_saves()
    {
        var store = new InMemoryStore();
        int saleId = await CreateWithVersion2Async(store);

        await using var context = store.Create();
        _ = await context.Sales.SingleAsync(v => v.Id == saleId);
        var result = await Sales(context).UpdateAsync(new Sale { Id = saleId, Name = "ok", Version = 2 });

        await Assert.That(result.IsSuccess).IsTrue();
    }

    // ---------- Ordenação por texto ----------

    [Test]
    [Arguments("PasswordHash")]
    [Arguments("IsDeleted")]
    [Arguments("Id")]
    public async Task With_Sortable_only_marked_properties_are_accepted(string sortBy)
    {
        await using var context = new InMemoryStore().Create();
        var appUsers = TestOrm.Orm<AppUser, int>(context);
        await appUsers.CreateAsync(new AppUser { Name = "Ana", PasswordHash = "h" });

        var rejected = await appUsers.ListAsync(new PageRequest(1, 10, sortBy));
        var accepted = await appUsers.ListAsync(new PageRequest(1, 10, "name"));

        await Assert.That(rejected.Error!.Code).IsEqualTo(OrmErrors.InvalidInputCode);
        await Assert.That(rejected.Error.Field).IsEqualTo("sortBy");
        await Assert.That(accepted.IsSuccess).IsTrue();
    }

    [Test]
    [Arguments("IsDeleted")]
    [Arguments("deletedat")]
    [Arguments("Origem")]   // sombra
    [Arguments("Version")]   // token de concorrência
    public async Task Without_Sortable_shadow_soft_delete_and_token_are_always_rejected(string sortBy)
    {
        var store = new InMemoryStore();
        await CreateSaleAsync(store);
        await using var context = store.Create();

        var rejected = await Sales(context).ListAsync(new PageRequest(1, 10, sortBy));
        var accepted = await Sales(context).ListAsync(new PageRequest(1, 10, "PasswordHash"));   // sem [Sortable]: como antes

        await Assert.That(rejected.Error!.Code).IsEqualTo(OrmErrors.InvalidInputCode);
        await Assert.That(rejected.Error.Field).IsEqualTo("sortBy");
        await Assert.That(accepted.IsSuccess).IsTrue();
    }
}
