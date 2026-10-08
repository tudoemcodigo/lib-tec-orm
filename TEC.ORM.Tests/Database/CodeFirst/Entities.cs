using TEC.ORM.Entities;

namespace TEC.ORM.Tests.Database.CodeFirst;

// Modelo de testes do banco de testes (schema "codefirst"). Um tipo de chave por entidade, para cobrir o componente genérico:
// string (Categoria), int (Produto), Guid (Cliente) e long (Pedido).

public sealed class Category : Entity<string>
{
    public string Name { get; set; } = string.Empty;
}

public sealed class Product : Entity<int>
{
    public string Name { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public string CategoryId { get; set; } = string.Empty;

    public Category? Category { get; set; }
}

public sealed class Customer : Entity<Guid>
{
    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public bool Active { get; set; } = true;

    public List<Order> Orders { get; set; } = [];
}

public sealed class Order : Entity<long>
{
    public Guid CustomerId { get; set; }

    public Customer? Customer { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    public int Quantity { get; set; }

    public decimal Amount { get; set; }

    public DateTimeOffset CreatedOn { get; set; }
}
