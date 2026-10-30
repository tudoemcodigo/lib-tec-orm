using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TEC.ORM.EntityFrameworkCore.Internal;

namespace TEC.ORM.EntityFrameworkCore.Auditing;

/// <summary>
/// Fluent API equivalente aos atributos <c>[Auditable]</c>, <c>[AuditIgnore]</c>, <c>[SensitiveData]</c> e <c>[Sortable]</c>.
/// Use no <c>IEntityTypeConfiguration</c> da infraestrutura para manter as entidades de domínio sem referência ao TEC.ORM
/// (Clean Architecture). Atributo e Fluent API se somam: basta um dos dois.
/// </summary>
/// <example>
/// <code>
/// builder.IsAuditable();
/// builder.Property(c =&gt; c.Documento).IsSensitiveData();
/// builder.Property(c =&gt; c.Nome).IsSortable();
/// </code>
/// </example>
public static class OrmMetadataBuilderExtensions
{
    /// <summary>Liga a trilha histórica (diffs) da entidade, como <c>[Auditable]</c> na classe.</summary>
    /// <typeparam name="TEntity">Tipo da entidade.</typeparam>
    /// <param name="builder">Configuração da entidade.</param>
    /// <returns>O mesmo <paramref name="builder"/>.</returns>
    public static EntityTypeBuilder<TEntity> IsAuditable<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(OrmAnnotationNames.Auditable, true);
        return builder;
    }

    /// <summary>Liga a trilha histórica de um tipo <i>owned</i>, como <c>[Auditable]</c> na classe dele.</summary>
    /// <typeparam name="TOwner">Tipo dono.</typeparam>
    /// <typeparam name="TDependent">Tipo <i>owned</i>.</typeparam>
    /// <param name="builder">Configuração do tipo <i>owned</i>.</param>
    /// <returns>O mesmo <paramref name="builder"/>.</returns>
    public static OwnedNavigationBuilder<TOwner, TDependent> IsAuditable<TOwner, TDependent>(
        this OwnedNavigationBuilder<TOwner, TDependent> builder)
        where TOwner : class
        where TDependent : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(OrmAnnotationNames.Auditable, true);
        return builder;
    }

    /// <summary>Mascara o valor da propriedade (<c>***</c>) na trilha histórica, como <c>[SensitiveData]</c>.</summary>
    /// <typeparam name="TProperty">Tipo da propriedade.</typeparam>
    /// <param name="builder">Configuração da propriedade.</param>
    /// <returns>O mesmo <paramref name="builder"/>.</returns>
    public static PropertyBuilder<TProperty> IsSensitiveData<TProperty>(this PropertyBuilder<TProperty> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(OrmAnnotationNames.SensitiveData, true);
        return builder;
    }

    /// <summary>Deixa a propriedade fora da trilha histórica, como <c>[AuditIgnore]</c>.</summary>
    /// <typeparam name="TProperty">Tipo da propriedade.</typeparam>
    /// <param name="builder">Configuração da propriedade.</param>
    /// <returns>O mesmo <paramref name="builder"/>.</returns>
    public static PropertyBuilder<TProperty> IsAuditIgnored<TProperty>(this PropertyBuilder<TProperty> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(OrmAnnotationNames.AuditIgnore, true);
        return builder;
    }

    /// <summary>
    /// Aceita a propriedade no <c>SortBy</c> da paginação, como <c>[Sortable]</c>. Com ao menos uma marcada, só as marcadas são
    /// aceitas (whitelist).
    /// </summary>
    /// <typeparam name="TProperty">Tipo da propriedade.</typeparam>
    /// <param name="builder">Configuração da propriedade.</param>
    /// <returns>O mesmo <paramref name="builder"/>.</returns>
    public static PropertyBuilder<TProperty> IsSortable<TProperty>(this PropertyBuilder<TProperty> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HasAnnotation(OrmAnnotationNames.Sortable, true);
        return builder;
    }
}
