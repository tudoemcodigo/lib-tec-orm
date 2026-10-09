using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TEC.Cqrs.Idempotency;
using TEC.ORM.SqlServer.Idempotency;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests.Integration;

/// <summary>Contexto só com a tabela de idempotência (schema próprio), para os testes do <see cref="EfIdempotencyStore{TContext}"/>.</summary>
public sealed class IdempotencyTestContext(DbContextOptions<IdempotencyTestContext> options) : DbContext(options)
{
    public const string Schema = "idempotencia";

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddTecIdempotency(Schema);
}

/// <summary><see cref="EfIdempotencyStore{TContext}"/> contra o SQL Server real: reserva atômica, conclusão, expiração e limpeza.</summary>
[Category(TestCategories.Integration)]
public class IdempotencyIntegrationTests
{
    private static readonly SemaphoreSlim TableLock = new(1, 1);
    private static bool _tableReady;

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static IdempotencyKey NewKey() => new("usuario-" + Guid.NewGuid().ToString("N")[..8], Guid.NewGuid().ToString("N"));

    private static IdempotencyRequest Request(IdempotencyKey key, string hash = "H1", int lockSeconds = 60) =>
        new(key, hash, TimeSpan.FromHours(1), TimeSpan.FromSeconds(lockSeconds));

    private static IdempotentResponse Response() =>
        new(201, "application/json", new Dictionary<string, string> { ["Location"] = "/pedidos/1" }, "{\"id\":1}"u8.ToArray());

    private static async Task<ServiceProvider> BuildAsync()
    {
        await SqlServerFixture.RequireAsync();
        var provider = SqlServerFixture.Build<IdempotencyTestContext>(new CapturingLoggerProvider());
        await EnsureTableAsync(provider);
        return provider;
    }

    /// <summary>Cria o schema e a tabela pelo script do próprio modelo, uma vez por execução.</summary>
    private static async Task EnsureTableAsync(IServiceProvider provider)
    {
        await TableLock.WaitAsync();
        try
        {
            if (_tableReady)
                return;
            await using var scope = provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<IdempotencyTestContext>();
            int exists = await context.Database.SqlQueryRaw<int>(
                $"SELECT COUNT(*) AS [Value] FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = '{IdempotencyTestContext.Schema}' AND t.name = '{IdempotencyModelBuilderExtensions.DefaultTableName}'")
                .SingleAsync();
            if (exists == 0)
            {
                foreach (string batch in TestDatabase.SplitBatches(context.Database.GenerateCreateScript()))
                {
#pragma warning disable EF1002 // script gerado pelo próprio modelo de testes
                    await context.Database.ExecuteSqlRawAsync(batch);
#pragma warning restore EF1002
                }
            }

            _tableReady = true;
        }
        finally
        {
            TableLock.Release();
        }
    }

    private static EfIdempotencyStore<IdempotencyTestContext> Store(IServiceScope scope, TimeProvider? clock = null) =>
        new(scope.ServiceProvider.GetRequiredService<IdempotencyTestContext>(), clock ?? TimeProvider.System);

    [Test]
    public async Task Begin_complete_replay_and_mismatch()
    {
        await using var provider = await BuildAsync();
        await using var scope = provider.CreateAsyncScope();
        var store = Store(scope);
        var key = NewKey();

        var begin = await store.TryBeginAsync(Request(key), CancellationToken.None);
        var concurrent = await store.TryBeginAsync(Request(key), CancellationToken.None);
        var completed = await store.CompleteAsync(key, begin.LockId, Response(), CancellationToken.None);
        var replay = await store.TryBeginAsync(Request(key), CancellationToken.None);
        var mismatch = await store.TryBeginAsync(Request(key, "H2"), CancellationToken.None);

        await Assert.That(begin.Status).IsEqualTo(IdempotencyBeginStatus.Started);
        await Assert.That(concurrent.Status).IsEqualTo(IdempotencyBeginStatus.InProgress);
        await Assert.That(completed).IsTrue();
        await Assert.That(replay.Status).IsEqualTo(IdempotencyBeginStatus.Completed);
        await Assert.That(replay.Response!.StatusCode).IsEqualTo(201);
        await Assert.That(replay.Response.Headers["location"]).IsEqualTo("/pedidos/1");
        await Assert.That(System.Text.Encoding.UTF8.GetString(replay.Response.Body.Span)).IsEqualTo("{\"id\":1}");
        await Assert.That(mismatch.Status).IsEqualTo(IdempotencyBeginStatus.Mismatch);
    }

    [Test]
    public async Task Parallel_reservations_start_exactly_once()
    {
        await using var provider = await BuildAsync();
        var key = NewKey();

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
        {
            await using var scope = provider.CreateAsyncScope();
            return (await Store(scope).TryBeginAsync(Request(key), CancellationToken.None)).Status;
        }));

        await Assert.That(results.Count(s => s == IdempotencyBeginStatus.Started)).IsEqualTo(1);
        await Assert.That(results.Count(s => s == IdempotencyBeginStatus.InProgress)).IsEqualTo(15);
    }

    [Test]
    public async Task Abandon_releases_and_expired_lock_is_taken_over()
    {
        await using var provider = await BuildAsync();
        await using var scope = provider.CreateAsyncScope();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = Store(scope, clock);
        var key = NewKey();

        var first = await store.TryBeginAsync(Request(key), CancellationToken.None);
        await Assert.That(await store.AbandonAsync(key, first.LockId, CancellationToken.None)).IsTrue();
        var second = await store.TryBeginAsync(Request(key, lockSeconds: 10), CancellationToken.None);

        clock.Now = clock.Now.AddSeconds(11);
        var third = await store.TryBeginAsync(Request(key, "H2", lockSeconds: 10), CancellationToken.None);

        await Assert.That(second.Status).IsEqualTo(IdempotencyBeginStatus.Started);
        await Assert.That(third.Status).IsEqualTo(IdempotencyBeginStatus.Started);
        await Assert.That(await store.CompleteAsync(key, second.LockId, Response(), CancellationToken.None)).IsFalse();
        await Assert.That(await store.CompleteAsync(key, third.LockId, Response(), CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task Purge_removes_only_expired_keys()
    {
        await using var provider = await BuildAsync();
        await using var scope = provider.CreateAsyncScope();
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddYears(-1));
        var oldStore = Store(scope, clock);
        var expired = NewKey();
        var begin = await oldStore.TryBeginAsync(Request(expired), CancellationToken.None);
        await oldStore.CompleteAsync(expired, begin.LockId, Response(), CancellationToken.None);

        var store = Store(scope);
        var alive = NewKey();
        await store.TryBeginAsync(Request(alive), CancellationToken.None);

        await store.PurgeExpiredAsync(10_000, CancellationToken.None);

        await Assert.That((await store.TryBeginAsync(Request(expired, "H9"), CancellationToken.None)).Status).IsEqualTo(IdempotencyBeginStatus.Started);
        await Assert.That((await store.TryBeginAsync(Request(alive), CancellationToken.None)).Status).IsEqualTo(IdempotencyBeginStatus.InProgress);
    }

    [Test]
    public async Task Store_never_saves_pending_changes_of_the_context()
    {
        await using var provider = await BuildAsync();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdempotencyTestContext>();
        var store = Store(scope);
        var key = NewKey();

        await store.TryBeginAsync(Request(key), CancellationToken.None);

        await Assert.That(context.ChangeTracker.Entries().Count()).IsEqualTo(0);
    }
}
