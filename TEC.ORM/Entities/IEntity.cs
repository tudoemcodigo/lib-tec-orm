namespace TEC.ORM.Entities;

/// <summary>Entidade persistida com identificador do tipo <typeparamref name="TKey"/> (<c>int</c>, <c>long</c>, <c>Guid</c>, <c>string</c>...).</summary>
/// <typeparam name="TKey">Tipo da chave primária.</typeparam>
public interface IEntity<TKey> where TKey : notnull, IEquatable<TKey>
{
    /// <summary>Identificador (chave primária).</summary>
    TKey Id { get; }
}

/// <summary>
/// Exclusão lógica: toda entidade do TEC.ORM implementa esta interface. Excluir marca <see cref="IsDeleted"/> e
/// <see cref="DeletedAt"/> em vez de remover a linha, e as consultas ignoram as linhas excluídas automaticamente.
/// </summary>
/// <remarks>
/// Os dois campos são preenchidos pela implementação (no EF Core, por um interceptor ao salvar); a aplicação não os altera.
/// Por isso só há <c>get</c> aqui: a implementação grava pelos metadados do ORM (setter privado ou campo).
/// </remarks>
public interface ISoftDelete
{
    /// <summary>Indica se a entidade foi excluída logicamente.</summary>
    bool IsDeleted { get; }

    /// <summary>Quando a entidade foi excluída (UTC); <c>null</c> se não foi.</summary>
    DateTimeOffset? DeletedAt { get; }
}
