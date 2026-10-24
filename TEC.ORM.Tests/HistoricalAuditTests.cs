using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using TEC.Core.Security;
using TEC.ORM.Auditing;
using TEC.ORM.Entities;
using TEC.ORM.EntityFrameworkCore.Auditing;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests;

public class HistoricalAuditTests
{
    [Auditable]
    public sealed class CustomerProduct : AuditableEntity<int>
    {
        public string Sku { get; set; } = string.Empty;

        [SensitiveData]
        public string SecretApiKey { get; set; } = string.Empty;

        [AuditIgnore]
        public string InternalNote { get; set; } = string.Empty;
    }

    public sealed class NonAuditedItem : Entity<int>
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class HistoricalAuditContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<CustomerProduct> Products => Set<CustomerProduct>();
        public DbSet<NonAuditedItem> NonAuditedItems => Set<NonAuditedItem>();
        public DbSet<OrmAuditRecord> AuditRecords => Set<OrmAuditRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyAuditTrailConfiguration();
        }
    }

    private sealed class TestActor(
        string? id,
        string? tenant,
        PrincipalKind kind = PrincipalKind.User,
        string? actorType = null,
        string? actorName = null,
        string? clientIp = null,
        string? endpoint = null) : ICurrentUser, IOrmAuditActorDetails
    {
        public bool IsAuthenticated => id is not null;
        public PrincipalKind Kind => id is null ? PrincipalKind.Anonymous : kind;
        public string? Id => id;
        public string? TenantId => tenant;
        public string? ActorType => actorType;
        public string? ActorName => actorName ?? id;
        public string? ClientIp => clientIp;
        public string? Endpoint => endpoint;
    }

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static HistoricalAuditContext CreateContext(string dbName, ICurrentUser actor)
    {
        var timeProvider = new FixedTimeProvider(FixedNow);
        var auditContextProvider = new DefaultOrmAuditContextProvider(() => actor);
        var auditInterceptor = new HistoricalAuditInterceptor(auditContextProvider, timeProvider);

        var options = new DbContextOptionsBuilder<HistoricalAuditContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(auditInterceptor)
            .Options;

        return new HistoricalAuditContext(options);
    }

    [Test]
    public async Task Create_audited_entity_records_audit_trail_with_sensitive_data_masked()
    {
        var db = Guid.NewGuid().ToString("N");
        var actor = new TestActor("usr_123", "tenant_alpha", PrincipalKind.User, actorName: "Roberto", clientIp: "192.168.1.10", endpoint: "POST /products");

        await using (var ctx = CreateContext(db, actor))
        {
            var product = new CustomerProduct
            {
                Id = 1,
                Sku = "SKU-999",
                SecretApiKey = "super-secret-key-12345",
                InternalNote = "Don't track this note"
            };

            ctx.Products.Add(product);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = CreateContext(db, actor))
        {
            var audit = await ctx.AuditRecords.FirstOrDefaultAsync();

            await Assert.That(audit).IsNotNull();
            await Assert.That(audit!.EntityName).IsEqualTo(nameof(CustomerProduct));
            await Assert.That(audit.EntityKey).IsEqualTo("1");
            await Assert.That(audit.Action).IsEqualTo("Created");
            await Assert.That(audit.ActorId).IsEqualTo("usr_123");
            await Assert.That(audit.ActorType).IsEqualTo("User");
            await Assert.That(audit.ActorName).IsEqualTo("Roberto");
            await Assert.That(audit.TenantId).IsEqualTo("tenant_alpha");
            await Assert.That(audit.ClientIp).IsEqualTo("192.168.1.10");
            await Assert.That(audit.Endpoint).IsEqualTo("POST /products");
            await Assert.That(audit.TimestampUtc).IsEqualTo(FixedNow.UtcDateTime);

            // SecretApiKey deve estar mascarada com ***
            await Assert.That(audit.NewValues).Contains("***");
            await Assert.That(audit.NewValues).DoesNotContain("super-secret-key-12345");

            // InternalNote deve ter sido ignorada por [AuditIgnore]
            await Assert.That(audit.AffectedColumns).Contains(nameof(CustomerProduct.Sku));
            await Assert.That(audit.AffectedColumns).DoesNotContain(nameof(CustomerProduct.InternalNote));
            await Assert.That(audit.NewValues).DoesNotContain("Don't track this note");
        }
    }

    [Test]
    public async Task Non_audited_entity_does_not_generate_audit_records()
    {
        var db = Guid.NewGuid().ToString("N");
        var actor = new TestActor("usr_123", "tenant_alpha");

        await using (var ctx = CreateContext(db, actor))
        {
            ctx.NonAuditedItems.Add(new NonAuditedItem { Id = 10, Name = "Item X" });
            await ctx.SaveChangesAsync();

            var count = await ctx.AuditRecords.CountAsync();
            await Assert.That(count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Update_audited_entity_records_old_and_new_diffs_for_m2m()
    {
        var db = Guid.NewGuid().ToString("N");
        var actor = new TestActor("service_account_sync", "tenant_corp", PrincipalKind.Application);

        await using (var ctx = CreateContext(db, actor))
        {
            ctx.Products.Add(new CustomerProduct
            {
                Id = 2,
                Sku = "SKU-V1",
                SecretApiKey = "old-secret",
                InternalNote = "note1"
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = CreateContext(db, actor))
        {
            var product = await ctx.Products.FirstAsync(p => p.Id == 2);
            product.Sku = "SKU-V2";
            product.SecretApiKey = "new-secret";
            product.InternalNote = "note2";
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = CreateContext(db, actor))
        {
            var updateRecord = await ctx.AuditRecords
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync(r => r.Action == "Updated");

            await Assert.That(updateRecord).IsNotNull();
            await Assert.That(updateRecord!.ActorType).IsEqualTo("M2M");
            await Assert.That(updateRecord.ActorId).IsEqualTo("service_account_sync");

            // Old e New devem conter os valores anteriores e atuais
            await Assert.That(updateRecord.OldValues).Contains("SKU-V1");
            await Assert.That(updateRecord.NewValues).Contains("SKU-V2");

            // Sensitive data sempre mascarado em ambos os lados
            await Assert.That(updateRecord.OldValues).DoesNotContain("old-secret");
            await Assert.That(updateRecord.NewValues).DoesNotContain("new-secret");

            // Propriedade ignorada
            await Assert.That(updateRecord.AffectedColumns).DoesNotContain(nameof(CustomerProduct.InternalNote));
        }
    }

    [Test]
    public async Task Delete_audited_entity_records_delete_trail_for_apikey()
    {
        var db = Guid.NewGuid().ToString("N");
        var actor = new TestActor("apikey_tenant_client", "tenant_external", PrincipalKind.Application);

        await using (var ctx = CreateContext(db, actor))
        {
            ctx.Products.Add(new CustomerProduct
            {
                Id = 3,
                Sku = "SKU-TO-DELETE",
                SecretApiKey = "secret",
                InternalNote = "n"
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = CreateContext(db, actor))
        {
            var product = await ctx.Products.FirstAsync(p => p.Id == 3);
            ctx.Products.Remove(product);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = CreateContext(db, actor))
        {
            var deleteRecord = await ctx.AuditRecords
                .FirstOrDefaultAsync(r => r.Action == "Deleted");

            await Assert.That(deleteRecord).IsNotNull();
            await Assert.That(deleteRecord!.ActorType).IsEqualTo("ApiKey");
            await Assert.That(deleteRecord.ActorId).IsEqualTo("apikey_tenant_client");
            await Assert.That(deleteRecord.OldValues).Contains("SKU-TO-DELETE");
            await Assert.That(deleteRecord.NewValues).IsNull();
        }
    }

    [Test]
    public async Task Anonymous_actor_is_properly_classified()
    {
        var db = Guid.NewGuid().ToString("N");
        var anonymousActor = new TestActor(null, null, PrincipalKind.Anonymous);

        await using (var ctx = CreateContext(db, anonymousActor))
        {
            ctx.Products.Add(new CustomerProduct
            {
                Id = 4,
                Sku = "SKU-ANON",
                SecretApiKey = "s"
            });
            await ctx.SaveChangesAsync();

            var record = await ctx.AuditRecords.FirstAsync(r => r.EntityKey == "4");
            await Assert.That(record.ActorType).IsEqualTo("Anonymous");
            await Assert.That(record.ActorId).IsNull();
        }
    }

    [Test]
    public async Task TraceId_from_activity_is_captured()
    {
        var db = Guid.NewGuid().ToString("N");
        var actor = new TestActor("usr_trace", "tenant_trace");

        using var activity = new Activity("TestTraceActivity").Start();
        string expectedTraceId = activity.TraceId.ToString();

        await using (var ctx = CreateContext(db, actor))
        {
            ctx.Products.Add(new CustomerProduct
            {
                Id = 5,
                Sku = "SKU-TRACE",
                SecretApiKey = "s"
            });
            await ctx.SaveChangesAsync();

            var record = await ctx.AuditRecords.FirstAsync(r => r.EntityKey == "5");
            await Assert.That(record.TraceId).IsEqualTo(expectedTraceId);
        }
    }
}
