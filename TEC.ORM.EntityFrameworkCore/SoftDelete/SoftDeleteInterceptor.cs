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

internal sealed class SoftDeleteInterceptor(TimeProvider time, int maxPropagation = SoftDeleteInterceptor.DefaultMaxPropagation)
    : SaveChangesInterceptor
{
    public const int DefaultMaxPropagation = 10_000;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ApplyAsync(eventData.Context, async: false, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(eventData.Context, async: true, cancellationToken).ConfigureAwait(false);
        return result;
    }

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

    private sealed class Propagation(DbContext context, DateTimeOffset now, bool cascadedByTracker, int maxPropagation, bool async,
        CancellationToken cancellationToken)
    {
        private static readonly MethodInfo LoadBoundedMethod =
            typeof(Propagation).GetMethod(nameof(LoadBoundedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

        private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IForeignKey, Dictionary<object?[], List<EntityEntry>>> _byForeignKey = [];
        private int _propagated;

        public void MarkDeleted(EntityEntry entry)
        {
            _visited.Add(entry.Entity);
            if (entry.State == EntityState.Deleted)
                entry.State = EntityState.Unchanged;
            entry.Property(nameof(ISoftDelete.IsDeleted)).CurrentValue = true;
            entry.Property(nameof(ISoftDelete.DeletedAt)).CurrentValue = now;
        }

        public async ValueTask PropagateAsync(EntityEntry principal, bool softDelete)
        {
            foreach (var foreignKey in principal.Metadata.GetReferencingForeignKeys())
            {
                bool cascade = foreignKey.IsOwnership || foreignKey.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.ClientCascade;
                bool dependentIsSoftDelete = typeof(ISoftDelete).IsAssignableFrom(foreignKey.DeclaringEntityType.ClrType);

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
                            continue;
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

        private void Preserve(EntityEntry dependent, IForeignKey foreignKey, bool cascade)
        {
            if (!cascadedByTracker)
                return;

            if (cascade && dependent.State == EntityState.Deleted)
            {
                dependent.State = EntityState.Unchanged;
                return;
            }

            foreach (var property in foreignKey.Properties)
            {
                var value = dependent.Property(property.Name);
                if (value.CurrentValue is null && value.OriginalValue is not null)
                    value.CurrentValue = value.OriginalValue;
            }
        }

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
            var bounded = ((IQueryable<T>)query).AsTracking().Take(limit);
            return async ? await bounded.ToListAsync(cancellationToken).ConfigureAwait(false) : bounded.ToList();
        }

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

internal sealed class OrmSoftDeletePropagationException(Error error) : InvalidOperationException("Exclusão recusada: " + error.Code)
{
    public Error Error { get; } = error;
}
