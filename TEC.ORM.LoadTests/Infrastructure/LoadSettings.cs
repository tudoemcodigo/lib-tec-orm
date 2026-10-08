using System.Globalization;

namespace TEC.ORM.LoadTests.Infrastructure;

/// <summary>
/// Parâmetros dos testes de carga (as categorias ficam em <see cref="TestCategories"/>). Os volumes e durações dos testes pesados podem ser ajustados por
/// variáveis de ambiente, sem recompilar (ex.: TEC_CARGA_SOAK_SEGUNDOS=600 para um soak de 10 minutos).
/// </summary>
public static class LoadSettings
{
    /// <summary>
    /// Chave de [NotInParallel] dos testes rápidos com banco: um de cada vez, porque disputam as mesmas tabelas (bloqueios de um
    /// distorceriam as latências e os tempos limite do outro).
    /// </summary>
    public const string Database = "carga-banco";

    /// <summary>Chave de [NotInParallel] dos testes que medem alocação ou tempo no CI.</summary>
    public const string Exclusive = "carga-exclusiva";

    /// <summary>Contas gravadas no teste de volume (padrão 50 mil).</summary>
    public static int Rows => GetInt("TEC_CARGA_LINHAS", 50_000);

    /// <summary>Duração do soak (padrão 120 s).</summary>
    public static int SoakSeconds => GetInt("TEC_CARGA_SOAK_SEGUNDOS", 120);

    /// <summary>Duração da carga sustentada no SQL Server (padrão 60 s).</summary>
    public static int LoadSeconds => GetInt("TEC_CARGA_DURACAO_SEGUNDOS", 60);

    /// <summary>Workers simultâneos na carga sustentada e no soak (padrão 32).</summary>
    public static int Concurrency => GetInt("TEC_CARGA_CONCORRENCIA", 32);

    /// <summary>
    /// Pasta onde os testes gravam os relatórios em Markdown (o workflow de performance publica no resumo do CI).
    /// Sem a variável, os relatórios vão só para a saída do teste.
    /// </summary>
    public static string? ReportDirectory => Environment.GetEnvironmentVariable("TEC_CARGA_RELATORIOS");

    /// <summary>Escreve o relatório na saída do teste e, se configurado, em arquivo.</summary>
    public static void Report(string title, string body)
    {
        Console.WriteLine($"## {title}{Environment.NewLine}{body}");
        if (ReportDirectory is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "carga.md"), $"### {title}{Environment.NewLine}{Environment.NewLine}{body}{Environment.NewLine}");
        }
    }

    private static int GetInt(string name, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : defaultValue;
}
