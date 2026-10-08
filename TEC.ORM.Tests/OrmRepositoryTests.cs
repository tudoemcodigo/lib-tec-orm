using Microsoft.EntityFrameworkCore;
using TEC.ORM.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.Paging;
using TEC.ORM.Specifications;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests;

/// <summary>CRUD do <c>OrmRepository</c> (EF Core InMemory): todas as operações, exclusão lógica e chaves genéricas.</summary>
public class OrmRepositoryTests
{
    private static async Task<(CodeFirstContext Context, IOrmRepository<Customer, Guid> Customers)> WithCustomersAsync(params string[] names)
    {
        var context = TestOrm.InMemoryContext();
        var customers = TestOrm.Orm<Customer, Guid>(context);
        foreach (string name in names)
            await customers.CreateAsync(new Customer { Name = name, Email = $"{name.ToLowerInvariant()}@exemplo.com" });
        return (context, customers);
    }

    // ---------- Criar / Ler ----------

    [Test]
    public async Task Create_generates_the_identifier_and_get_returns_the_record()
    {
        var (_, customers) = await WithCustomersAsync();

        var created = await customers.CreateAsync(new Customer { Name = "Ana", Email = "ana@exemplo.com" });
        var read = await customers.GetByIdAsync(created.Value.Id);

        await Assert.That(created.Value.Id).IsNotEqualTo(Guid.Empty);
        await Assert.That(read.Value.Name).IsEqualTo("Ana");
        await Assert.That(read.Value.IsDeleted).IsFalse();
    }

    [Test]
    public async Task Create_null_entity_is_invalid_input()
    {
        var (_, customers) = await WithCustomersAsync();
        var result = await customers.CreateAsync(null!);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.InvalidInputCode);
        await Assert.That(result.Error.Field).IsEqualTo("entity");
    }

    [Test]
    public async Task Get_missing_is_not_found()
    {
        var (_, customers) = await WithCustomersAsync();
        var result = await customers.GetByIdAsync(Guid.NewGuid());

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.NotFoundCode);
    }

    [Test]
    public async Task Generic_keys_string_int_and_long()
    {
        var context = TestOrm.InMemoryContext();
        var categories = TestOrm.Orm<Category, string>(context);
        var products = TestOrm.Orm<Product, int>(context);
        var customers = TestOrm.Orm<Customer, Guid>(context);
        var orders = TestOrm.Orm<Order, long>(context);

        var category = await categories.CreateAsync(new Category { Id = "ELETRO", Name = "Eletrônicos" });
        var product = await products.CreateAsync(new Product { Name = "Fone", Price = 199.90m, CategoryId = "ELETRO" });
        var customer = await customers.CreateAsync(new Customer { Name = "Caio", Email = "caio@exemplo.com" });
        var order = await orders.CreateAsync(new Order
        {
            CustomerId = customer.Value.Id, ProductId = product.Value.Id, Quantity = 2, Amount = 399.80m, CreatedOn = DateTimeOffset.UtcNow
        });

        await Assert.That((await categories.GetByIdAsync("ELETRO")).Value.Name).IsEqualTo("Eletrônicos");
        await Assert.That(product.Value.Id).IsGreaterThan(0);
        await Assert.That(order.Value.Id).IsGreaterThan(0L);
        await Assert.That((await orders.GetByIdAsync(order.Value.Id)).Value.Amount).IsEqualTo(399.80m);
        await Assert.That(category.IsSuccess).IsTrue();
    }

    // ---------- Atualizar ----------

    [Test]
    public async Task Update_with_detached_object_saves_the_changes()
    {
        var (context, customers) = await WithCustomersAsync("Ana");
        var id = (await customers.FindAsync(c => c.Name == "Ana")).Value[0].Id;
        context.ChangeTracker.Clear();

        var result = await customers.UpdateAsync(new Customer { Id = id, Name = "Ana Maria", Email = "ana@exemplo.com", Active = false });

        await Assert.That(result.IsSuccess).IsTrue();
        var read = (await customers.GetByIdAsync(id)).Value;
        await Assert.That(read.Name).IsEqualTo("Ana Maria");
        await Assert.That(read.Active).IsFalse();
    }

    [Test]
    public async Task Update_with_another_instance_when_already_tracked_copies_the_values()
    {
        var (_, customers) = await WithCustomersAsync();
        var created = (await customers.CreateAsync(new Customer { Name = "Bia", Email = "bia@exemplo.com" })).Value;   // rastreado

        var result = await customers.UpdateAsync(new Customer { Id = created.Id, Name = "Beatriz", Email = "bia@exemplo.com" });

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That((await customers.GetByIdAsync(created.Id)).Value.Name).IsEqualTo("Beatriz");
    }

    [Test]
    public async Task Update_missing_or_deleted_is_not_found()
    {
        var (_, customers) = await WithCustomersAsync("Caio");
        var caio = (await customers.FindAsync(c => c.Name == "Caio")).Value[0];
        await customers.DeleteAsync(caio.Id);

        var missing = await customers.UpdateAsync(new Customer { Id = Guid.NewGuid(), Name = "X", Email = "x@exemplo.com" });
        var deleted = await customers.UpdateAsync(new Customer { Id = caio.Id, Name = "Caio de volta", Email = "caio@exemplo.com" });

        await Assert.That(missing.Error!.Code).IsEqualTo(OrmErrors.NotFoundCode);
        await Assert.That(deleted.Error!.Code).IsEqualTo(OrmErrors.NotFoundCode);
    }

    // ---------- Excluir (lógico) ----------

    [Test]
    public async Task Delete_is_soft_and_the_row_stays_marked_in_the_database()
    {
        var moment = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var context = TestOrm.InMemoryContext(new FixedTimeProvider(moment));
        var customers = TestOrm.Orm<Customer, Guid>(context);
        var id = (await customers.CreateAsync(new Customer { Name = "Duda", Email = "duda@exemplo.com" })).Value.Id;

        var result = await customers.DeleteAsync(id);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That((await customers.GetByIdAsync(id)).Error!.Code).IsEqualTo(OrmErrors.NotFoundCode);
        await Assert.That((await customers.ExistsAsync(id)).Value).IsFalse();
        await Assert.That((await customers.CountAsync()).Value).IsEqualTo(0L);

        var row = await context.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == id);
        await Assert.That(row.IsDeleted).IsTrue();
        await Assert.That(row.DeletedAt).IsEqualTo(moment);
    }

    [Test]
    public async Task Deleting_twice_or_missing_is_not_found()
    {
        var (_, customers) = await WithCustomersAsync();
        var id = (await customers.CreateAsync(new Customer { Name = "Eva", Email = "eva@exemplo.com" })).Value.Id;
        await customers.DeleteAsync(id);

        await Assert.That((await customers.DeleteAsync(id)).Error!.Code).IsEqualTo(OrmErrors.NotFoundCode);
        await Assert.That((await customers.DeleteAsync(Guid.NewGuid())).Error!.Code).IsEqualTo(OrmErrors.NotFoundCode);
    }

    // ---------- Exclusão física (o DELETE em si só roda num provedor relacional: ver SqlServerIntegrationTests) ----------

