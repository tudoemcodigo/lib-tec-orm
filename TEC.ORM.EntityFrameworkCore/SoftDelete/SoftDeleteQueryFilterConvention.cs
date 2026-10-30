using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using TEC.ORM.Entities;
using TEC.ORM.EntityFrameworkCore.Auditing;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Idempotency;
using TEC.ORM.Internal;

namespace TEC.ORM.EntityFrameworkCore.SoftDelete;

/// <summary>
/// Convenção que, ao finalizar o modelo (depois do <c>OnModelCreating</c>), adiciona o filtro global <c>!IsDeleted</c> a toda
/// entidade raiz <see cref="ISoftDelete"/>, sem perder os filtros que a aplicação já tenha definido.
/// </summary>
/// <remarks>
/// <para>Filtro <b>nomeado</b> <see cref="OrmModelConfigurationExtensions.SoftDeleteFilterName"/> (EF Core 10), que convive com
/// os filtros nomeados da aplicação (ex.: <c>HasQueryFilter("Tenant", ...)</c>); o EF 10 recusa misturar filtro anônimo com
/// nomeado. Se a aplicação usar um filtro anônimo na entidade, ele é combinado com E.</para>
/// <para>Idempotente: entidade cujo filtro já testa <c>IsDeleted</c> não recebe outro, então a convenção pode estar registrada
/// duas vezes (ex.: <see cref="OrmDbContext"/> e o registro do TEC.ORM) sem duplicar o filtro.</para>
/// <para>Use <c>IgnoreQueryFilters()</c> em consultas próprias (ex.: auditoria, restauração) para enxergar os excluídos.</para>
/// <para><c>IsDeleted</c> também vira token de concorrência: todo <c>UPDATE</c> confere o valor lido (<c>IsDeleted = 0</c> numa
/// atualização), então uma exclusão lógica concorrente faz a gravação falhar com concorrência em vez de alterar o registro
/// excluído. Não muda o schema.</para>
/// <para>O índice criado por <see cref="OrmModelConfigurationExtensions.HasSoftDelete{TEntity}"/> recebe aqui o filtro na sintaxe
/// do banco do provedor, com o nome real da coluna (<c>HasColumnName</c> incluído): <c>[IsDeleted] = 0</c> no SQL Server e
/// <c>"IsDeleted" = FALSE</c> no PostgreSQL. MySQL e Oracle não têm índice filtrado: lá o índice fica sem filtro.</para>
/// </remarks>
/// <param name="database">Banco do provedor do EF Core do contexto.</param>
internal sealed class SoftDeleteQueryFilterConvention(OrmDatabase database = OrmDatabase.Other) : IModelFinalizingConvention
{
    /// <summary>Marca (temporária, removida pela convenção) do índice criado pelo <c>HasSoftDelete</c>.</summary>
    internal const string SoftDeleteIndexAnnotation = "TecOrm:SoftDeleteIndex";

    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            ApplySoftDeleteIndexFilter(entityType, database);
            // Exclusão lógica concorrente entre a leitura e o UPDATE: falha de concorrência, não gravação no registro excluído
            if (!entityType.IsOwned() && typeof(ISoftDelete).IsAssignableFrom(entityType.ClrType) &&
                entityType.FindDeclaredProperty(nameof(ISoftDelete.IsDeleted)) is { } isDeleted)
                isDeleted.Builder.IsConcurrencyToken(true);

