using Npgsql;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.PostgreSql.Dialect;

/// <summary>
/// Tradutor de exceções do provedor Npgsql / PostgreSQL para erros de domínio do TEC.ORM.
/// </summary>
public sealed class PostgreSqlExceptionTranslator : IOrmExceptionTranslator
{
    private static readonly HashSet<string> ConnectionStates =
    [
        "08000", "08003", "08006", "08001", "08004", "08007", "08P01", "57P01", "57P02", "57P03"
    ];

    private static readonly HashSet<string> TransientStates =
    [
        "40001", // serialization failure
        "40P01", // deadlock detected
        "53000", // insufficient resources
        "53100", // disk full
        "53200", // out of memory
        "53300", // too many connections
        "57P03", // cannot connect now
        "08006", // connection failure
        "08001"  // unable to connect
    ];

    /// <inheritdoc />
    public OrmDbExceptionTranslation Translate(Exception exception)
    {
        if (exception is NpgsqlException npgsqlEx)
        {
            if (npgsqlEx is PostgresException pgEx)
            {
                string state = pgEx.SqlState;
                if (state == "23505" || state == "23503")
                    return new OrmDbExceptionTranslation(OrmErrors.Conflict(), false, state);
                if (state == "40P01" || state == "40001")
                    return new OrmDbExceptionTranslation(OrmErrors.Concurrency(), false, state);
                if (state == "57014")
                    return new OrmDbExceptionTranslation(OrmErrors.Timeout(), true, state);
                if (ConnectionStates.Contains(state))
                    return new OrmDbExceptionTranslation(OrmErrors.ConnectionUnavailable(), true, state);

                return new OrmDbExceptionTranslation(OrmErrors.Failure(), false, state);
            }

            if (npgsqlEx.InnerException is TimeoutException)
                return new OrmDbExceptionTranslation(OrmErrors.Timeout(), true, null);

            return new OrmDbExceptionTranslation(OrmErrors.ConnectionUnavailable(), true, null);
        }

        if (exception is TimeoutException)
            return new OrmDbExceptionTranslation(OrmErrors.Timeout(), true, null);

        return new OrmDbExceptionTranslation(OrmErrors.Failure(), false, null);
    }

    /// <inheritdoc />
    public bool IsTransient(Exception exception)
    {
        if (exception is PostgresException pgEx && TransientStates.Contains(pgEx.SqlState))
            return true;

        if (exception is NpgsqlException && exception is not PostgresException)
            return true;

        return false;
    }

    /// <inheritdoc />
    public bool IsConnectionFailure(Exception exception)
    {
        if (exception is PostgresException pgEx)
            return ConnectionStates.Contains(pgEx.SqlState);

        return exception is NpgsqlException;
    }
}
