namespace TEC.ORM.EntityFrameworkCore.Idempotency;

/// <summary>Opções de limpeza de idempotência.</summary>
public sealed class OrmIdempotencyOptions
{
    /// <summary>Indica se a limpeza automática de registros expirados deve ser executada em background.</summary>
    public bool EnableCleanup { get; set; } = true;

    /// <summary>Intervalo de execução do ciclo de limpeza.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Tamanho do lote de exclusão por ciclo.</summary>
    public int CleanupBatchSize { get; set; } = 1000;

    /// <summary>Quantidade máxima de lotes excluídos por ciclo de limpeza.</summary>
    public int MaxBatchesPerCleanup { get; set; } = 50;

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
