namespace TEC.ORM.Entities;

/// <summary>
/// Base opcional das entidades: identificador genérico e exclusão lógica. Entidades geradas por scaffolding (database first)
/// podem, em vez de herdar, implementar <see cref="IEntity{TKey}"/> e <see cref="ISoftDelete"/> numa classe <c>partial</c>.
/// </summary>
/// <typeparam name="TKey">Tipo da chave primária.</typeparam>
public abstract class Entity<TKey> : IEntity<TKey>, ISoftDelete where TKey : notnull, IEquatable<TKey>
{
    /// <inheritdoc />
    public TKey Id { get; set; } = default!;

    /// <inheritdoc />
    public bool IsDeleted { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset? DeletedAt { get; private set; }
}

/// <summary>Base opcional das entidades auditadas: <see cref="Entity{TKey}"/> + <see cref="IAuditable"/>.</summary>
/// <typeparam name="TKey">Tipo da chave primária.</typeparam>
public abstract class AuditableEntity<TKey> : Entity<TKey>, IAuditable where TKey : notnull, IEquatable<TKey>
{
    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; private set; }

    /// <inheritdoc />
    public string? CreatedBy { get; private set; }

    /// <inheritdoc />
    public string? CreatedByTenant { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <inheritdoc />
    public string? UpdatedBy { get; private set; }

    /// <inheritdoc />
    public string? UpdatedByTenant { get; private set; }

    /// <inheritdoc />
    public string? DeletedBy { get; private set; }

    /// <inheritdoc />
    public string? DeletedByTenant { get; private set; }
}
