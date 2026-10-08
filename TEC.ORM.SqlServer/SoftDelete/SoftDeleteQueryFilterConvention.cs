using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using TEC.ORM.Entities;
using TEC.ORM.Internal;

namespace TEC.ORM.SqlServer.SoftDelete;

/// <summary>
/// Convenção que, ao finalizar o modelo (depois do <c>OnModelCreating</c>), adiciona o filtro global <c>!IsDeleted</c> a toda
/// entidade raiz <see cref="ISoftDelete"/>, sem perder os filtros que a aplicação já tenha definido.
/// </summary>
/// <remarks>
/// <para>EF Core 8: filtro anônimo (o único que existe), combinado com E ao filtro da aplicação.</para>
/// <para>EF Core 10: filtro <b>nomeado</b> <see cref="OrmModelConfigurationExtensions.SoftDeleteFilterName"/>, que convive com
/// os filtros nomeados da aplicação (ex.: <c>HasQueryFilter("Tenant", ...)</c>); o EF 10 recusa misturar filtro anônimo com
/// nomeado. Se a aplicação usar um filtro anônimo na entidade, ele é combinado com E, como no EF 8.</para>
/// <para>Idempotente: entidade cujo filtro já testa <c>IsDeleted</c> não recebe outro, então a convenção pode estar registrada
/// duas vezes (ex.: <see cref="OrmDbContext"/> e <c>AddTecOrm</c>) sem duplicar o filtro.</para>
/// <para>Use <c>IgnoreQueryFilters()</c> em consultas próprias (ex.: auditoria, restauração) para enxergar os excluídos.</para>
/// </remarks>
internal sealed class SoftDeleteQueryFilterConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            // Herança (TPH/TPT): o filtro só pode ficar na raiz. Tipos owned seguem o dono.
            if (entityType.BaseType is not null || entityType.IsOwned() || !typeof(ISoftDelete).IsAssignableFrom(entityType.ClrType) ||
                HasSoftDeleteFilter(entityType))
                continue;

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            Expression body = Expression.Not(Expression.Call(typeof(EF), nameof(EF.Property), [typeof(bool)], parameter,
                Expression.Constant(nameof(ISoftDelete.IsDeleted))));

