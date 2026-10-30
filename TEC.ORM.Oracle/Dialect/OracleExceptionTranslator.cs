using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.Oracle.Dialect;

/// <summary>
/// Tradutor de exceções do ODP.NET (<see cref="OracleException"/>, também dentro de <c>DbUpdateException</c>) para erros
/// padronizados do TEC.ORM. Concorrência otimista, tempo esgotado e as recusas do TEC.ORM ficam na base
/// (<see cref="OrmExceptionTranslatorBase"/>).
/// </summary>
public sealed class OracleExceptionTranslator : OrmExceptionTranslatorBase
{
    private const int UniqueViolation = 1;

    private static readonly HashSet<int> ConnectionErrors =
    [
        1017,  // ORA-01017: invalid username/password
        12154, // ORA-12154: TNS:could not resolve the connect identifier specified
        12170, // ORA-12170: TNS:Connect timeout occurred
        12514, // ORA-12514: TNS:listener does not currently know of service requested
        12541, // ORA-12541: TNS:no listener
        12545, // ORA-12545: Connect failed because target host or object does not exist
        3113,  // ORA-03113: end-of-file on communication channel
        3114   // ORA-03114: not connected to ORACLE
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
    protected override bool IsProviderException(Exception exception) => exception is OracleException;

    /// <inheritdoc />
    protected override OrmDbExceptionTranslation TranslateProviderException(Exception providerException)
    {
        int number = ((OracleException)providerException).Number;
        string code = Format(number);
        if (number is UniqueViolation or 2291 or 2292)
            return new OrmDbExceptionTranslation(OrmErrors.Conflict(), false, code);
        if (number is 60 or 54)
            return new OrmDbExceptionTranslation(OrmErrors.Concurrency(), false, code);
        if (number == 1013)
            return new OrmDbExceptionTranslation(OrmErrors.Timeout(), true, code);
        if (ConnectionErrors.Contains(number))
            return new OrmDbExceptionTranslation(OrmErrors.ConnectionUnavailable(), true, code);
        return new OrmDbExceptionTranslation(OrmErrors.Failure(), false, code);
    }

    /// <inheritdoc />
    protected override bool IsTransientProviderException(Exception providerException) =>
        TransientErrors.Contains(((OracleException)providerException).Number);

    /// <inheritdoc />
    protected override bool IsConnectionFailureProviderException(Exception providerException) =>
        ConnectionErrors.Contains(((OracleException)providerException).Number);

    /// <inheritdoc />
    protected override bool IsUniqueViolationProviderException(Exception providerException) =>
        ((OracleException)providerException).Number == UniqueViolation;

    /// <inheritdoc />
    protected override string? GetErrorCode(Exception providerException) => Format(((OracleException)providerException).Number);

    private static string Format(int number) => "ORA-" + number.ToString("D5", CultureInfo.InvariantCulture);
}
