using Npgsql;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.PostgreSql.Dialect;

/// <summary>
/// Tradutor de exceções do Npgsql (<see cref="NpgsqlException"/> e <see cref="PostgresException"/>, também dentro de
/// <c>DbUpdateException</c>) para erros de domínio do TEC.ORM. Concorrência otimista, tempo esgotado e as recusas do TEC.ORM
/// ficam na base (<see cref="OrmExceptionTranslatorBase"/>).
/// </summary>
public sealed class PostgreSqlExceptionTranslator : OrmExceptionTranslatorBase
{
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";

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
    protected override bool IsProviderException(Exception exception) => exception is NpgsqlException;

    /// <inheritdoc />
    protected override OrmDbExceptionTranslation TranslateProviderException(Exception providerException)
    {
        if (providerException is PostgresException postgres)
        {
            string state = postgres.SqlState;
            if (state is UniqueViolation or ForeignKeyViolation)
                return new OrmDbExceptionTranslation(OrmErrors.Conflict(), false, state);
            if (state is "40P01" or "40001")
                return new OrmDbExceptionTranslation(OrmErrors.Concurrency(), false, state);
            if (state == "57014")
                return new OrmDbExceptionTranslation(OrmErrors.Timeout(), true, state);
            if (ConnectionStates.Contains(state))
                return new OrmDbExceptionTranslation(OrmErrors.ConnectionUnavailable(), true, state);
            return new OrmDbExceptionTranslation(OrmErrors.Failure(), false, state);
        }

        // Erro do cliente (rede, pool, tempo esgotado), sem SQLSTATE
        if (providerException.InnerException is TimeoutException)
            return new OrmDbExceptionTranslation(OrmErrors.Timeout(), true, null);
        return new OrmDbExceptionTranslation(OrmErrors.ConnectionUnavailable(), true, null);
    }

    /// <inheritdoc />
    protected override bool IsTransientProviderException(Exception providerException) =>
        providerException is PostgresException postgres ? TransientStates.Contains(postgres.SqlState) : providerException is NpgsqlException;

    /// <inheritdoc />
    protected override bool IsConnectionFailureProviderException(Exception providerException) =>
        providerException is PostgresException postgres ? ConnectionStates.Contains(postgres.SqlState) : providerException is NpgsqlException;

    /// <inheritdoc />
    protected override bool IsUniqueViolationProviderException(Exception providerException) =>
        providerException is PostgresException { SqlState: UniqueViolation };

    /// <inheritdoc />
    protected override string? GetErrorCode(Exception providerException) => (providerException as PostgresException)?.SqlState;
}
