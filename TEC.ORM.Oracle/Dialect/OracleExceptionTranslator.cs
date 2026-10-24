using Oracle.ManagedDataAccess.Client;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.Oracle.Dialect;

/// <summary>
/// Tradutor de exceções do provedor Oracle para erros padronizados do TEC.ORM.
/// </summary>
public sealed class OracleExceptionTranslator : IOrmExceptionTranslator
{
    private static readonly HashSet<int> ConnectionErrors =
    [
        1017,  // ORA-01017: invalid username/password
        12154, // ORA-12154: TNS:could not resolve the connect identifier specified
        12170, // ORA-12170: TNS:Connect timeout occurred
        12514, // ORA-12514: TNS:listener does not currently know of service requested
        12541, // ORA-12541: TNS:no listener
        12545  // ORA-12545: Connect failed because target host or object does not exist
    ];

    private static readonly HashSet<int> TransientErrors =
    [
        60,    // ORA-00060: deadlock detected while waiting for resource
        12170, // ORA-12170: TNS:Connect timeout occurred
        12541, // ORA-12541: TNS:no listener
        3113,  // ORA-03113: end-of-file on communication channel
        3114   // ORA-03114: not connected to ORACLE
    ];

    /// <inheritdoc />
    public OrmDbExceptionTranslation Translate(Exception exception)
    {
        if (exception is OracleException oraEx)
        {
            int number = oraEx.Number;
            if (number is 1 or 2291 or 2292)
                return new OrmDbExceptionTranslation(OrmErrors.Conflict(), false, $"ORA-{number:D5}");

            if (number is 60 or 54)
                return new OrmDbExceptionTranslation(OrmErrors.Concurrency(), false, $"ORA-{number:D5}");

            if (number is 1013)
                return new OrmDbExceptionTranslation(OrmErrors.Timeout(), true, $"ORA-{number:D5}");

            if (ConnectionErrors.Contains(number))
                return new OrmDbExceptionTranslation(OrmErrors.ConnectionUnavailable(), true, $"ORA-{number:D5}");

            return new OrmDbExceptionTranslation(OrmErrors.Failure(), false, $"ORA-{number:D5}");
        }

        if (exception is TimeoutException)
            return new OrmDbExceptionTranslation(OrmErrors.Timeout(), true, null);

        return new OrmDbExceptionTranslation(OrmErrors.Failure(), false, null);
    }

    /// <inheritdoc />
    public bool IsTransient(Exception exception)
    {
        if (exception is OracleException oraEx)
            return TransientErrors.Contains(oraEx.Number);

        return false;
    }

    /// <inheritdoc />
    public bool IsConnectionFailure(Exception exception)
    {
        if (exception is OracleException oraEx)
            return ConnectionErrors.Contains(oraEx.Number);

        return false;
    }
}
