using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Security;
using TEC.ORM.SqlServer.DependencyInjection;

namespace TEC.ORM.Tests;

/// <summary>
/// <c>UseTecOrm</c> (ex.: <c>AddDbContextFactory</c>): o <see cref="ICurrentUser"/> da auditoria é resolvido num escopo próprio a
/// cada gravação e copiado antes de o escopo ser descartado (antes, a auditoria recebia a instância já descartada).
/// </summary>
public class IdentityTests
{
    private static readonly AsyncLocal<string?> Ambient = new();

    /// <summary>Implementação scoped e descartável que lê o usuário ambiente e se recusa a ser usada depois do descarte.</summary>
    private sealed class DisposableUser : ICurrentUser, IDisposable
    {
        private readonly string? _id = Ambient.Value;
        private bool _disposed;

        public PrincipalKind Kind => Read(_id is null ? PrincipalKind.Anonymous : PrincipalKind.User);

        public string? Id => Read(_id);

        public string? TenantId => Read("contoso");

        public void Dispose() => _disposed = true;

        private T Read<T>(T value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return value;
        }
    }

    [Test]
    public async Task Snapshot_is_usable_after_the_scope_is_disposed_and_is_resolved_per_operation()
    {
        var services = new ServiceCollection();
        services.AddScoped<ICurrentUser, DisposableUser>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();

        Ambient.Value = "oid-ana";
        var first = ServiceCollectionExtensions.SnapshotCurrentUser(scopes);
        Ambient.Value = "oid-bia";
        var second = ServiceCollectionExtensions.SnapshotCurrentUser(scopes);

        await Assert.That(first!.Id).IsEqualTo("oid-ana");
        await Assert.That(first.IsAuthenticated).IsTrue();
        await Assert.That(first.TenantId).IsEqualTo("contoso");
        await Assert.That(second!.Id).IsEqualTo("oid-bia");
    }

    [Test]
    public async Task Snapshot_without_registered_user_is_null()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();

        await Assert.That(ServiceCollectionExtensions.SnapshotCurrentUser(provider.GetRequiredService<IServiceScopeFactory>())).IsNull();
    }
}
