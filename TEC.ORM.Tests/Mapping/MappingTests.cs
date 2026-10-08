using Microsoft.EntityFrameworkCore;
using TEC.ORM.Common;
using TEC.ORM.Mapping;
using TEC.ORM.Paging;
using TEC.ORM.Specifications;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests.Mapping;

/// <summary>Comportamento do mapeamento gerado: entidade → DTO, DTO → entidade, aninhados, ciclos, conversões e projeção.</summary>
public class MappingTests
{
    private static Account NewAccount() => new()
    {
        Id = 7,
        Status = AccountStatus.Blocked,
        Situation = "active",
        Points = null,
        Level = 3,
        Cpf = "12345678909",
        BalanceInCents = 12_345,
        Tags = ["vip", "pj"],
        Address = new Address { City = "Recife", StateCode = "PE" },
        Contacts =
        [
            new Contact { Id = 1, Kind = "email", Value = "a@exemplo.com" },
            new Contact { Id = 2, Kind = "fone", Value = "8199999" }
        ]
    };

    private static Order NewOrder()
    {
        var category = new Category { Id = "LIV", Name = "Livros" };
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Ana", Email = "ana@exemplo.com" };
        var order = new Order
        {
            Id = 10, Customer = customer, CustomerId = customer.Id, Quantity = 2, Amount = 50m, CreatedOn = DateTimeOffset.UtcNow,
            Product = new Product { Id = 3, Name = "Livro", Price = 25m, Category = category, CategoryId = "LIV" }
        };
        customer.Orders.Add(order);
        return order;
    }

    // ---------- Entidade → DTO ----------

    [Test]
    public async Task Type_conversions_and_converter()
    {
        var dto = NewAccount().ToAccountDto();

        await Assert.That(dto.Id).IsEqualTo(7);
        await Assert.That(dto.Status).IsEqualTo("Blocked");                 // enum → string
        await Assert.That(dto.Situation).IsEqualTo(AccountStatus.Active);         // string → enum (sem diferenciar maiúsculas)
        await Assert.That(dto.Points).IsEqualTo(0);                           // int? nulo → 0
        await Assert.That(dto.Level).IsEqualTo(3L);                           // int → long
        await Assert.That(dto.Cpf).IsEqualTo(TEC.Core.Text.Masking.SensitiveDataMasker.MaskCpf("12345678909"));   // conversor
        await Assert.That(dto.Cpf).DoesNotContain("12345678909");
        await Assert.That(dto.Balance).IsEqualTo(123.45m);                      // conversor de ida e volta
        await Assert.That(dto.Tags).IsEquivalentTo(["vip", "pj"]);            // List<string> → string[]
    }

    [Test]
    public async Task Nested_objects_and_collections()
    {
        var dto = NewAccount().ToAccountDto();

        await Assert.That(dto.City).IsEqualTo("Recife");                    // achatamento
        await Assert.That(dto.Address!.StateCode).IsEqualTo("PE");
        await Assert.That(dto.Contacts.Count).IsEqualTo(2);
        await Assert.That(dto.Contacts[1]).IsEqualTo(new ContactDto { Id = 2, Kind = "fone", Value = "8199999" });   // record com init
    }

