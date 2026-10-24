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
/// Implementação de <see cref="IOrmQueryExecutor"/> com Dapper: leituras complexas sobre a conexão somente leitura do cofre.
/// </summary>
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

    private Task<Result<T>> ExecuteAsync<T>(SqlQuery? query, string operationName, Error? precondition,
        Func<DbDataReader, CancellationToken, Task<(Result<T> Result, bool Complete)>> read, CancellationToken cancellationToken)
    {
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
            if (options.EnforceReadOnlyQueries && _sqlValidator?.Check(query.Sql) is { } reason)
                return OrmErrors.QueryNotAllowed(reason);

            var opened = await security.OpenConnectionAsync(OrmConnectionKind.ReadOnly, token).ConfigureAwait(false);
            if (opened.IsFailure)
                return opened.ToFailure<T>();

            var connection = opened.Value;
            await using (connection.ConfigureAwait(false))
            {
                var parameters = new DynamicParameters();
                foreach (var (name, value) in query.Parameters)
                    parameters.Add(name, value);

                var command = new CommandDefinition(query.Sql, parameters, commandTimeout: options.CommandTimeoutSeconds,
                    cancellationToken: token);
                var reader = await connection.ExecuteReaderAsync(command).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    bool complete = false;
                    try
                    {
                        var (result, done) = await read(reader, token).ConfigureAwait(false);
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
        }, cancellationToken);
    }

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

    private static void CancelCommand(DbDataReader reader)
    {
        if (!reader.IsClosed && reader is IWrappedDataReader { Command: { } command })
            command.Cancel();
    }
}
