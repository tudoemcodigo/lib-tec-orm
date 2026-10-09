using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TEC.Core.Common.Results;
using TEC.ORM.Common;

namespace TEC.ORM.SqlServer.Internal;

/// <summary>Converte exceções do EF Core, do SqlClient e do Dapper nos erros padronizados de <see cref="OrmErrors"/>.</summary>
internal static class OrmExceptionTranslator
{
    /// <summary>Resultado da tradução: o erro, se é falha de infraestrutura e o número do erro do SQL Server (0 se não houver).</summary>
    internal readonly record struct Translation(Error Error, bool IsInfrastructure, int SqlErrorNumber);

    // Números de erro do SQL Server
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int ReferenceConstraintViolation = 547;
    private const int Deadlock = 1205;
    private const int ClientTimeout = -2;
    // Severidade 20 a 25: erro fatal que encerra a conexão (rede, servidor, sessão)
    private const byte FatalConnectionSeverity = 20;
    private static readonly HashSet<int> ConnectionErrors =
    [
        -1,      // erro de rede ao estabelecer a conexão
        53,      // servidor não encontrado
        40,      // não foi possível abrir a conexão
        233,     // conexão encerrada pelo servidor
        258,     // tempo de espera da conexão (SqlClient 6+ ao não alcançar o servidor)
        4060,    // não foi possível abrir o banco do login
        10053,   // conexão abortada
        10054,   // conexão redefinida
        10060,   // tempo limite de rede
        10061,   // conexão recusada
        18456,   // login falhou
        18452    // login de domínio não confiável
    ];

    /// <summary>
    /// Falhas transitórias do SQL Server que justificam nova tentativa de uma <b>leitura</b>: rede/sessão encerrada, failover e
    /// banco temporariamente indisponível (Azure SQL), recursos do serviço e vítima de deadlock. Fora: tempo esgotado (repetir
    /// agravaria a consulta cara), login recusado e erros de dados.
    /// </summary>
    private static readonly HashSet<int> TransientErrors =
    [
        Deadlock,
        -1,      // erro de rede ao estabelecer a conexão
        20,      // instância não aceita conexões
        64,      // conexão encerrada durante o login
        121,     // erro de transporte (semáforo)
        233,     // conexão encerrada pelo servidor
        4060,    // banco do login indisponível (failover)
        4221,    // réplica secundária ainda não disponível
        10053,   // conexão abortada
        10054,   // conexão redefinida
        10060,   // tempo limite de rede
        10928,   // limite de recursos (Azure SQL)
        10929,   // limite de recursos (Azure SQL)
        40143,   // falha de conexão no gateway (Azure SQL)
        40197,   // erro ao processar a requisição (Azure SQL)
        40501,   // serviço ocupado (Azure SQL)
        40540,   // serviço indisponível (Azure SQL)
        40613,   // banco indisponível (Azure SQL, failover)
        42108,   // gateway: conexão não encontrada (Azure SQL)
        42109,   // gateway: pool de conexões esgotado (Azure SQL)
        49918,   // recursos insuficientes (Azure SQL)
        49919,   // muitas operações em andamento (Azure SQL)
        49920    // muitas operações em andamento (Azure SQL)
    ];

    /// <summary>Chave em <see cref="Exception.Data"/> que marca falha ao abrir a conexão (ver <see cref="MarkConnectionOpenFailure"/>).</summary>
    private const string ConnectionOpenFailureKey = "TEC.ORM.ConnectionOpenFailure";

    /// <summary>
    /// Marca a exceção como falha ao abrir a conexão. Cobre o esgotamento do pool do SqlClient ("tempestade no pool"), que é um
    /// <see cref="InvalidOperationException"/> sem número de erro e com mensagem traduzida (não dá para reconhecer pelo texto):
    /// com a marca ele vira <c>ORM_CONEXAO_INDISPONIVEL</c> (infraestrutura, sem stack trace no log), e não falha de programação.
    /// </summary>
    public static void MarkConnectionOpenFailure(Exception exception)
    {
        if (exception.Data is { IsReadOnly: false } data)
            data[ConnectionOpenFailureKey] = true;
    }

