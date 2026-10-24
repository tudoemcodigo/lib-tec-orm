using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using TEC.ORM.Entities;
using TEC.ORM.Internal;

namespace TEC.ORM.EntityFrameworkCore.SoftDelete;

internal sealed class SoftDeleteQueryFilterConvention : IModelFinalizingConvention
{
    internal const string SoftDeleteIndexAnnotation = "TecOrm:SoftDeleteIndex";

    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            ApplySoftDeleteIndexFilter(entityType);
            if (!entityType.IsOwned() && typeof(ISoftDelete).IsAssignableFrom(entityType.ClrType) &&
                entityType.FindDeclaredProperty(nameof(ISoftDelete.IsDeleted)) is { } isDeleted)
                isDeleted.Builder.IsConcurrencyToken(true);

            if (entityType.BaseType is not null || entityType.IsOwned() || !typeof(ISoftDelete).IsAssignableFrom(entityType.ClrType) ||
                HasSoftDeleteFilter(entityType))
                continue;

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            Expression body = Expression.Not(Expression.Call(typeof(EF), nameof(EF.Property), [typeof(bool)], parameter,
                Expression.Constant(nameof(ISoftDelete.IsDeleted))));

#pragma warning disable CS0618
            if (entityType.GetQueryFilter() is { } existing)
            {
                var existingBody = ParameterReplacer.Replace(existing.Body, existing.Parameters[0], parameter);
                entityType.SetQueryFilter(Expression.Lambda(Expression.AndAlso(existingBody, body), parameter));
            }
            else
            {
#if NET10_0_OR_GREATER
                entityType.SetQueryFilter(OrmModelConfigurationExtensions.SoftDeleteFilterName, Expression.Lambda(body, parameter));
#else
                entityType.SetQueryFilter(Expression.Lambda(body, parameter));
#endif
            }
#pragma warning restore CS0618
        }
    }

    private static void ApplySoftDeleteIndexFilter(IConventionEntityType entityType)
    {
        foreach (var index in entityType.GetDeclaredIndexes().ToList())
        {
            if (index.FindAnnotation(SoftDeleteIndexAnnotation) is null)
                continue;
            index.RemoveAnnotation(SoftDeleteIndexAnnotation);
            if (index.Properties is [var property] && property.GetColumnName() is { } column)
                index.Builder.HasFilter($"[{column.Replace("]", "]]", StringComparison.Ordinal)}] = 0");
        }
    }

    internal static DbContext EnsureFilter(DbContext context, Type entityClrType)
    {
        var entityType = context.Model.FindEntityType(entityClrType);
        if (entityType is null)
            return context;

        var root = entityType.GetRootType();
        if (!HasSoftDeleteFilter(root))
            throw new InvalidOperationException(
                $"O contexto '{context.GetType().Name}' não tem o filtro global de exclusão lógica para '{entityClrType.Name}': registros " +
                "excluídos voltariam nas consultas. Registre o contexto com AddTecOrm, herde de OrmDbContext ou chame " +
                "configurationBuilder.AddTecOrmConventions() em ConfigureConventions.");
        return context;
    }

    internal static bool HasSoftDeleteFilter(IReadOnlyEntityType entityType)
    {
#if NET10_0_OR_GREATER
        return entityType.GetDeclaredQueryFilters().Any(filter => filter.Expression is { } expression && IsDeletedFinder.Find(expression));
#else
        return entityType.GetQueryFilter() is { } filter && IsDeletedFinder.Find(filter);
#endif
    }

    internal static IQueryable<TEntity> IgnoreSoftDeleteFilter<TEntity>(IQueryable<TEntity> query, IReadOnlyEntityType entityType, DbContext context)
        where TEntity : class
    {
        var root = entityType.GetRootType();
#if NET10_0_OR_GREATER
        if (root.GetDeclaredQueryFilters().Any(filter => filter.Key == OrmModelConfigurationExtensions.SoftDeleteFilterName))
            return query.IgnoreQueryFilters([OrmModelConfigurationExtensions.SoftDeleteFilterName]);
#endif

#pragma warning disable CS0618
        var filter = root.GetQueryFilter();
#pragma warning restore CS0618
        if (filter is null)
            return query;

        var original = filter.Parameters[0];
        if (IsConventionTerm(filter.Body, original))
            return query.IgnoreQueryFilters();

        if (filter.Body is BinaryExpression { NodeType: ExpressionType.AndAlso } combined && IsConventionTerm(combined.Right, original))
        {
            var parameter = Expression.Parameter(typeof(TEntity), "e");
            var applicationFilter = new ContextReplacer(context).Visit(ParameterReplacer.Replace(combined.Left, original, parameter));
            return query.IgnoreQueryFilters().Where(Expression.Lambda<Func<TEntity, bool>>(applicationFilter, parameter));
        }
        return query;
    }

    internal static bool IsConventionTerm(Expression expression, ParameterExpression parameter) =>
        expression is UnaryExpression { NodeType: ExpressionType.Not, Operand: MethodCallExpression call } &&
        call.Method.DeclaringType == typeof(EF) && call.Method.Name == nameof(EF.Property) &&
        call.Arguments is [var instance, ConstantExpression { Value: nameof(ISoftDelete.IsDeleted) }] && instance == parameter;

    private sealed class ContextReplacer(DbContext context) : ExpressionVisitor
    {
        protected override Expression VisitConstant(ConstantExpression node) =>
            node.Value is DbContext && node.Type.IsInstanceOfType(context) ? Expression.Constant(context, node.Type) : base.VisitConstant(node);
    }

    private sealed class IsDeletedFinder : ExpressionVisitor
    {
        private bool _found;

        public static bool Find(Expression expression)
        {
            var finder = new IsDeletedFinder();
            finder.Visit(expression);
            return finder._found;
        }

        public override Expression? Visit(Expression? node) => _found ? node : base.Visit(node);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(EF) && node.Method.Name == nameof(EF.Property) &&
                node.Arguments is [_, ConstantExpression { Value: nameof(ISoftDelete.IsDeleted) }])
                _found = true;
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Member.Name == nameof(ISoftDelete.IsDeleted) && node.Expression is ParameterExpression)
                _found = true;
            return base.VisitMember(node);
        }
    }
}

/// <summary>Configura as convenções do TEC.ORM no DbContext.</summary>
public static class OrmModelConfigurationExtensions
{
    /// <summary>Nome do filtro de exclusão lógica.</summary>
    public const string SoftDeleteFilterName = "TecOrm.SoftDelete";

    /// <summary>Adiciona as convenções do TEC.ORM ao configurador de modelos.</summary>
    public static ModelConfigurationBuilder AddTecOrmConventions(this ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Conventions.Add(_ => new Auditing.AuditModelConvention());
        configurationBuilder.Conventions.Add(_ => new SoftDeleteQueryFilterConvention());
        return configurationBuilder;
    }

    /// <summary>Configura exclusão lógica na entidade.</summary>
    public static EntityTypeBuilder<TEntity> HasSoftDelete<TEntity>(this EntityTypeBuilder<TEntity> builder, bool index = true)
        where TEntity : class, ISoftDelete
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Property(e => e.IsDeleted).HasDefaultValue(false);
        builder.Property(e => e.DeletedAt);
        if (index)
            builder.HasIndex(e => e.IsDeleted).HasAnnotation(SoftDeleteQueryFilterConvention.SoftDeleteIndexAnnotation, true);
        return builder;
    }
}

/// <summary>Base opcional dos DbContext do TEC.ORM.</summary>
public abstract class OrmDbContext(DbContextOptions options) : DbContext(options)
{
    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.AddTecOrmConventions();
    }
}
