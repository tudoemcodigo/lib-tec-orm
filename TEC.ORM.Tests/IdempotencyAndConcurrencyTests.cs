using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Idempotency;
using TEC.Cqrs.Validation;
using TEC.ORM.Common;
using TEC.ORM.SqlServer.DependencyInjection;
using TEC.ORM.SqlServer.Idempotency;
using TEC.ORM.SqlServer.Internal;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests;

[AllowAnonymousRequest]
[SkipValidation]
[TEC.Cqrs.Persistence.SkipTransaction]
public sealed record ThrowingCommand(int Kind) : ICommand;

internal sealed class ThrowingHandler : ICommandHandler<ThrowingCommand>
{
    public Task<Result> Handle(ThrowingCommand command, CancellationToken cancellationToken) => command.Kind switch
    {
        1 => throw new DbUpdateConcurrencyException("linha alterada"),
        2 => throw new DbUpdateException("chave", SqlExceptionFactory.Create(2627)),
        _ => throw new InvalidOperationException("outra")
    };
}

public class OrmExceptionErrorMapperTests
{
    private static readonly OrmExceptionErrorMapper Mapper = new();

    [Test]
    public async Task Optimistic_concurrency_and_deadlock_map_to_ORM_CONCORRENCIA()
    {
        await Assert.That(Mapper.TryMap(new DbUpdateConcurrencyException("x"), out var concurrency)).IsTrue();
        await Assert.That(Mapper.TryMap(new DbUpdateException("x", SqlExceptionFactory.Create(1205)), out var deadlock)).IsTrue();

        await Assert.That(concurrency![0].Code).IsEqualTo(OrmErrors.ConcurrencyCode);
        await Assert.That(concurrency[0].Type).IsEqualTo(ErrorType.Conflict);
        await Assert.That(deadlock![0].Code).IsEqualTo(OrmErrors.ConcurrencyCode);
    }

    [Test]
    [Arguments(2627)]
    [Arguments(2601)]
    [Arguments(547)]
    public async Task Key_violations_map_to_ORM_CONFLITO(int number)
    {
        await Assert.That(Mapper.TryMap(new DbUpdateException("x", SqlExceptionFactory.Create(number)), out var errors)).IsTrue();

        await Assert.That(errors![0].Code).IsEqualTo(OrmErrors.ConflictCode);
    }

    [Test]
    public async Task Other_failures_are_not_mapped()
    {
        await Assert.That(Mapper.TryMap(new DbUpdateException("x", SqlExceptionFactory.Create(8152)), out _)).IsFalse();
        await Assert.That(Mapper.TryMap(SqlExceptionFactory.Create(-2), out _)).IsFalse();
        await Assert.That(Mapper.TryMap(new InvalidOperationException("x"), out _)).IsFalse();
    }

    [Test]
    [Arguments(1, OrmErrors.ConcurrencyCode)]
    [Arguments(2, OrmErrors.ConflictCode)]
    public async Task AddTecOrm_registers_the_mapper_and_the_pipeline_returns_409(int kind, string code)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecOrm<CodeFirstContext>(o => o.ConnectionSecretName = "segredo");
        services.AddTecCqrs(o => o.AddCommandHandler<ThrowingCommand, ThrowingHandler>());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ThrowingCommand(kind));

        await Assert.That(result.Error!.Code).IsEqualTo(code);
        await Assert.That(result.Error.Type.ToHttpStatusCode()).IsEqualTo(409);
    }

    [Test]
    public async Task Unmapped_exception_still_escapes_the_pipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecOrm<CodeFirstContext>(o => o.ConnectionSecretName = "segredo");
        services.AddTecCqrs(o => o.AddCommandHandler<ThrowingCommand, ThrowingHandler>());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await Assert.That(async () => await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ThrowingCommand(3)))
            .ThrowsExactly<InvalidOperationException>();
    }
}

public class EfIdempotencyStoreRegistrationTests
{
    [Test]
    public async Task Registers_scoped_store_and_cleanup_service()
    {
        var services = new ServiceCollection();
        services.AddTecOrmIdempotency<CodeFirstContext>();

        await Assert.That(services.Single(d => d.ServiceType == typeof(IIdempotencyStore)).Lifetime).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(services.Any(d => d.ServiceType == typeof(IHostedService))).IsTrue();
    }

    [Test]
    public async Task Cleanup_can_be_disabled()
    {
        var services = new ServiceCollection();
        services.AddTecOrmIdempotency<CodeFirstContext>(o => o.EnableCleanup = false);

        await Assert.That(services.Any(d => d.ServiceType == typeof(IHostedService))).IsFalse();
    }

    [Test]
    public async Task Second_store_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddInMemoryIdempotencyStore();

        var ex = await Assert.That(() => services.AddTecOrmIdempotency<CodeFirstContext>()).ThrowsExactly<InvalidOperationException>();
        await Assert.That(ex!.Message).Contains("IIdempotencyStore");
    }

    [Test]
    [Arguments(0)]
    [Arguments(10_001)]
    public async Task Invalid_batch_size_is_rejected(int batch)
    {
        await Assert.That(() => new ServiceCollection().AddTecOrmIdempotency<CodeFirstContext>(o => o.CleanupBatchSize = batch))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Unmapped_table_fails_with_clear_message()
    {
        var options = new DbContextOptionsBuilder<CodeFirstContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new CodeFirstContext(options);
        var store = new EfIdempotencyStore<CodeFirstContext>(context, TimeProvider.System);

        var ex = await Assert.That(async () => await store.TryBeginAsync(
                new IdempotencyRequest(new IdempotencyKey("u", "k"), "h", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)), CancellationToken.None))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(ex!.Message).Contains("AddTecIdempotency");
    }
}