#pragma warning disable CS0618 // EF 10: GetQueryFilter/HasQueryFilter(lambda) são o filtro anônimo (o único no EF 8)
            if (entityType.GetQueryFilter() is { } existing)
            {
                var existingBody = ParameterReplacer.Replace(existing.Body, existing.Parameters[0], parameter);
                // Pelo metadado, não pelo builder: o filtro da aplicação tem origem explícita e o builder de convenção seria ignorado
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

    /// <summary>
    /// Garante que a entidade (se estiver no modelo) tem o filtro de exclusão lógica; lança <see cref="InvalidOperationException"/>
    /// se o contexto não aplicou a convenção. Devolve o próprio contexto.
    /// </summary>
    internal static DbContext EnsureFilter(DbContext context, Type entityClrType)
    {
        var entityType = context.Model.FindEntityType(entityClrType);
        if (entityType is null)
            return context;

        var root = entityType.GetRootType();
        if (!HasSoftDeleteFilter(root))
            throw new InvalidOperationException(
                $"O contexto '{context.GetType().Name}' não tem o filtro global de exclusão lógica para '{entityClrType.Name}': registros " +
                "excluídos voltariam nas consultas. Registre o contexto com AddTecOrm/UseTecOrm, herde de OrmDbContext ou chame " +
                "configurationBuilder.AddTecOrmConventions() em ConfigureConventions.");
        return context;
    }

    /// <summary>Algum filtro da entidade (anônimo ou nomeado) já testa <c>IsDeleted</c>.</summary>
    internal static bool HasSoftDeleteFilter(IReadOnlyEntityType entityType)
    {
#if NET10_0_OR_GREATER
        return entityType.GetDeclaredQueryFilters().Any(filter => filter.Expression is { } expression && IsDeletedFinder.Find(expression));
#else
        return entityType.GetQueryFilter() is { } filter && IsDeletedFinder.Find(filter);
#endif
    }

    /// <summary>
    /// Desliga só o filtro de exclusão lógica da consulta (para enxergar os excluídos), mantendo os demais filtros globais da
    /// aplicação.
    /// </summary>
    /// <remarks>
    /// EF Core 10 com o filtro nomeado: <c>IgnoreQueryFilters([SoftDeleteFilterName])</c>. Filtro anônimo (EF Core 8, ou filtro
    /// anônimo da aplicação): se for só o da convenção, ignora os filtros; se for o da aplicação combinado pela convenção
    /// (<c>filtroDaAplicação &amp;&amp; !IsDeleted</c>), ignora os filtros e reaplica o da aplicação. Qualquer outra forma (filtro
    /// da aplicação que já testa <c>IsDeleted</c> por conta própria) não é separável com segurança: a consulta fica como está e
    /// os excluídos continuam invisíveis.
    /// </remarks>
    internal static IQueryable<TEntity> IgnoreSoftDeleteFilter<TEntity>(IQueryable<TEntity> query, IReadOnlyEntityType entityType, DbContext context)
        where TEntity : class
    {
        var root = entityType.GetRootType();
#if NET10_0_OR_GREATER
        if (root.GetDeclaredQueryFilters().Any(filter => filter.Key == OrmModelConfigurationExtensions.SoftDeleteFilterName))
            return query.IgnoreQueryFilters([OrmModelConfigurationExtensions.SoftDeleteFilterName]);
#endif

#pragma warning disable CS0618 // EF 10: GetQueryFilter é o filtro anônimo (o único no EF 8)
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
            // O filtro reaplicado vira um Where comum: referências ao contexto que construiu o modelo (ex.: campo do contexto) precisam
            // apontar para o contexto desta consulta, que o EF só troca automaticamente dentro de filtros globais
            var applicationFilter = new ContextReplacer(context).Visit(ParameterReplacer.Replace(combined.Left, original, parameter));
            return query.IgnoreQueryFilters().Where(Expression.Lambda<Func<TEntity, bool>>(applicationFilter, parameter));
        }
        return query;
    }

    /// <summary>O termo criado pela convenção: <c>!EF.Property&lt;bool&gt;(e, "IsDeleted")</c>.</summary>
    internal static bool IsConventionTerm(Expression expression, ParameterExpression parameter) =>
        expression is UnaryExpression { NodeType: ExpressionType.Not, Operand: MethodCallExpression call } &&
        call.Method.DeclaringType == typeof(EF) && call.Method.Name == nameof(EF.Property) &&
        call.Arguments is [var instance, ConstantExpression { Value: nameof(ISoftDelete.IsDeleted) }] && instance == parameter;

    /// <summary>Troca constantes de <c>DbContext</c> (o contexto que construiu o modelo) pelo contexto da consulta.</summary>
    private sealed class ContextReplacer(DbContext context) : ExpressionVisitor
    {
        protected override Expression VisitConstant(ConstantExpression node) =>
            node.Value is DbContext && node.Type.IsInstanceOfType(context) ? Expression.Constant(context, node.Type) : base.VisitConstant(node);
    }

    /// <summary>Procura <c>EF.Property&lt;bool&gt;(e, "IsDeleted")</c> ou <c>e.IsDeleted</c> na expressão.</summary>
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

/// <summary>Configura as convenções do TEC.ORM em um <c>DbContext</c> que não herda de <see cref="OrmDbContext"/>.</summary>
public static class OrmModelConfigurationExtensions
{
    /// <summary>
    /// Nome do filtro global de exclusão lógica no EF Core 10 (filtros nomeados). Permite desligar só ele numa consulta:
    /// <c>IgnoreQueryFilters([OrmModelConfigurationExtensions.SoftDeleteFilterName])</c>. No EF Core 8 o filtro é anônimo.
    /// </summary>
    public const string SoftDeleteFilterName = "TecOrm.SoftDelete";

    /// <summary>
    /// Adiciona as convenções do TEC.ORM (filtro global de exclusão lógica e tamanho das colunas de auditoria). Chame em <c>ConfigureConventions</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
    ///     configurationBuilder.AddTecOrmConventions();
    /// </code>
    /// </example>
    public static ModelConfigurationBuilder AddTecOrmConventions(this ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Conventions.Add(_ => new Auditing.AuditModelConvention());
        configurationBuilder.Conventions.Add(_ => new SoftDeleteQueryFilterConvention());
        return configurationBuilder;
    }

    /// <summary>Configura as colunas de exclusão lógica de uma entidade (padrão <c>false</c> e índice filtrado opcional).</summary>
    public static EntityTypeBuilder<TEntity> HasSoftDelete<TEntity>(this EntityTypeBuilder<TEntity> builder, bool index = true)
        where TEntity : class, ISoftDelete
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Property(e => e.IsDeleted).HasDefaultValue(false);
        builder.Property(e => e.DeletedAt);
        if (index)
            builder.HasIndex(e => e.IsDeleted).HasFilter("[IsDeleted] = 0");
        return builder;
    }
}

/// <summary>
/// Base opcional dos <c>DbContext</c> do TEC.ORM: já aplica as convenções (<see cref="OrmModelConfigurationExtensions.AddTecOrmConventions(ModelConfigurationBuilder)"/>).
/// </summary>
public abstract class OrmDbContext(DbContextOptions options) : DbContext(options)
{
    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.AddTecOrmConventions();
    }
}