    [Test]
    public async Task Nulls_in_the_path_become_default_without_exception()
    {
        var account = NewAccount();
        account.Address = null;
        account.Contacts = null!;
        account.Tags = null!;

        var dto = account.ToAccountDto();

        await Assert.That(dto.City).IsNull();
        await Assert.That(dto.Address).IsNull();
        await Assert.That(dto.Contacts.Count).IsEqualTo(0);
        await Assert.That(dto.Tags.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Flattening_two_levels_and_cycle_limited_by_MaxDepth()
    {
        var dto = NewOrder().ToOrderDto();

        await Assert.That(dto.CustomerName).IsEqualTo("Ana");
        await Assert.That(dto.CategoryName).IsEqualTo("Livros");             // Produto.Categoria.Nome
        await Assert.That(dto.Product!.Category!.Name).IsEqualTo("Livros");
        // Pedido → Cliente → Pedidos → (Cliente cortado: MaxDepth = 1)
        await Assert.That(dto.Customer!.Orders.Single().Id).IsEqualTo(10L);
        await Assert.That(dto.Customer.Orders.Single().Customer).IsNull();

        var withoutProduct = NewOrder();
        withoutProduct.Product = null;
        await Assert.That(withoutProduct.ToOrderDto().CategoryName).IsNull();
    }

    [Test]
    public async Task Compiled_projection_matches_in_memory_conversion()
    {
        var account = NewAccount();
        var projected = AccountDto.Projection.Compile()(account);
        var inMemory = AccountDto.FromEntity(account);

        await Assert.That(projected.Cpf).IsEqualTo(inMemory.Cpf);
        await Assert.That(projected.Balance).IsEqualTo(inMemory.Balance);
        await Assert.That(projected.Contacts.Count).IsEqualTo(inMemory.Contacts.Count);
        await Assert.That(projected.Address!.City).IsEqualTo(inMemory.Address!.City);
    }

    [Test]
    public async Task Generic_and_generated_extensions()
    {
        var accounts = new[] { NewAccount(), NewAccount() };

        await Assert.That(accounts.ToAccountDtoList().Count).IsEqualTo(2);
        await Assert.That(accounts.ToDtoList<Account, AccountDto>().Count).IsEqualTo(2);
        await Assert.That(accounts.AsQueryable().ProjectToAccountDto().Count()).IsEqualTo(2);
        await Assert.That(accounts.AsQueryable().ProjectToDto<Account, AccountDto>().First().City).IsEqualTo("Recife");

        var page = new TEC.Core.Responses.Pagination.PagedResult<Account>(accounts, 1, 10, 2).ToDtoPage<Account, AccountDto>();
        await Assert.That(page.TotalItems).IsEqualTo(2L);
        await Assert.That(page.Items[0].Status).IsEqualTo("Blocked");
    }

    // ---------- DTO → entidade ----------

    [Test]
    public async Task ApplyTo_never_changes_id_soft_delete_flattened_or_read_only()
    {
        var order = NewOrder();
        var createdOn = order.CreatedOn;
        var dto = new OrderDto
        {
            Id = 999, Quantity = 5, Amount = 80m, CreatedOn = DateTimeOffset.MinValue, CustomerName = "Outro", CategoryName = "Outra",
            CustomerId = order.CustomerId, ProductId = 3, Customer = new CustomerDto { Name = "Não deve gravar" }
        };

        dto.ApplyTo(order);

        await Assert.That(order.Id).IsEqualTo(10L);                          // Id protegido (overposting)
        await Assert.That(order.Quantity).IsEqualTo(5);
        await Assert.That(order.Amount).IsEqualTo(80m);
        await Assert.That(order.CreatedOn).IsEqualTo(createdOn);               // [MapReadOnly]
        await Assert.That(order.Customer!.Name).IsEqualTo("Ana");            // achatado e objeto sem Sync: não voltam
        await Assert.That(order.IsDeleted).IsFalse();
    }

    [Test]
    public async Task ApplyTo_converts_back()
    {
        var account = NewAccount();
        var dto = account.ToAccountDto();
        dto.Situation = AccountStatus.Blocked;
        dto.Status = "Active";
        dto.Balance = 10.5m;
        dto.Cpf = "00000000000";
        dto.Level = 9;

        dto.ApplyTo(account);

        await Assert.That(account.Situation).IsEqualTo("Blocked");             // enum → string
        await Assert.That(account.Status).IsEqualTo(AccountStatus.Active);         // string → enum
        await Assert.That(account.BalanceInCents).IsEqualTo(1050L);             // ConvertBack
        await Assert.That(account.Cpf).IsEqualTo("12345678909");                // conversor só de ida: não volta
        await Assert.That(account.Level).IsEqualTo(3);                          // long → int tem perda: não volta
    }

    [Test]
    public async Task ApplyTo_invalid_enum_value_and_null_preserve_the_entity()
    {
        var account = NewAccount();
        account.Points = 40;
        var dto = account.ToAccountDto();
        dto.Status = "999";                                                   // número fora do enum
        dto.Points = 0;

        dto.ApplyTo(account);
        await Assert.That(account.Status).IsEqualTo(AccountStatus.Blocked);
        await Assert.That(account.Points).IsEqualTo(0);                         // int → int? volta

        dto.Status = "Inexistente";
        dto.ApplyTo(account);
        await Assert.That(account.Status).IsEqualTo(AccountStatus.Blocked);
    }

    [Test]
    public async Task ApplyTo_syncs_object_and_list_by_id()
    {
        var account = NewAccount();
        var email = account.Contacts[0];
        var dto = account.ToAccountDto();
        dto.Address!.City = "Olinda";
        dto.Contacts =
        [
            dto.Contacts[0] with { Value = "novo@exemplo.com" },             // Id 1: atualiza a mesma instância
            new ContactDto { Id = 0, Kind = "whatsapp", Value = "8188888" },  // sem Id: cria
            new ContactDto { Id = 555, Kind = "fax", Value = "x" }            // Id desconhecido: cria (o Id não vem do DTO)
        ];                                                                    // Id 2 ausente: remove

        dto.ApplyTo(account);

        await Assert.That(account.Address!.City).IsEqualTo("Olinda");
        await Assert.That(account.Contacts.Count).IsEqualTo(3);
        await Assert.That(ReferenceEquals(account.Contacts[0], email)).IsTrue();
        await Assert.That(email.Value).IsEqualTo("novo@exemplo.com");
        await Assert.That(account.Contacts.Any(c => c.Id == 2)).IsFalse();
        await Assert.That(account.Contacts.Count(c => c.Id == 0)).IsEqualTo(2);
    }

    [Test]
    public async Task ApplyTo_creates_nested_object_when_the_entity_has_none()
    {
        var account = NewAccount();
        account.Address = null;
        var dto = new AccountDto { Address = new AddressDto { City = "Natal", StateCode = "RN" }, Contacts = [] };

        dto.ApplyTo(account);

        await Assert.That(account.Address!.City).IsEqualTo("Natal");
    }

    [Test]
    public async Task ToEntity_creates_the_entity_without_id_and_with_children()
    {
        var dto = new CustomerDto
        {
            Id = Guid.NewGuid(), Name = "Bia", Email = "bia@exemplo.com", Active = true,
            Orders = [new OrderDto { Id = 5, Quantity = 1, Amount = 9m, ProductId = 1 }]
        };

        var customer = dto.ToEntity();

        await Assert.That(customer.Id).IsEqualTo(Guid.Empty);                  // gerado pelo ORM, nunca pelo DTO
        await Assert.That(customer.Name).IsEqualTo("Bia");
        await Assert.That(customer.Orders.Single().Amount).IsEqualTo(9m);
        await Assert.That(customer.Orders.Single().Id).IsEqualTo(0L);
        await Assert.That(dto is IDtoEntityFactory<Customer>).IsTrue();
    }

    // ---------- Projeção no ORM ----------

    [Test]
    public async Task Projection_reads_only_the_dto_columns_from_the_database()
    {
        await using var context = new CodeFirstDesignTimeFactory().CreateDbContext([]);   // provedor SQL Server, sem conexão

        string sql = context.Orders.ProjectToOrderSummaryDto().ToQueryString();

        await Assert.That(sql).Contains("[Amount]");
        await Assert.That(sql).Contains("[Name]");                            // Cliente.Nome pelo JOIN
        await Assert.That(sql).DoesNotContain("[Quantity]");
        await Assert.That(sql).DoesNotContain("[Email]");
        await Assert.That(sql).Contains("[IsDeleted] = CAST(0 AS bit)");      // filtro de exclusão lógica mantido
    }

    [Test]
    public async Task Repository_gets_lists_and_finds_as_dto()
    {
        var context = TestOrm.InMemoryContext();
        var orderEntity = NewOrder();
        context.Add(orderEntity);
        await context.SaveChangesAsync();
        var orders = TestOrm.Orm<Order, long>(context);
        var customers = TestOrm.Orm<Customer, Guid>(context);

        var read = await orders.GetByIdAsync<OrderDto>(orderEntity.Id);
        var list = await customers.ListAsync<CustomerDto>(new PageRequest(1, 10, SortBy: "name"));
        var search = await orders.FindAsync<OrderSummaryDto>(new Specification<Order>(p => p.Amount > 10));
        var missing = await orders.GetByIdAsync<OrderDto>(404);

        await Assert.That(read.Value.Product!.Category!.Name).IsEqualTo("Livros");
        await Assert.That(read.Value.Customer!.Orders.Single().Id).IsEqualTo(orderEntity.Id);
        await Assert.That(list.Value.TotalItems).IsEqualTo(1L);
        await Assert.That(list.Value.Items[0].Orders.Count).IsEqualTo(1);
        await Assert.That(search.Value.Single().CustomerName).IsEqualTo("Ana");
        await Assert.That(missing.Error!.Code).IsEqualTo(OrmErrors.NotFoundCode);
    }

    [Test]
    public async Task Repository_dto_respects_soft_delete()
    {
        var context = TestOrm.InMemoryContext();
        var order = NewOrder();
        context.Add(order);
        await context.SaveChangesAsync();
        var orders = TestOrm.Orm<Order, long>(context);

        await orders.DeleteAsync(order.Id);

        await Assert.That((await orders.GetByIdAsync<OrderDto>(order.Id)).Error!.Code).IsEqualTo(OrmErrors.NotFoundCode);
        await Assert.That((await orders.ListAsync<OrderSummaryDto>(new PageRequest())).Value.TotalItems).IsEqualTo(0L);
    }
}
