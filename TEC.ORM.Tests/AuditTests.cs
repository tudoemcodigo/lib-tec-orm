using Microsoft.EntityFrameworkCore;
using TEC.Core.Security;
using TEC.ORM.Common;
using TEC.ORM.Entities;
using TEC.ORM.SqlServer.Auditing;
using TEC.ORM.SqlServer.SoftDelete;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests;

/// <summary>
/// Auditoria (CriadoPor/AlteradoPor/ExcluídoPor e o tenant de cada um) a partir do ICurrentUser (TEC.Core). Os registros são
/// globais: o tenant só é gravado, nunca filtra nem restringe.
/// </summary>
public class AuditTests
{
    public sealed class Note : AuditableEntity<int>
    {
        public string Text { get; set; } = string.Empty;
    }

    private sealed class AuditContext(DbContextOptions options) : OrmDbContext(options)
    {
        public DbSet<Note> Notes => Set<Note>();
    }

    /// <summary>Contexto que não herda de OrmDbContext e chama AddTecOrmConventions.</summary>
    private sealed class ConventionsContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<Note> Notes => Set<Note>();

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
            configurationBuilder.AddTecOrmConventions();
    }

    private sealed class AppUser(string? id, string? tenant, PrincipalKind kind = PrincipalKind.User) : ICurrentUser
    {
        public bool IsAuthenticated => id is not null;

        public PrincipalKind Kind => id is null ? PrincipalKind.Anonymous : kind;

        public string? Id => id;

        public string? TenantId => tenant;
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static AuditContext Context(string database, ICurrentUser? user, DateTimeOffset? now = null)
    {
        var time = new FixedTimeProvider(now ?? Now);
        return new AuditContext(new DbContextOptionsBuilder<AuditContext>()
            .UseInMemoryDatabase(database)
            .AddInterceptors(new SoftDeleteInterceptor(time), new AuditInterceptor(time))
            .UseTecOrmIdentity(() => user)
            .Options);
    }

    private static readonly AppUser Ana = new("oid-ana", "contoso");
    private static readonly AppUser Bia = new("oid-bia", "fabrikam");

    // ---------------------------------------------------------------- autoria

    [Test]
    public async Task Creation_fills_authorship_and_tenant_ignoring_the_object_values()
    {
        await using var context = Context(Guid.NewGuid().ToString("N"), Ana);
        var entry = context.Notes.Add(new Note { Id = 1, Text = "a" });
        entry.Property(nameof(IAuditable.CreatedBy)).CurrentValue = "oid-forjado";
        entry.Property(nameof(IAuditable.CreatedByTenant)).CurrentValue = "forjado";
        entry.Property(nameof(IAuditable.UpdatedByTenant)).CurrentValue = "forjado";
        await context.SaveChangesAsync();

        var note = entry.Entity;
        await Assert.That(note.CreatedBy).IsEqualTo("oid-ana");
        await Assert.That(note.CreatedByTenant).IsEqualTo("contoso");
        await Assert.That(note.CreatedAt).IsEqualTo(Now);
        await Assert.That(note.UpdatedAt).IsNull();
        await Assert.That(note.UpdatedByTenant).IsNull();
        await Assert.That(note.DeletedBy).IsNull();
        await Assert.That(note.DeletedByTenant).IsNull();
    }

    [Test]
    public async Task Without_identity_the_write_is_rejected_and_nothing_is_saved()
    {
        string db = Guid.NewGuid().ToString("N");
        await using (var anonymous = Context(db, new AppUser(null, null)))
        {
            var result = await TestOrm.Orm<Note, int>(anonymous).CreateAsync(new Note { Id = 1, Text = "x" });

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.AuditIdentityRequiredCode);
        }

        await using var context = Context(db, Ana);
        await Assert.That(await context.Notes.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task Without_configured_ICurrentUser_audited_entity_is_not_saved()
    {
        await using var context = Context(Guid.NewGuid().ToString("N"), user: null);

        var result = await TestOrm.Orm<Note, int>(context).CreateAsync(new Note { Id = 1 });

        await Assert.That(result.Error!.Code).IsEqualTo(OrmErrors.AuditIdentityRequiredCode);
    }

    [Test]
    public async Task Identity_without_tenant_saves_null_tenant()
    {
        string db = Guid.NewGuid().ToString("N");
        var job = new AppUser("system:job", null, PrincipalKind.System);
        await using (var creation = Context(db, job))
            await Assert.That((await TestOrm.Orm<Note, int>(creation).CreateAsync(new Note { Id = 1 })).IsSuccess).IsTrue();

        await using var reading = Context(db, Ana);
        var note = await reading.Notes.AsNoTracking().SingleAsync();
        await Assert.That(note.CreatedBy).IsEqualTo("system:job");
        await Assert.That(note.CreatedByTenant).IsNull();
    }

    [Test]
    public async Task Update_records_who_updated_and_preserves_the_creation()
    {
        string db = Guid.NewGuid().ToString("N");
        await using (var creation = Context(db, Ana))
            await TestOrm.Orm<Note, int>(creation).CreateAsync(new Note { Id = 1, Text = "v1" });

        await using (var update = Context(db, new AppUser("oid-carlos", "contoso"), Now.AddHours(1)))
        {
            var repo = TestOrm.Orm<Note, int>(update);
            var note = (await repo.GetByIdAsync(1)).Value;
            note.Text = "v2";
            await repo.UpdateAsync(note);
        }

        await using var reading = Context(db, Ana);
        var saved = await reading.Notes.AsNoTracking().SingleAsync();
        await Assert.That(saved.Text).IsEqualTo("v2");
        await Assert.That(saved.CreatedBy).IsEqualTo("oid-ana");
        await Assert.That(saved.CreatedByTenant).IsEqualTo("contoso");
        await Assert.That(saved.CreatedAt).IsEqualTo(Now);
        await Assert.That(saved.UpdatedBy).IsEqualTo("oid-carlos");
        await Assert.That(saved.UpdatedByTenant).IsEqualTo("contoso");
        await Assert.That(saved.UpdatedAt).IsEqualTo(Now.AddHours(1));
    }

    [Test]
    public async Task Forged_authorship_in_the_object_is_not_saved_on_update()
    {
        string db = Guid.NewGuid().ToString("N");
        await using (var creation = Context(db, Ana))
            await TestOrm.Orm<Note, int>(creation).CreateAsync(new Note { Id = 1, Text = "v1" });

        await using (var update = Context(db, Bia))
        {
            var entry = update.Notes.Attach(new Note { Id = 1, Text = "v2" });
            entry.Property(nameof(IAuditable.CreatedBy)).CurrentValue = "oid-forjado";
            entry.Property(nameof(IAuditable.CreatedByTenant)).CurrentValue = "forjado";
            entry.Property(nameof(IAuditable.DeletedBy)).CurrentValue = "oid-forjado";
            entry.Property(nameof(IAuditable.DeletedByTenant)).CurrentValue = "forjado";
            entry.State = EntityState.Modified;
            await update.SaveChangesAsync();
        }

        await using var reading = Context(db, Ana);
        var saved = await reading.Notes.AsNoTracking().SingleAsync();
        await Assert.That(saved.CreatedBy).IsEqualTo("oid-ana");
        await Assert.That(saved.CreatedByTenant).IsEqualTo("contoso");
        await Assert.That(saved.DeletedBy).IsNull();
        await Assert.That(saved.DeletedByTenant).IsNull();
        await Assert.That(saved.UpdatedByTenant).IsEqualTo("fabrikam");
    }

    [Test]
    public async Task Soft_delete_records_who_deleted()
    {
        string db = Guid.NewGuid().ToString("N");
        await using (var creation = Context(db, Ana))
            await TestOrm.Orm<Note, int>(creation).CreateAsync(new Note { Id = 1 });

        await using (var deletion = Context(db, new AppUser("oid-gerente", "contoso"), Now.AddDays(1)))
            await Assert.That((await TestOrm.Orm<Note, int>(deletion).DeleteAsync(1)).IsSuccess).IsTrue();

        await using var reading = Context(db, Ana);
        var note = await reading.Notes.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        await Assert.That(note.IsDeleted).IsTrue();
        await Assert.That(note.DeletedBy).IsEqualTo("oid-gerente");
        await Assert.That(note.DeletedByTenant).IsEqualTo("contoso");
        await Assert.That(note.DeletedAt).IsEqualTo(Now.AddDays(1));
        await Assert.That(note.UpdatedBy).IsNull();
        await Assert.That(note.UpdatedByTenant).IsNull();
    }

    // ---------------------------------------------------------------- registros globais

    [Test]
    public async Task Records_are_global_across_tenants()
    {
        string db = Guid.NewGuid().ToString("N");
        await using (var a = Context(db, Ana))
            await TestOrm.Orm<Note, int>(a).CreateAsync(new Note { Id = 1, Text = "original" });

        await using (var b = Context(db, Bia, Now.AddHours(1)))
        {
            var repo = TestOrm.Orm<Note, int>(b);
            await Assert.That((await repo.CountAsync()).Value).IsEqualTo(1);
            var note = (await repo.GetByIdAsync(1)).Value;
            note.Text = "alterado pela fabrikam";
            await Assert.That((await repo.UpdateAsync(note)).IsSuccess).IsTrue();
        }

        await using (var b = Context(db, Bia, Now.AddHours(2)))
            await Assert.That((await TestOrm.Orm<Note, int>(b).DeleteAsync(1)).IsSuccess).IsTrue();

        await using var reading = Context(db, Ana);
        var saved = await reading.Notes.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        await Assert.That(saved.Text).IsEqualTo("alterado pela fabrikam");
        await Assert.That(saved.CreatedByTenant).IsEqualTo("contoso");
        await Assert.That(saved.UpdatedBy).IsEqualTo("oid-bia");
        await Assert.That(saved.UpdatedByTenant).IsEqualTo("fabrikam");
        await Assert.That(saved.DeletedBy).IsEqualTo("oid-bia");
        await Assert.That(saved.DeletedByTenant).IsEqualTo("fabrikam");
    }

    [Test]
    public async Task Model_without_tenant_filter_and_audit_columns_with_length()
    {
        await using var context = new ConventionsContext(new DbContextOptionsBuilder<ConventionsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var entityType = context.Model.FindEntityType(typeof(Note))!;

        await Assert.That(SoftDeleteQueryFilterConvention.HasSoftDeleteFilter(entityType)).IsTrue();
#if NET10_0_OR_GREATER
        await Assert.That(entityType.GetDeclaredQueryFilters().Count()).IsEqualTo(1);
#endif
        foreach (string name in new[] { nameof(IAuditable.CreatedBy), nameof(IAuditable.UpdatedBy), nameof(IAuditable.DeletedBy) })
            await Assert.That(entityType.FindProperty(name)!.GetMaxLength()).IsEqualTo(256);
        foreach (string name in new[] { nameof(IAuditable.CreatedByTenant), nameof(IAuditable.UpdatedByTenant), nameof(IAuditable.DeletedByTenant) })
        {
            var property = entityType.FindProperty(name)!;
            await Assert.That(property.GetMaxLength()).IsEqualTo(64);
            await Assert.That(property.IsNullable).IsTrue();
            await Assert.That(property.IsConcurrencyToken).IsFalse();
        }
    }
}