            // Herança (TPH/TPT): o filtro só pode ficar na raiz. Tipos owned seguem o dono.
            if (entityType.BaseType is not null || entityType.IsOwned() || !typeof(ISoftDelete).IsAssignableFrom(entityType.ClrType) ||
                HasSoftDeleteFilter(entityType))
                continue;

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            Expression body = Expression.Not(Expression.Call(typeof(EF), nameof(EF.Property), [typeof(bool)], parameter,
                Expression.Constant(nameof(ISoftDelete.IsDeleted))));

#pragma warning disable CS0618 // EF 10: GetQueryFilter/SetQueryFilter(lambda) são o filtro anônimo, que pode vir da aplicação
            if (entityType.GetQueryFilter() is { } existing)
            {
                var existingBody = ParameterReplacer.Replace(existing.Body, existing.Parameters[0], parameter);
                // Pelo metadado, não pelo builder: o filtro da aplicação tem origem explícita e o builder de convenção seria ignorado
                entityType.SetQueryFilter(Expression.Lambda(Expression.AndAlso(existingBody, body), parameter));
            }
            else
            {
                entityType.SetQueryFilter(OrmModelConfigurationExtensions.SoftDeleteFilterName, Expression.Lambda(body, parameter));
            }
#pragma warning restore CS0618
        }
    }

    /// <summary>Filtro do índice do <c>HasSoftDelete</c> na sintaxe do banco, com o nome da coluna do modelo.</summary>
    private static void ApplySoftDeleteIndexFilter(IConventionEntityType entityType, OrmDatabase database)
    {
        foreach (var index in entityType.GetDeclaredIndexes().ToList())
        {
            if (index.FindAnnotation(SoftDeleteIndexAnnotation) is null)
                continue;
            index.RemoveAnnotation(SoftDeleteIndexAnnotation);
            if (index.Properties is [var property] && property.GetColumnName() is { } column && IndexFilter(database, column) is { } filter)
                index.Builder.HasFilter(filter);
        }
    }

    /// <summary>
    /// Filtro <c>IsDeleted = falso</c> no SQL do banco; <c>null</c> quando o banco não tem índice filtrado (MySQL, Oracle) ou o
    /// provedor é desconhecido (o índice fica sem filtro, em vez de receber SQL de outro banco).
    /// </summary>
    internal static string? IndexFilter(OrmDatabase database, string column) => database switch
    {
        OrmDatabase.SqlServer => "[" + column.Replace("]", "]]", StringComparison.Ordinal) + "] = 0",
        OrmDatabase.PostgreSql => "\"" + column.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" = FALSE",
        _ => null
    };

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
                "excluídos voltariam nas consultas. Registre o contexto com AddTecOrm (ou AddTecOrmPostgreSql, AddTecOrmMySql, " +
                "AddTecOrmOracle), herde de OrmDbContext ou chame configurationBuilder.AddTecOrmConventions() em ConfigureConventions.");
        return context;
    }

    /// <summary>Algum filtro da entidade (anônimo ou nomeado) já testa <c>IsDeleted</c>.</summary>
    internal static bool HasSoftDeleteFilter(IReadOnlyEntityType entityType) =>
        entityType.GetDeclaredQueryFilters().Any(filter => filter.Expression is { } expression && IsDeletedFinder.Find(expression));

    /// <summary>
    /// Desliga só o filtro de exclusão lógica da consulta (para enxergar os excluídos), mantendo os demais filtros globais da
    /// aplicação.
    /// </summary>
    /// <remarks>
    /// Com o filtro nomeado: <c>IgnoreQueryFilters([SoftDeleteFilterName])</c>. Filtro anônimo: se for só o da convenção, ignora
    /// os filtros; se for o da aplicação combinado pela convenção (<c>filtroDaAplicação &amp;&amp; !IsDeleted</c>), ignora os
    /// filtros e reaplica o da aplicação. Qualquer outra forma (filtro da aplicação que já testa <c>IsDeleted</c> por conta
    /// própria) não é separável com segurança: a consulta fica como está e os excluídos continuam invisíveis.
    /// </remarks>
    internal static IQueryable<TEntity> IgnoreSoftDeleteFilter<TEntity>(IQueryable<TEntity> query, IReadOnlyEntityType entityType, DbContext context)
        where TEntity : class
    {
        var root = entityType.GetRootType();
        if (root.GetDeclaredQueryFilters().Any(filter => filter.Key == OrmModelConfigurationExtensions.SoftDeleteFilterName))
            return query.IgnoreQueryFilters([OrmModelConfigurationExtensions.SoftDeleteFilterName]);

#pragma warning disable CS0618 // EF 10: GetQueryFilter é o filtro anônimo
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
    /// Nome do filtro global de exclusão lógica (filtro nomeado do EF Core 10). Permite desligar só ele numa consulta:
    /// <c>IgnoreQueryFilters([OrmModelConfigurationExtensions.SoftDeleteFilterName])</c>.
    /// </summary>
    public const string SoftDeleteFilterName = "TecOrm.SoftDelete";

    /// <summary>
    /// Adiciona as convenções do TEC.ORM (filtro global de exclusão lógica, colunas de auditoria, tabela da trilha histórica e
    /// collation da idempotência). Chame em <c>ConfigureConventions</c>.
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
        configurationBuilder.Conventions.Add(provider => new AuditModelConvention(TecOrmOptionsExtension.AuditTableNameFrom(Options(provider)),
            OrmDatabaseProvider.Detect(Options(provider))));
        configurationBuilder.Conventions.Add(provider => new SoftDeleteQueryFilterConvention(OrmDatabaseProvider.Detect(Options(provider))));
        configurationBuilder.Conventions.Add(provider => new IdempotencyModelConvention(OrmDatabaseProvider.Detect(Options(provider))));
        return configurationBuilder;
    }

    /// <summary>Configura as colunas de exclusão lógica de uma entidade (padrão <c>false</c> e índice filtrado opcional).</summary>
    /// <remarks>
    /// O índice (<paramref name="index"/>) cobre só a coluna <c>IsDeleted</c>, filtrada por "não excluído" na sintaxe do banco
    /// (SQL Server e PostgreSQL; MySQL e Oracle não têm índice filtrado e o índice fica sem filtro). O banco quase nunca o usa:
    /// prefira <c>index: false</c> e índices das colunas de busca filtrados por "não excluído" (ex., no SQL Server:
    /// <c>HasIndex(c =&gt; c.Email).IsUnique().HasFilter("[IsDeleted] = 0")</c>). O padrão continua <c>true</c> para não gerar
    /// uma migration de remoção nos modelos existentes. O filtro usa o nome real da coluna (<c>HasColumnName</c>), aplicado
    /// pelas convenções do TEC.ORM.
    /// </remarks>
    public static EntityTypeBuilder<TEntity> HasSoftDelete<TEntity>(this EntityTypeBuilder<TEntity> builder, bool index = true)
        where TEntity : class, ISoftDelete
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Property(e => e.IsDeleted).HasDefaultValue(false);
        builder.Property(e => e.DeletedAt);
        // Filtro definido pela convenção ao finalizar o modelo: a coluna pode ser renomeada depois desta chamada
        if (index)
            builder.HasIndex(e => e.IsDeleted).HasAnnotation(SoftDeleteQueryFilterConvention.SoftDeleteIndexAnnotation, true);
        return builder;
    }

    private static IDbContextOptions? Options(IServiceProvider provider) => provider.GetService(typeof(IDbContextOptions)) as IDbContextOptions;
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