#pragma warning disable TECORM014 // exclusão física intencional nos testes
    [Test]
    public async Task Batch_hard_delete_without_criteria_is_rejected()
    {
        var (_, customers) = await WithCustomersAsync("Gil");

        var withoutSpecification = await customers.HardDeleteAsync((ISpecification<Customer>)null!);
        var withoutCriteria = await customers.HardDeleteAsync(new Specification<Customer>());

        await Assert.That(withoutSpecification.Error!.Code).IsEqualTo(OrmErrors.InvalidInputCode);
        await Assert.That(withoutCriteria.Error!.Code).IsEqualTo(OrmErrors.InvalidInputCode);
        await Assert.That(withoutCriteria.Error.Field).IsEqualTo("specification");
        await Assert.That((await customers.CountAsync()).Value).IsEqualTo(1L);
    }

    [Test]
    public async Task Hard_delete_with_null_identifier_is_invalid_input()
    {
        var context = TestOrm.InMemoryContext();
        var categories = TestOrm.Orm<Category, string>(context);

        var result = await categories.HardDeleteAsync((string)null!);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.InvalidInputCode);
        await Assert.That(result.Error.Field).IsEqualTo("id");
    }
#pragma warning restore TECORM014

    [Test]
    public async Task Direct_remove_on_the_context_also_becomes_soft_delete()
    {
        var (context, customers) = await WithCustomersAsync("Fabi");
        var fabi = await context.Customers.SingleAsync();

        context.Customers.Remove(fabi);
        await context.SaveChangesAsync();

        await Assert.That(await context.Customers.IgnoreQueryFilters().CountAsync()).IsEqualTo(1);
        await Assert.That((await customers.CountAsync()).Value).IsEqualTo(0L);
    }

    // ---------- Buscar ----------

    [Test]
    public async Task Find_by_specification_with_ordering()
    {
        var (_, customers) = await WithCustomersAsync("Carla", "Ana", "Bruno", "Alice");

        var result = await customers.FindAsync(new Specification<Customer>(c => c.Name.StartsWith("A")).OrderByDescending(c => c.Name));

        await Assert.That(result.Value.Select(c => c.Name)).IsEquivalentTo(["Ana", "Alice"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Find_with_include_loads_the_navigation()
    {
        var context = TestOrm.InMemoryContext();
        var customer = (await TestOrm.Orm<Customer, Guid>(context).CreateAsync(new Customer { Name = "Gil", Email = "gil@exemplo.com" })).Value;
        await TestOrm.Orm<Category, string>(context).CreateAsync(new Category { Id = "LIVRO", Name = "Livros" });
        var product = (await TestOrm.Orm<Product, int>(context).CreateAsync(new Product { Name = "Livro", Price = 50, CategoryId = "LIVRO" })).Value;
        var orders = TestOrm.Orm<Order, long>(context);
        await orders.CreateAsync(new Order { CustomerId = customer.Id, ProductId = product.Id, Quantity = 1, Amount = 50 });
        context.ChangeTracker.Clear();

        var result = await orders.FindAsync(new Specification<Order>().Include(nameof(Order.Customer)));

        await Assert.That(result.Value[0].Customer!.Name).IsEqualTo("Gil");
    }

    [Test]
    public async Task Find_above_the_limit_asks_for_paged_listing()
    {
        var context = TestOrm.InMemoryContext();
        var customers = TestOrm.Orm<Customer, Guid>(context, TestOrm.Options(o => o.MaxFindResults = 2));
        foreach (string name in (string[])["A", "B", "C"])
            await customers.CreateAsync(new Customer { Name = name, Email = name + "@exemplo.com" });

        var result = await customers.FindAsync(c => true);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.TooManyResultsCode);
    }

    [Test]
    public async Task Find_without_specification_is_invalid_input()
    {
        var (_, customers) = await WithCustomersAsync();
        await Assert.That((await customers.FindAsync((ISpecification<Customer>)null!)).Error!.Code).IsEqualTo(OrmErrors.InvalidInputCode);
    }

    // ---------- Listar (paginação + filtro + ordenação) ----------

    [Test]
    public async Task List_page_with_total_and_text_sort()
    {
        var (_, customers) = await WithCustomersAsync("Elis", "Ana", "Dora", "Bia", "Caio");

        var page1 = await customers.ListAsync(new PageRequest(1, 2, SortBy: "name"));
        var page3 = await customers.ListAsync(new PageRequest(3, 2, SortBy: "NAME"));
        var desc = await customers.ListAsync(new PageRequest(1, 2, SortBy: "Name", Descending: true));

        await Assert.That(page1.Value.TotalItems).IsEqualTo(5L);
        await Assert.That(page1.Value.Items.Select(c => c.Name)).IsEquivalentTo(["Ana", "Bia"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(page3.Value.Items.Select(c => c.Name)).IsEquivalentTo(["Elis"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(desc.Value.Items[0].Name).IsEqualTo("Elis");
        await Assert.That(page1.Value.Pagination.TotalPages).IsEqualTo(3);
    }

    [Test]
    public async Task List_with_filter_counts_only_the_filtered()
    {
        var (_, customers) = await WithCustomersAsync("Ana", "Alice", "Bia");

        var result = await customers.ListAsync(new PageRequest(1, 10), new Specification<Customer>(c => c.Name.StartsWith("A")));

        await Assert.That(result.Value.TotalItems).IsEqualTo(2L);
        await Assert.That(result.Value.Items.Count).IsEqualTo(2);
    }

    [Test]
    public async Task List_beyond_the_last_page_returns_empty_with_total()
    {
        var (_, customers) = await WithCustomersAsync("Ana");
        var result = await customers.ListAsync(new PageRequest(5, 10));

        await Assert.That(result.Value.Items.Count).IsEqualTo(0);
        await Assert.That(result.Value.TotalItems).IsEqualTo(1L);
    }

    [Test]
    [Arguments("Name; DROP TABLE Customers")]
    [Arguments("NaoExiste")]
    [Arguments("Orders")]   // navegação não é propriedade escalar
    public async Task List_with_sort_outside_the_model_is_rejected(string sortBy)
    {
        var (_, customers) = await WithCustomersAsync("Ana");
        var result = await customers.ListAsync(new PageRequest(1, 10, sortBy));

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.InvalidInputCode);
        await Assert.That(result.Error.Field).IsEqualTo("sortBy");
    }

    [Test]
    public async Task List_with_page_above_the_limit_is_rejected()
    {
        var (_, customers) = await WithCustomersAsync();
        var result = await customers.ListAsync(new PageRequest(1, 1_000));

        await Assert.That(result.Error!.Field).IsEqualTo("pageSize");
    }

    // ---------- Existência / Contador ----------

    [Test]
    public async Task Existence_and_count_by_criteria()
    {
        var (_, customers) = await WithCustomersAsync("Ana", "Alice", "Bia");

        await Assert.That((await customers.ExistsAsync(c => c.Name == "Bia")).Value).IsTrue();
        await Assert.That((await customers.ExistsAsync(c => c.Name == "Zeca")).Value).IsFalse();
        await Assert.That((await customers.CountAsync()).Value).IsEqualTo(3L);
        await Assert.That((await customers.CountAsync(c => c.Name.StartsWith("A"))).Value).IsEqualTo(2L);
    }

    [Test]
    public async Task Operations_accept_cancellation()
    {
        var (_, customers) = await WithCustomersAsync("Ana");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await customers.CountAsync(cancellationToken: cts.Token)).Throws<OperationCanceledException>();
    }
}
