using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

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
    /// <summary>Nome padrão da tabela de idempotência.</summary>
    public const string DefaultTableName = "IdempotencyKeys";

    /// <summary>Collation binária para sensibilidade de caixa.</summary>
    public const string BinaryCollation = "Latin1_General_100_BIN2";

    /// <summary>Mapeia a tabela de idempotência no DbContext.</summary>
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

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class IdempotencyJsonContext : JsonSerializerContext;
