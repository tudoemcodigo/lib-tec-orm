using System.Globalization;
using MySql.Data.MySqlClient;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.MySql.Dialect;

/// <summary>
/// Tradutor das exceções do MySql.Data (<see cref="MySqlException"/>, também dentro de <c>DbUpdateException</c>) para erros de
/// domínio do TEC.ORM. Concorrência otimista, tempo esgotado e as recusas do TEC.ORM ficam na base
/// (<see cref="OrmExceptionTranslatorBase"/>).
/// </summary>
public sealed class MySqlExceptionTranslator : OrmExceptionTranslatorBase
{
    private const int DuplicateEntry = 1062;
    private const int DuplicateEntryWithKeyName = 1586;

    private static readonly HashSet<int> ConnectionErrors =
    [
        1042, // Unable to connect to any of the specified hosts
        1045, // Access denied (login recusado)
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
    protected override bool IsProviderException(Exception exception) => exception is MySqlException;

    /// <inheritdoc />
    protected override OrmDbExceptionTranslation TranslateProviderException(Exception providerException)
    {
        var mySql = (MySqlException)providerException;
        int number = mySql.Number;
        string code = number.ToString(CultureInfo.InvariantCulture);

        if (number is DuplicateEntry or DuplicateEntryWithKeyName or 1451 or 1452)
            return new OrmDbExceptionTranslation(OrmErrors.Conflict(), false, code);
        if (number is 1213 or 1205)
            return new OrmDbExceptionTranslation(OrmErrors.Concurrency(), false, code);
        // 1317: consulta interrompida; 3024: max_execution_time; 0 + TimeoutException: tempo do comando no cliente
        if (number is 1317 or 3024 or 1801 or 1907 || (number == 0 && mySql.InnerException is TimeoutException))
            return new OrmDbExceptionTranslation(OrmErrors.Timeout(), true, code);
        if (ConnectionErrors.Contains(number))
            return new OrmDbExceptionTranslation(OrmErrors.ConnectionUnavailable(), true, code);
        return new OrmDbExceptionTranslation(OrmErrors.Failure(), false, code);
    }

    /// <inheritdoc />
    protected override bool IsTransientProviderException(Exception providerException) =>
        TransientErrors.Contains(((MySqlException)providerException).Number);

    /// <inheritdoc />
    protected override bool IsConnectionFailureProviderException(Exception providerException) =>
        ConnectionErrors.Contains(((MySqlException)providerException).Number);

    /// <inheritdoc />
    protected override bool IsUniqueViolationProviderException(Exception providerException) =>
        ((MySqlException)providerException).Number is DuplicateEntry or DuplicateEntryWithKeyName;

    /// <inheritdoc />
    protected override string? GetErrorCode(Exception providerException) =>
        ((MySqlException)providerException).Number.ToString(CultureInfo.InvariantCulture);
}
