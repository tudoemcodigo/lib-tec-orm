using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TEC.Vault.DependencyInjection;
using TEC.Vault.InMemory;
using TEC.Core.Exceptions;
using TEC.ORM.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.DependencyInjection;
using TEC.ORM.SqlServer.Security;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests;

/// <summary>Conexão obtida só do TEC.Vault, política de segurança do segredo e ausência de vazamento em logs e objetos.</summary>
public class SecurityTests
{
    private const string Password = "S3nh@-Ultra-Secreta!";
    private const string ValidSecret =
        $"Server=tcp:db.interno,1433;Database=dbExemplo;User ID=app;Password={Password};Encrypt=True;TrustServerCertificate=False";

    private static (OrmConnectionSecurity Context, CapturingLoggerProvider Logs) Create(Action<OrmOptions>? configure = null,
        params (string Name, string Value)[] secrets)
    {
        var logs = new CapturingLoggerProvider();
        var context = new OrmConnectionSecurity(TestOrm.Secrets(secrets), TestOrm.Options(configure), logs.CreateLogger<OrmConnectionSecurity>());
        return (context, logs);
    }

    [Test]
    public async Task Connection_comes_from_the_vault_with_the_policy_enforced()
    {
        var (security, logs) = Create(o => o.ApplicationName = "Sales", (TestOrm.SecretName, ValidSecret + ";Persist Security Info=True"));
        await using var connection = new SqlConnection();

        var result = await security.ConfigureConnectionAsync(connection, OrmConnectionKind.ReadWrite, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        var applied = new SqlConnectionStringBuilder(connection.ConnectionString);
        await Assert.That(applied.InitialCatalog).IsEqualTo("dbExemplo");
        await Assert.That(applied.PersistSecurityInfo).IsFalse();
        await Assert.That(applied.ApplicationName).IsEqualTo("Sales");
        await Assert.That(applied.CommandTimeout).IsEqualTo(OrmOptions.DefaultCommandTimeoutSeconds);
        await Assert.That(applied.ApplicationIntent).IsEqualTo(ApplicationIntent.ReadWrite);
        await Assert.That(logs.AllText).DoesNotContain(Password);
    }

    [Test]
    public async Task Read_connection_uses_its_own_secret_and_read_only_intent()
    {
        var (security, _) = Create(o => o.ReadOnlyConnectionSecretName = TestOrm.ReadOnlySecretName,
            (TestOrm.SecretName, ValidSecret),
            (TestOrm.ReadOnlySecretName, ValidSecret.Replace("User ID=app", "User ID=leitor", StringComparison.Ordinal)));
        await using var connection = new SqlConnection();

        await security.ConfigureConnectionAsync(connection, OrmConnectionKind.ReadOnly, CancellationToken.None);

        var applied = new SqlConnectionStringBuilder(connection.ConnectionString);
        await Assert.That(applied.UserID).IsEqualTo("leitor");
        await Assert.That(applied.ApplicationIntent).IsEqualTo(ApplicationIntent.ReadOnly);
    }

    [Test]
    public async Task Connection_that_already_has_a_string_is_not_changed()
    {
        var (security, _) = Create(secrets: (TestOrm.SecretName, ValidSecret));
        await using var connection = new SqlConnection("Server=outro;Database=x;Integrated Security=True;Encrypt=True");

        var result = await security.ConfigureConnectionAsync(connection, OrmConnectionKind.ReadWrite, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(connection.ConnectionString).Contains("Server=outro");
    }

    [Test]
    public async Task Connection_derived_by_ef_without_server_gets_the_secret_and_keeps_the_database()
    {
        // O EF Core deriva a conexão com master a partir da principal (que, sem string de conexão, fica só com o banco)
        var (security, _) = Create(secrets: (TestOrm.SecretName, ValidSecret));
        await using var connection = new SqlConnection("Initial Catalog=master;Encrypt=True");

        var result = await security.ConfigureConnectionAsync(connection, OrmConnectionKind.ReadWrite, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        var applied = new SqlConnectionStringBuilder(connection.ConnectionString);
        await Assert.That(applied.DataSource).IsEqualTo("tcp:db.interno,1433");
        await Assert.That(applied.InitialCatalog).IsEqualTo("master");
    }

    [Test]
    public async Task Missing_secret_becomes_connection_unavailable_without_exposing_the_name()
    {
        var (security, logs) = Create();
        await using var connection = new SqlConnection();

        var result = await security.ConfigureConnectionAsync(connection, OrmConnectionKind.ReadWrite, CancellationToken.None);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConnectionUnavailableCode);
        await Assert.That(result.Error.Message).DoesNotContain(TestOrm.SecretName);
        await Assert.That(connection.ConnectionString).IsEmpty();
        await Assert.That(logs.AllText).Contains("indisponível no cofre");
    }

    [Test]
    [Arguments("isto não é = uma; conexão ;; = válida " + Password, "formato inválido")]
    [Arguments("Server=db;User ID=app;Password=" + Password + ";Encrypt=True", "banco de dados não informado")]
    [Arguments("Database=dbExemplo;User ID=app;Password=" + Password + ";Encrypt=True", "servidor não informado")]
    [Arguments("Server=db;Database=dbExemplo;User ID=app;Password=" + Password + ";Encrypt=False", "criptografia desabilitada")]
    [Arguments("Server=db;Database=dbExemplo;User ID=app;Password=" + Password + ";Encrypt=True;TrustServerCertificate=True", "TrustServerCertificate")]
    public async Task Secret_outside_the_policy_is_rejected_without_leaking(string secret, string reason)
    {
        var (security, logs) = Create(secrets: (TestOrm.SecretName, secret));
        await using var connection = new SqlConnection();

        var result = await security.ConfigureConnectionAsync(connection, OrmConnectionKind.ReadWrite, CancellationToken.None);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.InvalidConnectionSecretCode);
        await Assert.That(result.Error.Message).DoesNotContain(Password);
        await Assert.That(connection.ConnectionString).IsEmpty();
        await Assert.That(logs.AllText).Contains(reason);
        await Assert.That(logs.AllText).DoesNotContain(Password);
    }

    [Test]
    public async Task TrustServerCertificate_only_with_explicit_permission_and_warns()
    {
        var (security, logs) = Create(o => o.AllowTrustServerCertificate = true,
            (TestOrm.SecretName, ValidSecret.Replace("TrustServerCertificate=False", "TrustServerCertificate=True", StringComparison.Ordinal)));
        await using var connection = new SqlConnection();

        var result = await security.ConfigureConnectionAsync(connection, OrmConnectionKind.ReadWrite, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(logs.Entries.Any(e => e.Level == LogLevel.Warning && e.Text.Contains("TrustServerCertificate"))).IsTrue();
    }

    [Test]
    public async Task Open_failure_does_not_log_server_user_or_password()
    {
        Skip.When(TestDatabase.GlobalizationInvariant, TestDatabase.SqlClientRequiresIcu);

        // Porta 1 em loopback: recusa imediata, sem depender de rede
        var (security, logs) = Create(o => o.AllowTrustServerCertificate = true, (TestOrm.SecretName,
            $"Server=tcp:127.0.0.1,1;Database=dbExemplo;User ID=usuario-secreto;Password={Password};Encrypt=True;TrustServerCertificate=True;Connect Timeout=2"));

        var result = await security.OpenConnectionAsync(OrmConnectionKind.ReadWrite, CancellationToken.None);

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.ConnectionUnavailableCode);
        await Assert.That(logs.AllText).DoesNotContain(Password);
        await Assert.That(logs.AllText).DoesNotContain("usuario-secreto");
        await Assert.That(logs.AllText).DoesNotContain("127.0.0.1");
    }

    [Test]
    public async Task Protected_connection_string_masks_the_value()
    {
        var (security, _) = Create(secrets: (TestOrm.SecretName, ValidSecret));

        var built = await security.BuildConnectionStringAsync(OrmConnectionKind.ReadWrite, CancellationToken.None);

        await Assert.That(built.Value.ToString()).IsEqualTo("***");
        await Assert.That($"{built.Value}").DoesNotContain(Password);
        await Assert.That(built.Value.Reveal()).Contains(Password);
    }

    // ---------- Registro no DI: a conexão nunca passa pela configuração do EF ----------

    [Test]
    public async Task Registered_DbContext_has_no_connection_string_and_does_not_log_sensitive_data()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecVault(c => c.UseInMemory(o =>
        {
            o.AllowOutsideDevelopment = true;
            o.InitialSecrets[TestOrm.SecretName] = ValidSecret;
        }));
        services.AddTecOrm<CodeFirstContext>(o => o.ConnectionSecretName = TestOrm.SecretName);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<CodeFirstContext>();
        var core = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!;

        await Assert.That(string.IsNullOrEmpty(context.Database.GetConnectionString())).IsTrue();
        await Assert.That(core.IsSensitiveDataLoggingEnabled).IsFalse();
        await Assert.That(scope.ServiceProvider.GetService<IOrmRepository<Customer, Guid>>()).IsNotNull();
        await Assert.That(scope.ServiceProvider.GetService<IOrmQueryExecutor>()).IsNotNull();
        await Assert.That(scope.ServiceProvider.GetService<TEC.Cqrs.Persistence.IUnitOfWork>()).IsNotNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Options_without_secret_name_are_rejected_at_registration(string secretName)
    {
        var services = new ServiceCollection();
        await Assert.That(() => services.AddTecOrm<CodeFirstContext>(o => o.ConnectionSecretName = secretName))
            .Throws<InvalidConfigurationException>();
    }

    [Test]
    public async Task Out_of_range_options_are_rejected()
    {
        await Assert.That(() => TestOrm.Options(o => o.MaxPageSize = 0)).Throws<InvalidConfigurationException>();
        await Assert.That(() => TestOrm.Options(o => o.CommandTimeoutSeconds = 0)).Throws<InvalidConfigurationException>();
        await Assert.That(() => TestOrm.Options(o => o.ReadOnlyConnectionSecretName = " ")).Throws<InvalidConfigurationException>();
    }
}
