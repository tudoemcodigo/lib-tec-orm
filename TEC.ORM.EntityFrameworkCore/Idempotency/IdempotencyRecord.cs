using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.EntityFrameworkCore.Idempotency;

/// <summary>Linha da tabela de idempotência (uma por escopo + chave).</summary>
internal sealed class IdempotencyRecord
{
    public const byte InProgress = 0;
    public const byte Completed = 1;

    public string Scope { get; set; } = "";

    public string Key { get; set; } = "";

    public string RequestHash { get; set; } = "";

    public Guid LockId { get; set; }

    public byte State { get; set; }

    public DateTimeOffset LockedUntil { get; set; }

    public int RetentionSeconds { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public int? StatusCode { get; set; }

    public string? ContentType { get; set; }

    public string? HeadersJson { get; set; }

    public byte[]? Body { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Mapeamento da tabela de idempotência no modelo do contexto da aplicação.</summary>
public static class IdempotencyModelBuilderExtensions
{
    /// <summary>Nome padrão da tabela.</summary>
    public const string DefaultTableName = "IdempotencyKeys";

    /// <summary>
    /// Collation binária do SQL Server das colunas <c>Scope</c> e <c>IdempotencyKey</c> (comparação ordinal, sensível a
    /// maiúsculas). Nos outros bancos, as convenções do TEC.ORM trocam pela equivalente: <c>C</c> no PostgreSQL,
    /// <c>utf8mb4_bin</c> no MySQL; no Oracle a coluna fica sem collation (a comparação padrão, <c>NLS_COMP=BINARY</c>, já é binária).
    /// </summary>
    public const string BinaryCollation = "Latin1_General_100_BIN2";

    /// <summary>
    /// Mapeia a tabela usada pelo <see cref="EfIdempotencyStore{TContext}"/>. Chame no <c>OnModelCreating</c> e gere uma
    /// migration (a tabela faz parte do banco da aplicação).
    /// </summary>
    /// <param name="modelBuilder">Modelo do contexto.</param>
    /// <param name="schema">Schema (<c>null</c>: o padrão do modelo).</param>
    /// <param name="tableName">Nome da tabela.</param>
    /// <returns>O próprio <paramref name="modelBuilder"/>.</returns>
    /// <example>
    /// <code>
    /// protected override void OnModelCreating(ModelBuilder modelBuilder)
    /// {
    ///     modelBuilder.AddTecIdempotency(schema: "app");
    /// }
    /// </code>
    /// </example>
    public static ModelBuilder AddTecIdempotency(this ModelBuilder modelBuilder, string? schema = null, string tableName = DefaultTableName)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable(tableName, schema);
            entity.HasKey(r => new { r.Scope, r.Key });
            // Comparação ORDINAL (contrato do IIdempotencyStore): collation binária, senão a collation padrão (em geral sem
            // diferenciar maiúsculas) trataria "Abc" e "abc" como a mesma chave. O escopo (hash SHA-256 hex, 64 caracteres) cabe
            // nos 200. A collation é a do SQL Server; IdempotencyModelConvention ajusta para o banco do provedor.
            entity.Property(r => r.Scope).HasMaxLength(TEC.Cqrs.Idempotency.IdempotencyKey.MaxScopeLength).IsUnicode().IsRequired()
                .UseCollation(BinaryCollation);
            entity.Property(r => r.Key).HasColumnName("IdempotencyKey").HasMaxLength(TEC.Cqrs.Idempotency.IdempotencyKey.MaxKeyLength).IsUnicode().IsRequired()
                .UseCollation(BinaryCollation);
            entity.Property(r => r.RequestHash).HasMaxLength(TEC.Cqrs.Idempotency.IdempotencyRequest.MaxRequestHashLength).IsUnicode(false).IsRequired();
            entity.Property(r => r.ContentType).HasMaxLength(200);
            entity.Property(r => r.HeadersJson).HasMaxLength(4000);
            entity.HasIndex(r => r.ExpiresAt);
        });
        return modelBuilder;
    }
}

