using TEC.Core.Common.Results;

namespace TEC.ORM.EntityFrameworkCore.Security;

/// <summary>
/// Falha ao preparar a conexão (segredo indisponível ou recusado, circuito aberto). Lançada só dentro do pipeline do EF Core
/// (interceptor), que não aceita <see cref="Result"/>; o <c>OrmOperationRunner</c> a converte de volta no erro original.
/// </summary>
internal sealed class OrmConnectionException(Error error)
    : InvalidOperationException("Conexão com o banco de dados indisponível: " + error.Code)
{
    public Error Error { get; } = error;
}

/// <summary>Marca de falha na abertura da conexão (esgotamento do pool), reconhecida pela tradução de exceções.</summary>
internal static class OrmConnectionFailures
{
    /// <summary>Chave em <see cref="Exception.Data"/> que marca falha ao abrir a conexão.</summary>
    private const string ConnectionOpenFailureKey = "TEC.ORM.ConnectionOpenFailure";

    /// <summary>
    /// Marca a exceção como falha ao abrir a conexão. Cobre o esgotamento do pool (no SqlClient, um
    /// <see cref="InvalidOperationException"/> sem número de erro e com mensagem traduzida, que não dá para reconhecer pelo
    /// texto): com a marca ele vira <c>ORM_CONEXAO_INDISPONIVEL</c> (infraestrutura, sem stack trace no log), e não falha de
    /// programação.
    /// </summary>
    public static void Mark(Exception exception)
    {
        if (exception.Data is { IsReadOnly: false } data)
            data[ConnectionOpenFailureKey] = true;
    }

    /// <summary>Indica se a exceção foi marcada por <see cref="Mark"/>.</summary>
    public static bool IsMarked(Exception exception) =>
        exception.Data is { } data && data.Contains(ConnectionOpenFailureKey);

    /// <summary>
    /// Indica se a falha na abertura é o esgotamento do pool: <see cref="InvalidOperationException"/> (sem número de erro)
    /// depois de esperar praticamente todo o <c>Connect Timeout</c>. Outras <see cref="InvalidOperationException"/> (rápidas)
    /// são erro de programação e seguem como <c>ORM_FALHA</c>.
    /// </summary>
    /// <param name="exception">Exceção da abertura.</param>
    /// <param name="elapsed">Tempo gasto na tentativa de abertura.</param>
    /// <param name="connectTimeoutSeconds"><c>Connect Timeout</c> da conexão (segundos; 0 = sem limite).</param>
    public static bool IsPoolExhaustion(Exception exception, TimeSpan elapsed, int connectTimeoutSeconds) =>
        exception is InvalidOperationException and not OrmConnectionException and not ObjectDisposedException
        && connectTimeoutSeconds > 0
        && elapsed >= TimeSpan.FromSeconds(connectTimeoutSeconds * 0.9);
}
