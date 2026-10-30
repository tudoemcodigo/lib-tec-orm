using System.Globalization;
using Microsoft.Data.SqlClient;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.SqlServer;

/// <summary>
/// Tradutor das exceções do SqlClient (<see cref="SqlException"/>, também dentro de <c>DbUpdateException</c>) para os erros de
/// <see cref="OrmErrors"/>. Concorrência otimista, tempo esgotado e as recusas do TEC.ORM ficam na base
/// (<see cref="OrmExceptionTranslatorBase"/>).
/// </summary>
public sealed class SqlServerExceptionTranslator : OrmExceptionTranslatorBase
{
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

    /// <inheritdoc />
    protected override bool IsProviderException(Exception exception) => exception is SqlException;

    /// <inheritdoc />
    protected override OrmDbExceptionTranslation TranslateProviderException(Exception providerException)
    {
        var sql = (SqlException)providerException;
        int number = sql.Number;
        string code = number.ToString(CultureInfo.InvariantCulture);
        if (number is UniqueIndexViolation or UniqueConstraintViolation or ReferenceConstraintViolation)
            return new OrmDbExceptionTranslation(OrmErrors.Conflict(), false, code);
        if (number == Deadlock)
            return new OrmDbExceptionTranslation(OrmErrors.Concurrency(), false, code);
        if (number == ClientTimeout)
            return new OrmDbExceptionTranslation(OrmErrors.Timeout(), true, code);
        if (ConnectionErrors.Contains(number) || TransientErrors.Contains(number) || sql.Class >= FatalConnectionSeverity)
            return new OrmDbExceptionTranslation(OrmErrors.ConnectionUnavailable(), true, code);
        return new OrmDbExceptionTranslation(OrmErrors.Failure(), true, code);
    }

    /// <inheritdoc />
    protected override bool IsTransientProviderException(Exception providerException)
    {
        var sql = (SqlException)providerException;
        foreach (SqlError error in sql.Errors)
        {
            if (TransientErrors.Contains(error.Number))
                return true;
        }
        return TransientErrors.Contains(sql.Number);
    }

    /// <inheritdoc />
    protected override bool IsConnectionFailureProviderException(Exception providerException)
    {
        var sql = (SqlException)providerException;
        return sql.Number is not (Deadlock or ClientTimeout)
               && (ConnectionErrors.Contains(sql.Number) || TransientErrors.Contains(sql.Number) || sql.Class >= FatalConnectionSeverity);
    }

    /// <inheritdoc />
    protected override bool IsUniqueViolationProviderException(Exception providerException) =>
        ((SqlException)providerException).Number is UniqueIndexViolation or UniqueConstraintViolation;

    /// <inheritdoc />
    protected override string? GetErrorCode(Exception providerException) =>
        ((SqlException)providerException).Number.ToString(CultureInfo.InvariantCulture);
}
