namespace TEC.ORM.SqlServer.Idempotency;

/// <summary>Opções do <c>AddTecOrmIdempotency&lt;TContext&gt;</c>.</summary>
public sealed class OrmIdempotencyOptions
{
    /// <summary>Executa a limpeza periódica das chaves expiradas em segundo plano. Padrão: <c>true</c>.</summary>
    /// <remarks>Com várias instâncias, todas podem limpar: as remoções são em lotes curtos e idempotentes.</remarks>
    public bool EnableCleanup { get; set; } = true;

    /// <summary>Intervalo entre as limpezas (1 minuto a 1 dia). Padrão: 1 hora.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Linhas removidas por comando (1 a 10.000). Padrão: 1.000.</summary>
    public int CleanupBatchSize { get; set; } = 1000;

    /// <summary>Lotes por ciclo de limpeza (1 a 1.000), para não ocupar o banco por muito tempo. Padrão: 50.</summary>
    public int MaxBatchesPerCleanup { get; set; } = 50;

    /// <summary>Valida as opções.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Valor fora dos limites.</exception>
    internal void Validate()
    {
        if (CleanupInterval < TimeSpan.FromMinutes(1) || CleanupInterval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(CleanupInterval), CleanupInterval, "Use de 1 minuto a 1 dia.");
        if (CleanupBatchSize is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(CleanupBatchSize), CleanupBatchSize, "Use de 1 a 10.000.");
        if (MaxBatchesPerCleanup is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(MaxBatchesPerCleanup), MaxBatchesPerCleanup, "Use de 1 a 1.000.");
    }
}
