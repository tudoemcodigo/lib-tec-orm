using Microsoft.Extensions.DependencyInjection;
using TEC.ORM.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.LoadTests.Infrastructure;
using TEC.ORM.Queries;

namespace TEC.ORM.LoadTests.Security;

/// <summary>
/// Segurança sob carga no SQL Server real, pelo caminho de produção (<c>AddTecOrm</c> + escopo de DI por requisição): a
/// auditoria nunca grava a identidade de outro usuário, a verificação de somente leitura não deixa escrita passar sob
/// concorrência e nenhum valor sensível (senha da conexão, valores que causaram erro) chega aos logs.
/// </summary>
[Category(TestCategories.LoadCi)]
[NotInParallel(LoadSettings.Database)]
public class SecurityLoadTests
{
    private const int Workers = 32;

    [Test]
    public async Task Audit_ConcurrentUsers_NeverRecordsAnotherUsersIdentity()
    {
        // Risco: identidade resolvida do escopo errado (opções do DbContext, interceptor singleton, pool de contextos)
        await LoadDatabase.RequireAsync();
        await using var provider = LoadDatabase.Build();
        var batch = LoadDatabase.NewBatch();
        const int perWorker = 25;
        try
        {
            using var gate = new Barrier(Workers);
            await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Factory.StartNew(async () =>
            {
                gate.SignalAndWait();
                for (int i = 0; i < perWorker; i++)
                {
                    await using var scope = provider.CreateAsyncScope();
                    scope.ServiceProvider.GetRequiredService<TestUser>().As(worker);
                    var accounts = scope.ServiceProvider.GetRequiredService<IOrmRepository<Account, Guid>>();
                    var created = await accounts.CreateAsync(new Account { Batch = batch, Name = $"w{worker}-{i}", Email = $"a{worker}-{i}-{batch:N}@carga", Category = worker });
                    created.Value.Balance = i + 1;   // sempre muda: atualização sem alteração não gera UPDATE nem autoria
                    await accounts.UpdateAsync(created.Value);
                    if (i % 5 == 0)
                        await accounts.DeleteAsync(created.Value.Id);
                }
            }, TaskCreationOptions.LongRunning).Unwrap()));

            await using var check = provider.CreateAsyncScope();
            var rows = await check.ServiceProvider.GetRequiredService<IOrmQueryExecutor>().QueryAsync<AuditRow>(SqlQuery.Interpolated("carga.auditoria",
                $"SELECT Category, IsDeleted, CreatedBy, CreatedByTenant, UpdatedBy, UpdatedByTenant, DeletedBy FROM loadtest.Accounts WHERE Batch = {batch}"));

