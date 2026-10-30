namespace TEC.ORM.EntityFrameworkCore.Internal;

/// <summary>
/// Anotações do modelo EF Core gravadas pela Fluent API do TEC.ORM (<see cref="Auditing.OrmMetadataBuilderExtensions"/>).
/// Equivalem aos atributos <c>[Auditable]</c>, <c>[AuditIgnore]</c>, <c>[SensitiveData]</c> e <c>[Sortable]</c>, sem exigir que a
/// entidade (camada de domínio) referencie o TEC.ORM.
/// </summary>
internal static class OrmAnnotationNames
{
    /// <summary>Entidade com trilha histórica (equivale a <c>[Auditable]</c>).</summary>
    public const string Auditable = "TEC.ORM:Auditable";

    /// <summary>Propriedade fora da trilha histórica (equivale a <c>[AuditIgnore]</c>).</summary>
    public const string AuditIgnore = "TEC.ORM:AuditIgnore";

    /// <summary>Propriedade mascarada na trilha histórica (equivale a <c>[SensitiveData]</c>).</summary>
    public const string SensitiveData = "TEC.ORM:SensitiveData";

    /// <summary>Propriedade aceita no <c>SortBy</c> da paginação (equivale a <c>[Sortable]</c>).</summary>
    public const string Sortable = "TEC.ORM:Sortable";
}
