using System.Diagnostics;
using System.Reflection;
using TEC.ORM.EntityFrameworkCore.Security;

namespace TEC.ORM.Tests;

public class SecurityConnectionStringTests
{
    private const string RealConnectionString = "Server=tcp:sqlserver.corp.internal,1433;Database=Financas;User Id=superadmin;Password=MegaSecretPassword123!#;";

    [Test]
    public async Task ToString_returns_masked_value_never_the_secret()
    {
        var protectedConn = new ProtectedConnectionString(RealConnectionString);

        await Assert.That(protectedConn.ToString()).IsEqualTo("***");
        await Assert.That(protectedConn.ToString()).DoesNotContain("MegaSecretPassword123!#");
        await Assert.That(protectedConn.ToString()).DoesNotContain("superadmin");
        await Assert.That(protectedConn.ToString()).DoesNotContain("Financas");
    }

    [Test]
    public async Task DebuggerDisplay_is_configured_to_masked()
    {
        var type = typeof(ProtectedConnectionString);
        var debuggerDisplayAttr = type.GetCustomAttribute<DebuggerDisplayAttribute>();

        await Assert.That(debuggerDisplayAttr).IsNotNull();
        await Assert.That(debuggerDisplayAttr!.Value).IsEqualTo("***");
    }

    [Test]
    public async Task Unprotect_returns_the_original_connection_string()
    {
        var protectedConn = new ProtectedConnectionString(RealConnectionString);

        string retrieved = protectedConn.Unprotect();

        await Assert.That(retrieved).IsEqualTo(RealConnectionString);
    }

    [Test]
    public async Task Empty_or_null_values_are_handled_safely()
    {
        var empty = new ProtectedConnectionString(string.Empty);
        await Assert.That(empty.ToString()).IsEqualTo("***");
        await Assert.That(empty.Unprotect()).IsEqualTo(string.Empty);

        var nullStr = new ProtectedConnectionString(null!);
        await Assert.That(nullStr.ToString()).IsEqualTo("***");
        await Assert.That(nullStr.Unprotect()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Reflection_properties_do_not_expose_unmasked_secret()
    {
        var protectedConn = new ProtectedConnectionString(RealConnectionString);
        var type = typeof(ProtectedConnectionString);

        // Nenhuma propriedade pública deve retornar a connection string em texto plano
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = prop.GetValue(protectedConn);
            if (value is string strVal)
            {
                await Assert.That(strVal).DoesNotContain("MegaSecretPassword123!#");
            }
        }
    }
}
