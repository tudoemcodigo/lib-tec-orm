using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace TEC.ORM.SqlServer.Idempotency;

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
            entity.Property(r => r.Scope).HasMaxLength(TEC.Cqrs.Idempotency.IdempotencyKey.MaxScopeLength).IsUnicode().IsRequired();
            entity.Property(r => r.Key).HasColumnName("IdempotencyKey").HasMaxLength(TEC.Cqrs.Idempotency.IdempotencyKey.MaxKeyLength).IsUnicode().IsRequired();
            entity.Property(r => r.RequestHash).HasMaxLength(TEC.Cqrs.Idempotency.IdempotencyRequest.MaxRequestHashLength).IsUnicode(false).IsRequired();
            entity.Property(r => r.ContentType).HasMaxLength(200);
            entity.Property(r => r.HeadersJson).HasMaxLength(4000);
            entity.HasIndex(r => r.ExpiresAt);
        });
        return modelBuilder;
    }
}

/// <summary>Metadados JSON gerados em compilação dos cabeçalhos guardados.</summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class IdempotencyJsonContext : JsonSerializerContext;