            await Assert.That(rows.Value.Count).IsEqualTo(Workers * perWorker);
            var wrong = rows.Value.Where(r =>
                r.CreatedBy != Owner(r.Category) || r.UpdatedBy != Owner(r.Category) ||
                r.CreatedByTenant != $"tenant-{r.Category % 4}" || r.UpdatedByTenant != r.CreatedByTenant ||
                (r.IsDeleted ? r.DeletedBy != Owner(r.Category) : r.DeletedBy is not null)).ToList();
            await Assert.That(wrong).IsEmpty().Because(string.Join("; ", wrong.Take(5)));
        }
        finally
        {
            await LoadDatabase.PurgeAsync(provider, batch);
        }

        static string Owner(int worker) => $"oid-worker-{worker:D3}";
    }

    public sealed record AuditRow(int Category, bool IsDeleted, string? CreatedBy, string? CreatedByTenant, string? UpdatedBy,
        string? UpdatedByTenant, string? DeletedBy);

    [Test]
    public async Task ReadOnlyQueries_UnderConcurrency_NeverLetWritesThrough()
    {
        await LoadDatabase.RequireAsync();
        await using var provider = LoadDatabase.Build();
        var batch = LoadDatabase.NewBatch();
        string[] attacks =
        [
            "SELECT 1 DELETE FROM loadtest.Accounts", "SELECT 1DELETE FROM loadtest.Accounts", "SELECT 0x1FUPDATE loadtest.Accounts SET Balance = 0",
            "SELECT 1 --x\rUPDATE loadtest.Accounts SET Balance = 0", "WITH c AS (SELECT 1 a) UPDATE loadtest.Accounts SET Balance = 0",
            "SELECT * INTO loadtest.Copy FROM loadtest.Accounts", "SELECT 1 DISABLE TRIGGER ALL ON loadtest.Accounts", "SELECT 1; TRUNCATE TABLE loadtest.LedgerEntries",
        ];
        try
        {
            Guid canary;
            await using (var scope = provider.CreateAsyncScope())
            {
                scope.ServiceProvider.GetRequiredService<TestUser>().As(0);
                canary = (await scope.ServiceProvider.GetRequiredService<IOrmRepository<Account, Guid>>()
                    .CreateAsync(new Account { Batch = batch, Name = "Canário", Email = $"canario-{batch:N}@carga", Balance = 123.45m })).Value.Id;
            }

            var divergences = new Divergences();
            await Parallel.ForAsync(0, 3_000, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (i, _) =>
            {
                await using var scope = provider.CreateAsyncScope();
                var queries = scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>();
                if (i % 2 == 0)
                {
                    var attack = await queries.QueryAsync<int>(SqlQuery.Create("carga.ataque", attacks[i / 2 % attacks.Length]));
                    if (attack.Error?.Code != OrmErrors.QueryNotAllowedCode)
                        divergences.Add($"{attacks[i / 2 % attacks.Length]} → {attack.Error?.Code ?? "executed"}");
                }
                else
                {
                    var read = await queries.ExecuteScalarAsync<decimal>(SqlQuery.Interpolated("carga.leitura", $"SELECT Balance FROM loadtest.Accounts WHERE Id = {canary}"));
                    if (read.IsFailure || read.Value != 123.45m)
                        divergences.Add($"leitura → {read.Error?.Code ?? read.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }
            });

            await Assert.That(divergences.Count).IsEqualTo(0).Because(divergences.ToString());
        }
        finally
        {
            await LoadDatabase.PurgeAsync(provider, batch);
        }
    }

    [Test]
    public async Task ErrorsUnderLoad_NeverLogValuesOrTheConnectionPassword()
    {
        // Conflitos de chave única e erros de conversão em massa: as mensagens do SQL Server trazem o valor, os logs não
        await LoadDatabase.RequireAsync();
        var logs = new CapturingLoggerProvider();
        await using var provider = LoadDatabase.Build(logs);
        var batch = LoadDatabase.NewBatch();
        string secret = "SEGREDO" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        try
        {
            var codes = new System.Collections.Concurrent.ConcurrentBag<string>();
            await Parallel.ForAsync(0, 400, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (i, _) =>
            {
                await using var scope = provider.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TestUser>().As(i % Workers);
                if (i % 2 == 0)
                {
                    var created = await scope.ServiceProvider.GetRequiredService<IOrmRepository<Account, Guid>>()
                        .CreateAsync(new Account { Batch = batch, Name = "Duplicada", Email = $"{secret}-{i / 2 % 10}@carga".ToLowerInvariant() });
                    codes.Add(created.Error?.Code ?? "ok");
                }
                else
                {
                    var converted = await scope.ServiceProvider.GetRequiredService<IOrmQueryExecutor>()
                        .ExecuteScalarAsync<int>(SqlQuery.Interpolated("carga.conversao", $"SELECT CAST({secret + i} AS int)"));
                    codes.Add(converted.Error?.Code ?? "ok");
                }
            });

            await Assert.That(codes.Count(c => c == "ok")).IsEqualTo(10);
            await Assert.That(codes.Count(c => c == OrmErrors.ConflictCode)).IsEqualTo(190);
            await Assert.That(logs.AllText).DoesNotContain(secret);
            await Assert.That(logs.AllText).DoesNotContain(secret.ToLowerInvariant());
            await Assert.That(logs.AllText).DoesNotContain(LoadDatabase.Password);
        }
        finally
        {
            await LoadDatabase.PurgeAsync(provider, batch);
        }
    }
}
