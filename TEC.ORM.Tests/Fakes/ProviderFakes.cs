using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using TEC.Core.Common.Results;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Security;
using TEC.ORM.SqlServer;

namespace TEC.ORM.Tests.Fakes;

/// <summary>
/// Exceções dos drivers MySql.Data e ODP.NET, que não têm construtor público: criadas pelos construtores internos (só nos
/// testes). Quem usa confere o <c>Number</c> resultante antes de afirmar sobre a tradução.
/// </summary>
internal static class ProviderExceptionFactory
{
    public static global::MySql.Data.MySqlClient.MySqlException MySql(int number) =>
        (global::MySql.Data.MySqlClient.MySqlException)typeof(global::MySql.Data.MySqlClient.MySqlException)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string), typeof(int)])!
            .Invoke(["erro simulado", number]);

    // A sobrecarga (int, string, string, string, Exception) do ODP.NET 23.x desreferencia a exceção interna e lança
    // NullReferenceException com null; a de parseErrorOffset monta a mesma OracleError sem exceção interna.
    public static global::Oracle.ManagedDataAccess.Client.OracleException Oracle(int number) =>
        (global::Oracle.ManagedDataAccess.Client.OracleException)typeof(global::Oracle.ManagedDataAccess.Client.OracleException)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(int), typeof(string), typeof(string), typeof(string), typeof(int)])!
            .Invoke([number, "teste", "teste", "erro simulado", 0]);
}

/// <summary>Exceção de banco genérica (não reconhecida por nenhum dialeto).</summary>
internal sealed class FakeDbException(string message) : DbException(message);

/// <summary>
/// Conexão que não fala com banco: registra os comandos executados (com a transação de cada um) e roda <see cref="OnOpen"/> na
/// abertura, para simular falhas.
/// </summary>
internal sealed class RecordingConnection : DbConnection
{
    private ConnectionState _state;

    public RecordingConnection(ConnectionState state = ConnectionState.Open) => _state = state;

    public List<(string Sql, DbTransaction? Transaction)> Executed { get; } = [];

    public Func<CancellationToken, Task>? OnOpen { get; set; }

    public RecordingTransaction? LastTransaction { get; private set; }

    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => "teste";

    public override string DataSource => "teste";

    public override string ServerVersion => "0";

    public override ConnectionState State => _state;

    public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (OnOpen is not null)
            await OnOpen(cancellationToken);
        _state = ConnectionState.Open;
    }

    public override void Close() => _state = ConnectionState.Closed;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => LastTransaction = new RecordingTransaction(this);

    protected override DbCommand CreateDbCommand() => new RecordingCommand(this);
}

/// <summary>Comando que só registra o texto executado.</summary>
internal sealed class RecordingCommand(RecordingConnection connection) : DbCommand
{
    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; } = connection;

    protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery()
    {
        connection.Executed.Add((CommandText, DbTransaction));
        return 0;
    }

    public override object? ExecuteScalar() => ExecuteNonQuery();

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
}

/// <summary>Transação que só registra se foi confirmada ou desfeita (o descarte sem confirmação desfaz, como nos drivers).</summary>
internal sealed class RecordingTransaction(RecordingConnection connection) : DbTransaction
{
    public bool Committed { get; private set; }

    public bool RolledBack { get; private set; }

    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    protected override DbConnection DbConnection => connection;

    public override void Commit() => Committed = true;

    public override void Rollback() => RolledBack = true;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !Committed)
            RolledBack = true;
        base.Dispose(disposing);
    }
}

/// <summary>Dialeto do SQL Server que entrega a <see cref="RecordingConnection"/> informada (testes da abertura de conexão).</summary>
internal sealed class ScriptedDialect(RecordingConnection connection) : ISqlDialect
{
    private readonly SqlServerDialect _inner = new();

    public string ProviderName => _inner.ProviderName;

    public string HealthCheckProbeQuery => _inner.HealthCheckProbeQuery;

    public IReadOnlySqlValidator ReadOnlyValidator => _inner.ReadOnlyValidator;

    public IOrmExceptionTranslator ExceptionTranslator => _inner.ExceptionTranslator;

    public string DelimitIdentifier(string identifier) => _inner.DelimitIdentifier(identifier);

    public string FormatAtomicIdempotencyInsert(string tableName) => _inner.FormatAtomicIdempotencyInsert(tableName);

    public DbConnection CreateConnection(ProtectedConnectionString connectionString) => connection;

    public Result<ProtectedConnectionString> SanitizeAndValidateConnectionString(string rawConnectionString, bool isReadOnly,
        bool allowTrustServerCertificate, string applicationName, int commandTimeoutSeconds) =>
        _inner.SanitizeAndValidateConnectionString(rawConnectionString, isReadOnly, allowTrustServerCertificate, applicationName,
            commandTimeoutSeconds);
}
