using MySqlConnector;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.MySql.Dialect;

/// <summary>
/// Tradutor de exceções do provedor MySqlConnector / MySQL para erros de domínio do TEC.ORM.
/// </summary>
public sealed class MySqlExceptionTranslator : IOrmExceptionTranslator
{
    private static readonly HashSet<int> ConnectionErrors =
    [
        1042, // Unable to connect to any of the specified hosts
        2002, // Can't connect to server
        2003, // Can't connect to MySQL server
        2006, // MySQL server has gone away
        2013  // Lost connection to MySQL server during query
    ];

    private static readonly HashSet<int> TransientErrors =
    [
        1205, // Lock wait timeout exceeded
        1213, // Deadlock found when trying to get lock
        1040, // Too many connections
        2006, // MySQL server has gone away
        2013  // Lost connection
    ];

    /// <inheritdoc />
    public OrmDbExceptionTranslation Translate(Exception exception)
    {
        if (exception is MySqlException mySqlEx)
        {
            int number = mySqlEx.Number;
            if (number is 1062 or 1451 or 1452)
                return new OrmDbExceptionTranslation(OrmErrors.Conflict(), false, number.ToString());

            if (number is 1213 or 1205)
                return new OrmDbExceptionTranslation(OrmErrors.Concurrency(), false, number.ToString());

            if (number is 1317 or 1801 or 1907)
                return new OrmDbExceptionTranslation(OrmErrors.Timeout(), true, number.ToString());

            if (ConnectionErrors.Contains(number))
                return new OrmDbExceptionTranslation(OrmErrors.ConnectionUnavailable(), true, number.ToString());

            return new OrmDbExceptionTranslation(OrmErrors.Failure(), false, number.ToString());
        }

        if (exception is TimeoutException)
            return new OrmDbExceptionTranslation(OrmErrors.Timeout(), true, null);

        return new OrmDbExceptionTranslation(OrmErrors.Failure(), false, null);
    }

    /// <inheritdoc />
    public bool IsTransient(Exception exception)
    {
        if (exception is MySqlException mySqlEx)
            return TransientErrors.Contains(mySqlEx.Number);

        return false;
    }

    /// <inheritdoc />
    public bool IsConnectionFailure(Exception exception)
    {
        if (exception is MySqlException mySqlEx)
            return ConnectionErrors.Contains(mySqlEx.Number);

        return false;
    }
}
