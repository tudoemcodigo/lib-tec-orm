using TEC.Core.Text.Masking;
using TEC.ORM.Entities;
using TEC.ORM.Mapping;
using TEC.ORM.Tests.Database.CodeFirst;

namespace TEC.ORM.Tests.Mapping;

// ---------- DTOs sobre o modelo code first do banco de testes (EF Core) ----------

[MapFrom(typeof(Category))]
public partial class CategoryDto
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";
}

[MapFrom(typeof(Product))]
public partial class ProductDto
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public decimal Price { get; set; }

    public CategoryDto? Category { get; set; }                 // objeto aninhado: automático
}

[MapFrom(typeof(Customer))]
public partial class CustomerDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string Email { get; set; } = "";

    public bool Active { get; set; }

    [MapList(Sync = true)]
    public List<OrderDto> Orders { get; set; } = [];         // coleção aninhada, sincronizada no ApplyTo

    [MapIgnore]
    public string? Remark { get; set; }
}

[MapFrom(typeof(Order))]
public partial class OrderDto
{
    public long Id { get; set; }

    public Guid CustomerId { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public decimal Amount { get; set; }

    [MapReadOnly]
    public DateTimeOffset CreatedOn { get; set; }

    [MapFrom("Customer.Name")]
    public string? CustomerName { get; set; }                    // achatamento

    [MapFrom("Product.Category.Name")]
    public string? CategoryName { get; set; }                  // achatamento em dois níveis, seguro contra nulos

    public ProductDto? Product { get; set; }

    [MapObject(MaxDepth = 1)]
    public CustomerDto? Customer { get; set; }                    // ciclo Cliente → Pedidos → Cliente, limitado
}

/// <summary>DTO enxuto: prova que a projeção só lê as colunas usadas.</summary>
[MapFrom(typeof(Order))]
public partial class OrderSummaryDto
{
    public long Id { get; set; }

    public decimal Amount { get; set; }

    [MapFrom("Customer.Name")]
    public string? CustomerName { get; set; }
}

// ---------- Modelo em memória para conversões (sem EF) ----------

public enum AccountStatus
{
    Active = 1,
    Blocked = 2
}

public sealed class Address
{
    public string City { get; set; } = "";

    public string StateCode { get; set; } = "";
}

public sealed class Contact : Entity<int>
{
    public string Kind { get; set; } = "";

    public string Value { get; set; } = "";
}

public sealed class Account : Entity<int>
{
    public AccountStatus Status { get; set; }

    public string Situation { get; set; } = "";

    public int? Points { get; set; }

    public int Level { get; set; }

    public string Cpf { get; set; } = "";

    public long BalanceInCents { get; set; }

    public List<string> Tags { get; set; } = [];

    public Address? Address { get; set; }

    public List<Contact> Contacts { get; set; } = [];
}

[MapFrom(typeof(Address))]
public partial class AddressDto
{
    public string City { get; set; } = "";

    public string StateCode { get; set; } = "";
}

[MapFrom(typeof(Contact))]
public partial record ContactDto
{
    public int Id { get; init; }

    public string Kind { get; init; } = "";

    public string Value { get; init; } = "";
}

[MapFrom(typeof(Account))]
public partial class AccountDto
{
    public int Id { get; set; }

    public string Status { get; set; } = "";                     // enum → string

    public AccountStatus Situation { get; set; }                   // string → enum

    public int Points { get; set; }                             // int? → int (nulo vira 0)

    public long Level { get; set; }                             // int → long

    [MapConverter(typeof(MaskedCpfConverter))]
    public string Cpf { get; set; } = "";                       // conversor só de ida: não volta para a entidade

    [MapFrom(nameof(Account.BalanceInCents))]
    [MapConverter(typeof(CentsConverter))]
    public decimal Balance { get; set; }                          // conversor de ida e volta

    public string[] Tags { get; set; } = [];                    // coleção simples → array

    [MapFrom("Address.City")]
    public string? City { get; set; }

    [MapObject(Sync = true)]
    public AddressDto? Address { get; set; }

    [MapList(Sync = true)]
    public IReadOnlyList<ContactDto> Contacts { get; set; } = [];
}

public sealed class MaskedCpfConverter : IMapConverter<string, string>
{
    public string Convert(string value) => SensitiveDataMasker.MaskCpf(value);
}

public sealed class CentsConverter : IBidirectionalMapConverter<long, decimal>
{
    public decimal Convert(long value) => value / 100m;

    public long ConvertBack(decimal value) => (long)Math.Round(value * 100m, MidpointRounding.AwayFromZero);
}
