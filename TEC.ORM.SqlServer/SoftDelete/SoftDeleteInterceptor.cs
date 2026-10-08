using System.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using TEC.ORM.Entities;

namespace TEC.ORM.SqlServer.SoftDelete;

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
/// </remarks>
internal sealed class SoftDeleteInterceptor(TimeProvider time) : SaveChangesInterceptor
{
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

        var operation = new Propagation(context, time.GetUtcNow(), tracker.CascadeDeleteTiming == CascadeTiming.Immediate, async, cancellationToken);
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

    /// <summary>Uma propagação (um salvamento): o mesmo instante para todas as entidades e o controle de visitadas.</summary>
    private sealed class Propagation(DbContext context, DateTimeOffset now, bool cascadedByTracker, bool async, CancellationToken cancellationToken)
    {
        private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);

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

                // Dependentes ISoftDelete ainda não carregados: carrega pela navegação (o filtro global ignora os já excluídos)
                if (softDelete && cascade && dependentIsSoftDelete && !foreignKey.IsOwnership &&
                    foreignKey.PrincipalToDependent is { } navigation && principal.Navigation(navigation.Name) is { IsLoaded: false } toLoad)
                {
                    if (async)
                        await toLoad.LoadAsync(cancellationToken).ConfigureAwait(false);
                    else
                        toLoad.Load();
                }

                foreach (var dependent in Dependents(principal, foreignKey))
                {
                    if (!_visited.Add(dependent.Entity))
                        continue;

                    if (softDelete && cascade && dependent.Entity is ISoftDelete)
                    {
                        if (dependent.Entity is ISoftDelete { IsDeleted: true } && dependent.State != EntityState.Deleted)
                            continue;   // já excluído antes: mantém o DeletedAt original
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

        /// <summary>Entradas rastreadas que apontam para o principal pela chave estrangeira (valor atual ou original).</summary>
        private List<EntityEntry> Dependents(EntityEntry principal, IForeignKey foreignKey)
        {
            var principalValues = foreignKey.PrincipalKey.Properties.Select(p => principal.Property(p.Name).CurrentValue).ToArray();
            var dependentType = foreignKey.DeclaringEntityType;
            var result = new List<EntityEntry>();
            foreach (var entry in context.ChangeTracker.Entries())
            {
                if (ReferenceEquals(entry.Entity, principal.Entity) || entry.State == EntityState.Detached ||
                    !IsSameOrDerived(entry.Metadata, dependentType))
                    continue;
                if (Matches(entry, foreignKey, principalValues, original: false) || Matches(entry, foreignKey, principalValues, original: true))
                    result.Add(entry);
            }
            return result;
        }

        private static bool Matches(EntityEntry entry, IForeignKey foreignKey, object?[] principalValues, bool original)
        {
            for (int i = 0; i < principalValues.Length; i++)
            {
                var property = entry.Property(foreignKey.Properties[i].Name);
                var value = original ? property.OriginalValue : property.CurrentValue;
                if (value is null || !StructuralComparisons.StructuralEqualityComparer.Equals(value, principalValues[i]))
                    return false;
            }
            return true;
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
