using TEC.Core.Common.Results;
using TEC.ORM.Queries;

namespace TEC.ORM.Abstractions;

/// <summary>
/// Leituras complexas (agregações, joins, relatórios) com SQL parametrizado. Na implementação SQL Server: Dapper, com a
/// conexão somente leitura.
/// </summary>
/// <remarks>
/// <para>Só aceita <see cref="SqlQuery"/> (sempre parametrizada) e, por padrão, só comandos de leitura (<c>SELECT</c>/<c>WITH</c>).
/// Defesa principal: o segredo da conexão de leitura deve ser de um login com permissão só de <c>SELECT</c>.</para>
/// <para>Limites: toda consulta tem tempo limite e as que devolvem linhas leem no máximo o limite configurado na
/// implementação (acima dele, falha com <c>ORM_LIMITE_EXCEDIDO</c> sem carregar o resto); pagine no próprio SQL
/// (<c>OFFSET</c>/<c>FETCH</c>) para relatórios grandes.</para>
/// </remarks>
public interface IOrmQueryExecutor
{
    /// <summary>Executa a consulta e mapeia cada linha para <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Tipo de cada linha (classe com propriedades das colunas, ou tipo simples para uma coluna).</typeparam>
    /// <param name="query">Consulta parametrizada.</param>
    /// <param name="cancellationToken">Cancelamento (interrompe o comando no banco).</param>
    /// <returns>As linhas, ou falha (<c>ORM_LIMITE_EXCEDIDO</c> acima do limite de linhas).</returns>
    Task<Result<IReadOnlyList<T>>> QueryAsync<T>(SqlQuery query, CancellationToken cancellationToken = default);

    /// <summary>Executa a consulta e devolve a única linha (ou <c>default</c>); mais de uma linha é falha.</summary>
    /// <typeparam name="T">Tipo da linha.</typeparam>
    /// <param name="query">Consulta parametrizada.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <returns>A linha, <c>default</c> sem linhas, ou falha <c>ORM_ENTRADA_INVALIDA</c> com mais de uma (sem ler as demais).</returns>
    Task<Result<T?>> QuerySingleOrDefaultAsync<T>(SqlQuery query, CancellationToken cancellationToken = default);

    /// <summary>Executa a consulta e devolve a primeira coluna da primeira linha (agregações: <c>COUNT</c>, <c>SUM</c>...).</summary>
    /// <typeparam name="T">Tipo do valor.</typeparam>
    /// <param name="query">Consulta parametrizada.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <returns>O valor (ou <c>default</c> sem linhas / <c>NULL</c>).</returns>
    Task<Result<T?>> ExecuteScalarAsync<T>(SqlQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executa um join e combina duas entidades por linha (<paramref name="splitOn"/>: coluna onde começa a segunda).
    /// </summary>
    /// <typeparam name="TFirst">Tipo da primeira parte da linha.</typeparam>
    /// <typeparam name="TSecond">Tipo da segunda parte (<c>null</c> quando a primeira coluna dela é <c>NULL</c>, ex.: <c>LEFT JOIN</c>).</typeparam>
    /// <typeparam name="TResult">Resultado da combinação.</typeparam>
    /// <param name="query">Consulta parametrizada.</param>
    /// <param name="map">Combina as duas partes de uma linha.</param>
    /// <param name="splitOn">Nome da coluna onde começa a segunda parte (a última com esse nome, como no Dapper).</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <returns>As linhas combinadas, ou falha (<c>ORM_LIMITE_EXCEDIDO</c> acima do limite de linhas).</returns>
    Task<Result<IReadOnlyList<TResult>>> QueryAsync<TFirst, TSecond, TResult>(SqlQuery query, Func<TFirst, TSecond, TResult> map,
        string splitOn = "Id", CancellationToken cancellationToken = default);
}