    /// <summary>Indica se a exceção foi marcada por <see cref="MarkConnectionOpenFailure"/>.</summary>
    public static bool IsMarkedConnectionOpenFailure(Exception exception) =>
        exception.Data is { } data && data.Contains(ConnectionOpenFailureKey);

    /// <summary>
    /// Indica se a falha na abertura é o esgotamento do pool do SqlClient: <see cref="InvalidOperationException"/> (sem número
    /// de erro, mensagem traduzida) depois de esperar praticamente todo o <c>Connect Timeout</c>. Outras
    /// <see cref="InvalidOperationException"/> (rápidas) são erro de programação e seguem como <c>ORM_FALHA</c>.
    /// </summary>
    /// <param name="exception">Exceção da abertura.</param>
    /// <param name="elapsed">Tempo gasto na tentativa de abertura.</param>
    /// <param name="connectTimeoutSeconds"><c>Connect Timeout</c> da conexão (segundos; 0 = sem limite).</param>
    public static bool IsPoolExhaustion(Exception exception, TimeSpan elapsed, int connectTimeoutSeconds) =>
        exception is InvalidOperationException and not OrmConnectionException and not ObjectDisposedException
        && connectTimeoutSeconds > 0
        && elapsed >= TimeSpan.FromSeconds(connectTimeoutSeconds * 0.9);

    /// <summary>Indica se a exceção é uma falha transitória do SQL Server (ver <see cref="TransientErrors"/>).</summary>
    public static bool IsTransient(Exception exception)
    {
        if (FindSqlException(exception) is not { } sql)
            return false;
        foreach (SqlError error in sql.Errors)
        {
            if (TransientErrors.Contains(error.Number))
                return true;
        }
        return TransientErrors.Contains(sql.Number);
    }

    public static Translation Translate(Exception exception)
    {
        var sql = FindSqlException(exception);
        int number = sql?.Number ?? 0;

        if (exception is DbUpdateConcurrencyException)
            return new(OrmErrors.Concurrency(), false, number);

        if (sql is not null)
        {
            if (number is UniqueIndexViolation or UniqueConstraintViolation or ReferenceConstraintViolation)
                return new(OrmErrors.Conflict(), false, number);
            if (number == Deadlock)
                return new(OrmErrors.Concurrency(), false, number);
            if (number == ClientTimeout)
                return new(OrmErrors.Timeout(), true, number);
            if (ConnectionErrors.Contains(number) || TransientErrors.Contains(number) || sql.Class >= FatalConnectionSeverity)
                return new(OrmErrors.ConnectionUnavailable(), true, number);
            return new(OrmErrors.Failure(), true, number);
        }

        return exception switch
        {
            OrmConnectionException connection => new(connection.Error, true, 0),
            Auditing.OrmAuditException audit => new(audit.Error, false, 0),
            TimeoutException => new(OrmErrors.Timeout(), true, 0),
            _ when IsConnectionOpenFailure(exception) => new(OrmErrors.ConnectionUnavailable(), true, 0),
            _ => new(OrmErrors.Failure(), false, 0)
        };
    }

    /// <summary>Indica se a exceção é de banco (não deve ter a mensagem registrada: pode conter valores de colunas).</summary>
    public static bool IsDatabaseException(Exception exception) =>
        exception is DbUpdateException or OrmConnectionException || FindSqlException(exception) is not null ||
        IsConnectionOpenFailure(exception);

    private static bool IsConnectionOpenFailure(Exception exception) =>
        exception.Data.Contains(ConnectionOpenFailureKey);

    private static SqlException? FindSqlException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
                return sql;
        }
        return null;
    }
}

/// <summary>
/// Falha ao preparar a conexão (segredo indisponível ou recusado). Lançada só dentro do pipeline do EF Core (interceptor), que
/// não aceita <see cref="Result"/>; o <c>OrmOperationRunner</c> a converte de volta no erro original.
/// </summary>
internal sealed class OrmConnectionException(Error error)
    : InvalidOperationException("Conexão com o banco de dados indisponível: " + error.Code)
{
    public Error Error { get; } = error;
}
