using System.Data.Common;
using System.Globalization;
using Dapper;
using TEC.Core.Common.Results;
using TEC.ORM.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Diagnostics;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Security;
using TEC.ORM.Queries;

namespace TEC.ORM.EntityFrameworkCore;

/// <summary>
/// Implementação de <see cref="IOrmQueryExecutor"/> com Dapper: leituras complexas (agregações, joins, relatórios) sobre a
/// conexão somente leitura do <see cref="IOrmConnectionSecurity"/>, sempre com parâmetros.
/// </summary>
/// <remarks>
/// <para>Cada chamada abre e fecha a própria conexão (o pool do driver reaproveita). Não participa da transação do EF Core
/// nem de um <c>TransactionScope</c> ambiente (a conexão de leitura não entra nele): para ler o que acabou de ser gravado na
/// mesma transação, use o <see cref="IOrmRepository{TEntity, TKey}"/>.</para>
/// <para>Duas barreiras contra escrita: a verificação de somente leitura do dialeto (<see cref="OrmOptions.EnforceReadOnlyQueries"/>)
/// e, no PostgreSQL, MySQL e Oracle, uma transação <c>READ ONLY</c> aberta antes da consulta e desfeita depois (o banco recusa
/// a escrita mesmo que o SQL passe pela verificação). O SQL Server não tem transação somente leitura: lá a proteção principal é
/// o login da conexão de leitura ter só <c>SELECT</c>, com <c>ApplicationIntent=ReadOnly</c>.</para>
/// <para>A consulta deve produzir um único resultado: um segundo (<c>SELECT ... SELECT ...</c> sem <c>;</c>) cancela o comando
/// e falha com <c>ORM_CONSULTA_NAO_PERMITIDA</c>, sem trafegar as linhas dele.</para>
/// <para>As linhas são lidas uma a uma do banco (sem carregar o resultado inteiro antes): passando de
/// <see cref="OrmOptions.MaxQueryRows"/> (ou da segunda linha, em <see cref="QuerySingleOrDefaultAsync{T}"/>), o comando é
/// cancelado no servidor e o resto nunca trafega. Toda consulta tem o tempo limite de <see cref="OrmOptions.CommandTimeoutSeconds"/>
/// e é repetida em falha transitória (<see cref="OrmOptions.TransientRetryCount"/>), pois é somente leitura.</para>
/// <para>Os marcadores do <c>SqlQuery.Interpolated</c> saem com o prefixo do dialeto (<c>:p0</c> no Oracle, <c>@p0</c> nos
/// demais). No <c>SqlQuery.Create</c>, escreva os marcadores do banco (<c>:Nome</c> no Oracle).</para>
/// </remarks>
/// <param name="security">Conexão somente leitura (segredo do TEC.Vault).</param>
/// <param name="runner">Observabilidade, tradução de erros e novas tentativas.</param>
/// <param name="options">Opções (limites, tempo limite, verificação de somente leitura e dialeto).</param>
/// <param name="sqlValidator">Verificação de somente leitura; <c>null</c> usa a do dialeto.</param>
public class OrmQueryExecutor(
    IOrmConnectionSecurity security,
    IOrmOperationRunner runner,
    OrmOptions options,
    IReadOnlySqlValidator? sqlValidator = null)
    : IOrmQueryExecutor
{
    private readonly IReadOnlySqlValidator? _sqlValidator = sqlValidator ?? options.Dialect?.ReadOnlyValidator;

    /// <inheritdoc />
    public virtual Task<Result<IReadOnlyList<T>>> QueryAsync<T>(SqlQuery query, CancellationToken cancellationToken = default) =>
        ExecuteAsync(query, "query", precondition: null, (reader, token) =>
            ReadRowsAsync(reader, Parser<T>(reader), options.MaxQueryRows, token), cancellationToken);

    /// <inheritdoc />
    public virtual Task<Result<T?>> QuerySingleOrDefaultAsync<T>(SqlQuery query, CancellationToken cancellationToken = default) =>
        ExecuteAsync<T?>(query, "query-single", precondition: null, async (reader, token) =>
        {
            // Lê no máximo duas linhas: "mais de uma" vira erro padronizado sem trazer o resto do resultado
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
                return (Result<T?>.Success(default), true);
            var row = Parser<T>(reader)(reader);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
                return (OrmErrors.InvalidInput("sql", "A consulta retornou mais de uma linha."), false);
            return (Result<T?>.Success(row), true);
        }, cancellationToken);

    /// <inheritdoc />
    public virtual Task<Result<T?>> ExecuteScalarAsync<T>(SqlQuery query, CancellationToken cancellationToken = default) =>
        ExecuteAsync<T?>(query, "scalar", precondition: null, async (reader, token) =>
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.FieldCount == 0)
                return (Result<T?>.Success(default), true);
            var value = Parser<T>(reader, start: 0, length: 1)(reader);
            // Só a primeira linha interessa: as demais não são lidas (o comando é cancelado se houver mais)
            return (Result<T?>.Success(value), !await reader.ReadAsync(token).ConfigureAwait(false));
        }, cancellationToken);

    /// <inheritdoc />
    public virtual Task<Result<IReadOnlyList<TResult>>> QueryAsync<TFirst, TSecond, TResult>(SqlQuery query,
        Func<TFirst, TSecond, TResult> map, string splitOn = "Id", CancellationToken cancellationToken = default)
    {
        Error? precondition = map is null || string.IsNullOrWhiteSpace(splitOn)
            ? OrmErrors.InvalidInput(map is null ? nameof(map) : nameof(splitOn), "O mapeamento e a coluna de divisão são obrigatórios.")
            : null;

        return ExecuteAsync(query, "query-join", precondition, (reader, token) =>
        {
            // Como o Dapper: a segunda parte começa na última coluna com o nome de splitOn (exceto a primeira coluna)
            int split = -1;
            for (int i = reader.FieldCount - 1; i > 0; i--)
            {
                if (string.Equals(reader.GetName(i), splitOn, StringComparison.OrdinalIgnoreCase))
                {
                    split = i;
                    break;
                }
            }
            if (split < 0)
                return Task.FromResult<(Result<IReadOnlyList<TResult>>, bool)>(
                    (OrmErrors.InvalidInput(nameof(splitOn), "A coluna de divisão não está no resultado da consulta."), false));

            var first = Parser<TFirst>(reader, start: 0, length: split);
            var second = Parser<TSecond>(reader, start: split, length: reader.FieldCount - split, nullIfFirstMissing: true);
            return ReadRowsAsync(reader, row => map!(first(row), second(row)), options.MaxQueryRows, token);
        }, cancellationToken);
    }

    /// <summary>
    /// Valida (dentro da operação observada, antes de abrir a conexão), abre a conexão de leitura, abre a transação somente
    /// leitura do dialeto, executa com os parâmetros e entrega o leitor a <paramref name="read"/>. Ao sair antes do fim do
    /// resultado, cancela o comando no servidor.
    /// </summary>
    private Task<Result<T>> ExecuteAsync<T>(SqlQuery? query, string operationName, Error? precondition,
        Func<DbDataReader, CancellationToken, Task<(Result<T> Result, bool Complete)>> read, CancellationToken cancellationToken)
    {
        // Somente leitura e conexão própria: pode ser repetida em falha transitória
        var operation = new OrmOperation(OrmDiagnostics.DapperProvider, operationName, query?.Name ?? "invalida", isWrite: false)
        {
            IsRetryable = true
        };
        return runner.ExecuteAsync(operation, async token =>
        {
            if (query is null)
                return OrmErrors.InvalidInput(nameof(query), "A consulta é obrigatória.");
            if (precondition is not null)
                return precondition;

            var dialect = options.Dialect;
            string sql = query.SqlFor(dialect?.ParameterPrefix ?? '@');
            if (options.EnforceReadOnlyQueries)
            {
                // Falha fechada: sem verificação para o banco configurado, nenhuma consulta passa
                if (_sqlValidator is null)
                    return OrmErrors.QueryNotAllowed("não há verificação de somente leitura para o banco configurado.");
                if (_sqlValidator.Check(sql) is { } reason)
                    return OrmErrors.QueryNotAllowed(reason);
            }

            var opened = await security.OpenConnectionAsync(OrmConnectionKind.ReadOnly, token).ConfigureAwait(false);
            if (opened.IsFailure)
                return opened.ToFailure<T>();

            var connection = opened.Value;
            await using (connection.ConfigureAwait(false))
            {
                var readOnly = dialect is null
                    ? null
                    : await dialect.BeginReadOnlyTransactionAsync(connection, token).ConfigureAwait(false);
                try
                {
                    var parameters = new DynamicParameters();
                    foreach (var (name, value) in query.Parameters)
                        parameters.Add(name, value);

                    var command = new CommandDefinition(sql, parameters, transaction: readOnly as DbTransaction,
                        commandTimeout: options.CommandTimeoutSeconds, cancellationToken: token);
                    var reader = await connection.ExecuteReaderAsync(command).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        bool complete = false;
                        try
                        {
                            var (result, done) = await read(reader, token).ConfigureAwait(false);
                            // Outro resultado depois do primeiro (vários SELECT sem ';'): seria lido e descartado inteiro ao fechar
                            // o leitor, fora do MaxQueryRows. Recusado, com o comando cancelado no servidor.
                            if (done && await reader.NextResultAsync(token).ConfigureAwait(false))
                                return OrmErrors.QueryNotAllowed("a consulta produziu mais de um resultado.");
                            complete = done;
                            return result;
                        }
                        finally
                        {
                            if (!complete)
                                CancelCommand(reader);
                        }
                    }
                }
                finally
                {
                    if (readOnly is not null)
                        await EndReadOnlyAsync(readOnly).ConfigureAwait(false);
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Encerra a transação somente leitura (rollback). Melhor esforço: ela não gravou nada, e o pool do driver desfaz o que
    /// sobrar ao devolver a conexão; uma falha aqui não pode esconder o resultado (ou o erro) da consulta.
    /// </summary>
    private static async ValueTask EndReadOnlyAsync(IAsyncDisposable readOnly)
    {
        try
        {
            await readOnly.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // ver o resumo: a transação somente leitura não tem o que perder
        catch (Exception)
#pragma warning restore CA1031
        {
            // Conexão perdida ou comando cancelado: o banco já encerrou a transação
        }
    }

    /// <summary>
    /// Lê até <paramref name="maxRows"/> linhas; uma a mais é falha <c>ORM_LIMITE_EXCEDIDO</c> (o resto não é lido).
    /// </summary>
    private static async Task<(Result<IReadOnlyList<TRow>> Result, bool Complete)> ReadRowsAsync<TRow>(DbDataReader reader,
        Func<DbDataReader, TRow> parse, int maxRows, CancellationToken cancellationToken)
    {
        var rows = new List<TRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count == maxRows)
                return (OrmErrors.QueryTooManyRows(maxRows), false);
            rows.Add(parse(reader));
        }
        return (rows, true);
    }

    /// <summary>
    /// Conversão de uma linha (ou de um trecho de colunas) para <typeparamref name="T"/> com as regras do Dapper: o
    /// desserializador dele e a mesma conversão final do <c>Query&lt;T&gt;</c> (tipo compatível, <c>NULL</c> → <c>default</c>,
    /// senão <see cref="Convert.ChangeType(object, Type, IFormatProvider)"/>).
    /// </summary>
    private static Func<DbDataReader, T> Parser<T>(DbDataReader reader, int start = 0, int length = -1, bool nullIfFirstMissing = false)
    {
        var raw = reader.GetRowParser<object?>(typeof(T), start, length, nullIfFirstMissing);
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return row => raw(row) switch
        {
            T value => value,
            null or DBNull => default!,
            var other => (T)Convert.ChangeType(other, target, CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Resultado não lido até o fim (limite atingido, segunda linha, erro): cancela o comando antes de fechar o leitor. Sem
    /// isso o driver leria e descartaria todas as linhas restantes ao fechar, o que tornaria o limite inútil contra uma
    /// consulta enorme.
    /// </summary>
    private static void CancelCommand(DbDataReader reader)
    {
        if (!reader.IsClosed && reader is IWrappedDataReader { Command: { } command })
            command.Cancel();
    }
}