/// <summary>
/// Convenção que troca a collation binária do SQL Server, aplicada por <see cref="IdempotencyModelBuilderExtensions.AddTecIdempotency"/>,
/// pela equivalente do banco do provedor: <c>C</c> no PostgreSQL, <c>utf8mb4_bin</c> no MySQL e nenhuma no Oracle (que compara
/// texto de forma binária por padrão). Collation escolhida pela aplicação (diferente da padrão) não é alterada.
/// </summary>
/// <remarks>
/// <para>MySQL: o <c>MySql.EntityFrameworkCore</c> ignora a collation relacional do EF Core ao gerar o DDL e só lê as anotações
/// próprias (<c>MySQL:Charset</c> e <c>MySQL:Collation</c>, as mesmas do <c>ForMySQLHasCollation</c>). Sem elas a coluna ficaria
/// com a collation padrão do banco (em geral sem diferenciar maiúsculas) e "Abc" e "abc" seriam a mesma chave.</para>
/// <para>Oracle: o corpo da resposta (<c>byte[]</c> sem tamanho) vira <c>BLOB</c>; o padrão do provedor é <c>RAW(2000)</c>, e uma
/// resposta maior que isso não seria guardada.</para>
/// </remarks>
/// <param name="database">Banco do provedor do EF Core do contexto.</param>
internal sealed class IdempotencyModelConvention(OrmDatabase database) : IModelFinalizingConvention
{
    /// <summary>Anotação de conjunto de caracteres da coluna no <c>MySql.EntityFrameworkCore</c>.</summary>
    internal const string MySqlCharsetAnnotation = "MySQL:Charset";

    /// <summary>Anotação de collation da coluna no <c>MySql.EntityFrameworkCore</c>.</summary>
    internal const string MySqlCollationAnnotation = "MySQL:Collation";

    /// <summary>Tipo binário longo do Oracle.</summary>
    internal const string OracleLargeBinary = "BLOB";

    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        if (database is OrmDatabase.SqlServer or OrmDatabase.Other ||
            modelBuilder.Metadata.FindEntityType(typeof(IdempotencyRecord)) is not { } entityType)
            return;

        string? collation = BinaryCollationFor(database);
        foreach (string name in new[] { nameof(IdempotencyRecord.Scope), nameof(IdempotencyRecord.Key) })
        {
            if (entityType.FindProperty(name) is not { } property ||
                !string.Equals(property.GetCollation(), IdempotencyModelBuilderExtensions.BinaryCollation, StringComparison.Ordinal))
                continue;

            // Pelo metadado: a collation padrão foi definida com origem explícita (fluent API) e o builder de convenção seria ignorado
            var mutable = (Microsoft.EntityFrameworkCore.Metadata.IMutableProperty)property;
            mutable.SetCollation(collation);
            if (database == OrmDatabase.MySql && property.FindAnnotation(MySqlCollationAnnotation) is null)
            {
                // A collation exige o conjunto de caracteres dela (um banco em latin1 recusaria utf8mb4_bin sem o CHARACTER SET)
                mutable.SetAnnotation(MySqlCharsetAnnotation, "utf8mb4");
                mutable.SetAnnotation(MySqlCollationAnnotation, collation);
            }
        }

        if (database == OrmDatabase.Oracle && entityType.FindProperty(nameof(IdempotencyRecord.Body)) is { } body && body.GetMaxLength() is null)
            body.Builder.HasColumnType(OracleLargeBinary);
    }

    /// <summary>Collation binária (comparação ordinal) de cada banco; <c>null</c> = sem collation de coluna.</summary>
    internal static string? BinaryCollationFor(OrmDatabase database) => database switch
    {
        OrmDatabase.SqlServer => IdempotencyModelBuilderExtensions.BinaryCollation,
        OrmDatabase.PostgreSql => "C",
        OrmDatabase.MySql => "utf8mb4_bin",
        _ => null
    };
}

/// <summary>Metadados JSON gerados em compilação dos cabeçalhos guardados.</summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class IdempotencyJsonContext : JsonSerializerContext;
