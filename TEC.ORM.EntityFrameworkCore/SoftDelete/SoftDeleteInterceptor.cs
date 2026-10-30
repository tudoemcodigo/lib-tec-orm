using System.Collections;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using TEC.Core.Common.Results;
using TEC.ORM.Common;
using TEC.ORM.Entities;

namespace TEC.ORM.EntityFrameworkCore.SoftDelete;

/// <summary>
/// Converte a exclusão física em lógica ao salvar: toda entidade <see cref="ISoftDelete"/> marcada como <c>Deleted</c>
/// (por <c>Remove</c>, cascata ou pelo repositório) volta a <c>Unchanged</c> e só tem <c>IsDeleted</c> e <c>DeletedAt</c> gravados.
/// </summary>
/// <remarks>
/// <para>Propagação ("propagar soft delete"), a partir de cada entidade excluída logicamente:</para>
/// <list type="bullet">
/// <item><description>dependentes em relação de cascata (<c>Cascade</c>/<c>ClientCascade</c>) que também são
/// <see cref="ISoftDelete"/> são excluídos logicamente, recursivamente e com o mesmo <c>DeletedAt</c>; os que não estão
/// rastreados são carregados pela navegação do principal (se ela existir);</description></item>
/// <item><description>tipos <i>owned</i> (<c>OwnsOne</c>/<c>OwnsMany</c>) e dependentes que <b>não</b> são
/// <see cref="ISoftDelete"/> são preservados: a linha do principal continua existindo, então nada é apagado nem tem a chave
/// estrangeira anulada.</description></item>
/// </list>
/// <para>Com <c>CascadeDeleteTiming = Immediate</c> (padrão do EF Core), um <c>Remove</c> feito direto no contexto já cascateia
/// no rastreador antes do salvamento (dependentes <c>Deleted</c>, chaves estrangeiras opcionais anuladas): o interceptor desfaz
/// isso para os dependentes preservados. O <c>OrmRepository.DeleteAsync</c> desliga a cascata do rastreador durante a operação,
/// então não há o que desfazer.</para>
/// <para>Custo: uma consulta por navegação em cascata de cada principal excluído (N+1) e um <c>UPDATE</c> por linha. Por isso
/// há um teto (<paramref name="maxPropagation"/>, <c>OrmOptions.MaxSoftDeletePropagation</c>): cada carga lê no máximo o que
/// ainda cabe nele (+1) e, passando dele, o salvamento falha com <c>ORM_LIMITE_EXCEDIDO</c> sem gravar nada. Os dependentes
/// rastreados são achados por um índice por chave estrangeira montado uma vez por salvamento (antes: varredura do rastreador
/// inteiro para cada principal).</para>
/// </remarks>
/// <param name="time">Relógio do <c>DeletedAt</c>.</param>
/// <param name="maxPropagation">Máximo de dependentes excluídos por propagação num salvamento.</param>
internal sealed class SoftDeleteInterceptor(TimeProvider time, int maxPropagation = SoftDeleteInterceptor.DefaultMaxPropagation)
    : SaveChangesInterceptor
{
    /// <summary>Padrão de <c>OrmOptions.MaxSoftDeletePropagation</c>.</summary>
    internal const int DefaultMaxPropagation = 10_000;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        // Caminho síncrono: as cargas são síncronas, então a ValueTask já vem concluída
        ApplyAsync(eventData.Context, async: false, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(eventData.Context, async: true, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Aplica a exclusão lógica (síncrono; usado nos testes).</summary>
    internal void Apply(DbContext? context) =>
        ApplyAsync(context, async: false, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    private async ValueTask ApplyAsync(DbContext? context, bool async, CancellationToken cancellationToken)
    {
        if (context is null)
            return;

        var tracker = context.ChangeTracker;
        var roots = tracker.Entries<ISoftDelete>().Where(e => e.State == EntityState.Deleted).Select(e => (EntityEntry)e).ToList();
        if (roots.Count == 0)
            return;

        var operation = new Propagation(context, time.GetUtcNow(), tracker.CascadeDeleteTiming == CascadeTiming.Immediate, maxPropagation,
            async, cancellationToken);
        // DetectChanges já rodou (Entries); durante a propagação, as alterações são feitas pela API do rastreador
        bool autoDetect = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var root in roots)
                operation.MarkDeleted(root);
            foreach (var root in roots)
                await operation.PropagateAsync(root, softDelete: true).ConfigureAwait(false);
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    /// <summary>
    /// Uma propagação (um salvamento): o mesmo instante para todas as entidades, o controle de visitadas, o teto e o índice dos
    /// dependentes rastreados por chave estrangeira.
    /// </summary>
    private sealed class Propagation(DbContext context, DateTimeOffset now, bool cascadedByTracker, int maxPropagation, bool async,
        CancellationToken cancellationToken)
    {
        private static readonly MethodInfo LoadBoundedMethod =
            typeof(Propagation).GetMethod(nameof(LoadBoundedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

        private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);

        /// <summary>Entradas rastreadas por chave estrangeira e valor (atual e original), montado na primeira consulta à chave.</summary>
        private readonly Dictionary<IForeignKey, Dictionary<object?[], List<EntityEntry>>> _byForeignKey = [];

        /// <summary>Dependentes já excluídos por propagação neste salvamento.</summary>
        private int _propagated;

        /// <summary>Converte a exclusão física da entrada em lógica.</summary>
        public void MarkDeleted(EntityEntry entry)
        {
            _visited.Add(entry.Entity);
            if (entry.State == EntityState.Deleted)
                entry.State = EntityState.Unchanged;
            entry.Property(nameof(ISoftDelete.IsDeleted)).CurrentValue = true;
            entry.Property(nameof(ISoftDelete.DeletedAt)).CurrentValue = now;
        }

        /// <summary>
        /// Percorre os dependentes do principal. <paramref name="softDelete"/>: o principal foi excluído logicamente (a cascata
        /// para <see cref="ISoftDelete"/> vale); senão o principal foi preservado e os dependentes dele também são.
        /// </summary>
        public async ValueTask PropagateAsync(EntityEntry principal, bool softDelete)
        {
            foreach (var foreignKey in principal.Metadata.GetReferencingForeignKeys())
            {
                bool cascade = foreignKey.IsOwnership || foreignKey.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.ClientCascade;
                bool dependentIsSoftDelete = typeof(ISoftDelete).IsAssignableFrom(foreignKey.DeclaringEntityType.ClrType);

                // Dependentes ISoftDelete ainda não carregados: carrega pela navegação (o filtro global ignora os já excluídos),
                // no máximo o que ainda cabe no teto
                if (softDelete && cascade && dependentIsSoftDelete && !foreignKey.IsOwnership &&
                    foreignKey.PrincipalToDependent is { } navigation && principal.Navigation(navigation.Name) is { IsLoaded: false } toLoad)
                    await LoadAsync(toLoad).ConfigureAwait(false);

                foreach (var dependent in Dependents(principal, foreignKey))
                {
                    if (!_visited.Add(dependent.Entity))
                        continue;

                    if (softDelete && cascade && dependent.Entity is ISoftDelete)
                    {
                        if (dependent.Entity is ISoftDelete { IsDeleted: true } && dependent.State != EntityState.Deleted)
                            continue;   // já excluído antes: mantém o DeletedAt original
                        if (++_propagated > maxPropagation)
                            throw new OrmSoftDeletePropagationException(OrmErrors.SoftDeletePropagationTooLarge(maxPropagation));
                        MarkDeleted(dependent);
                        await PropagateAsync(dependent, softDelete: true).ConfigureAwait(false);
                    }
                    else
                    {
                        Preserve(dependent, foreignKey, cascade);
                        await PropagateAsync(dependent, softDelete: false).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>Desfaz o que a cascata imediata do rastreador fez num dependente que deve continuar como está.</summary>
        private void Preserve(EntityEntry dependent, IForeignKey foreignKey, bool cascade)
        {
            if (!cascadedByTracker)
                return;   // sem cascata imediata: um Deleted aqui foi pedido pela aplicação

            if (cascade && dependent.State == EntityState.Deleted)
            {
                // Unchanged; o DetectChanges do salvamento volta a Modified se houver outras alterações pendentes
                dependent.State = EntityState.Unchanged;
                return;
            }

            // Relação opcional (SetNull/ClientSetNull): a cascata anulou a chave estrangeira
            foreach (var property in foreignKey.Properties)
            {
                var value = dependent.Property(property.Name);
                if (value.CurrentValue is null && value.OriginalValue is not null)
                    value.CurrentValue = value.OriginalValue;
            }
        }

        /// <summary>
        /// Carrega a navegação lendo no máximo o que ainda cabe no teto (+1, para saber que passou): passando, falha antes de
        /// trazer o resto. Coleção lida por inteiro é marcada como carregada, como faria o <c>Load</c>.
        /// </summary>
        private async ValueTask LoadAsync(NavigationEntry navigation)
        {
            int remaining = maxPropagation - _propagated;
            var query = navigation.Query();
            var load = (Func<IQueryable, int, bool, CancellationToken, Task<IList>>)LoadBoundedMethod.MakeGenericMethod(query.ElementType)
                .CreateDelegate(typeof(Func<IQueryable, int, bool, CancellationToken, Task<IList>>));
            var task = load(query, remaining + 1, async, cancellationToken);
            var loaded = async ? await task.ConfigureAwait(false) : task.GetAwaiter().GetResult();
            if (loaded.Count > remaining)
                throw new OrmSoftDeletePropagationException(OrmErrors.SoftDeletePropagationTooLarge(maxPropagation));

            navigation.IsLoaded = true;
            foreach (object item in loaded)
                Index(context.Entry(item));
        }

        private static async Task<IList> LoadBoundedAsync<T>(IQueryable query, int limit, bool async, CancellationToken cancellationToken)
            where T : class
        {
            // Rastreadas, como no Load: a propagação marca e grava essas entradas
            var bounded = ((IQueryable<T>)query).AsTracking().Take(limit);
            return async ? await bounded.ToListAsync(cancellationToken).ConfigureAwait(false) : bounded.ToList();
        }

        /// <summary>Entradas rastreadas que apontam para o principal pela chave estrangeira (valor atual ou original).</summary>
        private List<EntityEntry> Dependents(EntityEntry principal, IForeignKey foreignKey)
        {
            var principalValues = foreignKey.PrincipalKey.Properties.Select(p => principal.Property(p.Name).CurrentValue).ToArray();
            if (!IndexFor(foreignKey).TryGetValue(principalValues, out var candidates))
                return [];

            var result = new List<EntityEntry>(candidates.Count);
            foreach (var entry in candidates)
            {
                if (!ReferenceEquals(entry.Entity, principal.Entity) && entry.State != EntityState.Detached)
                    result.Add(entry);
            }
            return result;
        }

        /// <summary>Índice da chave estrangeira; na primeira vez, percorre o rastreador uma única vez.</summary>
        private Dictionary<object?[], List<EntityEntry>> IndexFor(IForeignKey foreignKey)
        {
            if (_byForeignKey.TryGetValue(foreignKey, out var index))
                return index;

            index = new Dictionary<object?[], List<EntityEntry>>(KeyComparer.Instance);
            _byForeignKey[foreignKey] = index;
            foreach (var entry in context.ChangeTracker.Entries())
                Add(index, foreignKey, entry);
            return index;
        }

        /// <summary>Acrescenta uma entrada carregada durante a propagação aos índices já montados.</summary>
        private void Index(EntityEntry entry)
        {
            foreach (var (foreignKey, index) in _byForeignKey)
                Add(index, foreignKey, entry);
        }

        private static void Add(Dictionary<object?[], List<EntityEntry>> index, IForeignKey foreignKey, EntityEntry entry)
        {
            if (entry.State == EntityState.Detached || !IsSameOrDerived(entry.Metadata, foreignKey.DeclaringEntityType))
                return;

            var current = Values(entry, foreignKey, original: false);
            var original = Values(entry, foreignKey, original: true);
            if (current is not null)
                AddTo(index, current, entry);
            if (original is not null && (current is null || !KeyComparer.Instance.Equals(current, original)))
                AddTo(index, original, entry);
        }

        private static void AddTo(Dictionary<object?[], List<EntityEntry>> index, object?[] key, EntityEntry entry)
        {
            if (!index.TryGetValue(key, out var entries))
                index[key] = entries = [];
            if (!entries.Contains(entry))
                entries.Add(entry);
        }

        /// <summary>Valores da chave estrangeira (atual ou original); <c>null</c> se alguma parte é nula.</summary>
        private static object?[]? Values(EntityEntry entry, IForeignKey foreignKey, bool original)
        {
            var values = new object?[foreignKey.Properties.Count];
            for (int i = 0; i < values.Length; i++)
            {
                var property = entry.Property(foreignKey.Properties[i].Name);
                values[i] = original ? property.OriginalValue : property.CurrentValue;
                if (values[i] is null)
                    return null;
            }
            return values;
        }

        /// <summary>Igualdade estrutural de chaves compostas (inclusive <c>byte[]</c>).</summary>
        private sealed class KeyComparer : IEqualityComparer<object?[]>
        {
            public static readonly KeyComparer Instance = new();

            public bool Equals(object?[]? x, object?[]? y) => StructuralComparisons.StructuralEqualityComparer.Equals(x, y);

            public int GetHashCode(object?[] obj) => StructuralComparisons.StructuralEqualityComparer.GetHashCode(obj);
        }

        private static bool IsSameOrDerived(IReadOnlyEntityType type, IReadOnlyEntityType target)
        {
            for (var current = type; current is not null; current = current.BaseType)
            {
                if (current == target)
                    return true;
            }
            return false;
        }
    }
}

/// <summary>
/// Propagação da exclusão lógica além de <c>OrmOptions.MaxSoftDeletePropagation</c>. Lançada dentro do salvamento (o
/// interceptor não devolve <see cref="Result"/>); o <c>OrmOperationRunner</c> a converte de volta em <see cref="Error"/>.
/// </summary>
internal sealed class OrmSoftDeletePropagationException(Error error) : InvalidOperationException("Exclusão recusada: " + error.Code)
{
    public Error Error { get; } = error;
}
