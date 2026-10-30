using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Auditing;
using TEC.ORM.EntityFrameworkCore.Security;
using TEC.ORM.EntityFrameworkCore.SoftDelete;

namespace TEC.ORM.EntityFrameworkCore.Dialects;

/// <summary>
/// Base dos tradutores de exceção dos provedores: o que vale para qualquer banco fica aqui, e o provedor só reconhece e traduz
/// a exceção nativa do driver.
/// </summary>
/// <remarks>
/// <para>Ordem da tradução:</para>
/// <list type="number">
/// <item><description>recusas do próprio TEC.ORM (conexão recusada pela política ou pelo circuito, auditoria sem identidade,
/// limite da propagação da exclusão lógica), com o erro original;</description></item>
/// <item><description><c>DbUpdateConcurrencyException</c> (concorrência otimista do EF Core): <c>ORM_CONCORRENCIA</c>;</description></item>
/// <item><description>a exceção do driver, procurada também nas <c>InnerException</c> (o EF Core embrulha o erro do banco em
/// <c>DbUpdateException</c>): <see cref="TranslateProviderException"/>;</description></item>
/// <item><description><see cref="TimeoutException"/>: <c>ORM_TEMPO_ESGOTADO</c>; falha marcada na abertura (pool esgotado):
/// <c>ORM_CONEXAO_INDISPONIVEL</c>; o resto, <c>ORM_FALHA</c>.</description></item>
/// </list>
/// </remarks>
public abstract class OrmExceptionTranslatorBase : IOrmExceptionTranslator
{
    /// <summary>Profundidade máxima percorrida nas <c>InnerException</c> (proteção contra cadeias cíclicas ou enormes).</summary>
    private const int MaxDepth = 16;

    /// <inheritdoc />
    public OrmDbExceptionTranslation Translate(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (TranslateOrmRefusal(exception) is { } refusal)
            return refusal;

        var provider = FindProviderException(exception);
        if (Find<DbUpdateConcurrencyException>(exception) is not null)
            return new OrmDbExceptionTranslation(OrmErrors.Concurrency(), false, provider is null ? null : GetErrorCode(provider));
        if (provider is not null)
            return TranslateProviderException(provider);
        if (Find<TimeoutException>(exception) is not null)
            return new OrmDbExceptionTranslation(OrmErrors.Timeout(), true, null);
        if (OrmConnectionFailures.IsMarked(exception))
            return new OrmDbExceptionTranslation(OrmErrors.ConnectionUnavailable(), true, null);
        return new OrmDbExceptionTranslation(OrmErrors.Failure(), false, null);
    }

    /// <inheritdoc />
    public bool IsTransient(Exception exception) =>
        exception is not null && FindProviderException(exception) is { } provider && IsTransientProviderException(provider);

    /// <inheritdoc />
    public bool IsConnectionFailure(Exception exception)
    {
        if (exception is null)
            return false;
        return FindProviderException(exception) is { } provider
            ? IsConnectionFailureProviderException(provider)
            : OrmConnectionFailures.IsMarked(exception);
    }

    /// <inheritdoc />
    public bool IsUniqueViolation(Exception exception) =>
        exception is not null && FindProviderException(exception) is { } provider && IsUniqueViolationProviderException(provider);

    /// <summary>Indica se a exceção é a do driver deste provedor (ex.: <c>SqlException</c>, <c>PostgresException</c>).</summary>
    protected abstract bool IsProviderException(Exception exception);

    /// <summary>Traduz a exceção do driver (já encontrada na cadeia de <c>InnerException</c>).</summary>
    protected abstract OrmDbExceptionTranslation TranslateProviderException(Exception providerException);

    /// <summary>Falha transitória do driver que justifica nova tentativa de uma leitura. Padrão: <c>false</c>.</summary>
    protected virtual bool IsTransientProviderException(Exception providerException) => false;

    /// <summary>Falha de conexão do driver (conta no circuit breaker). Padrão: <c>false</c>.</summary>
    protected virtual bool IsConnectionFailureProviderException(Exception providerException) => false;

    /// <summary>Violação de chave única ou primária no driver. Padrão: <c>false</c>.</summary>
    protected virtual bool IsUniqueViolationProviderException(Exception providerException) => false;

    /// <summary>Código nativo do erro (número ou SQLSTATE), sem a mensagem. Padrão: <c>null</c>.</summary>
    protected virtual string? GetErrorCode(Exception providerException) => null;

    /// <summary>A exceção do driver na própria exceção ou nas <c>InnerException</c>; <c>null</c> se não houver.</summary>
    protected Exception? FindProviderException(Exception exception)
    {
        int depth = 0;
        for (var current = exception; current is not null && depth < MaxDepth; current = current.InnerException, depth++)
        {
            if (IsProviderException(current))
                return current;
        }
        return null;
    }

    /// <summary>A primeira exceção do tipo <typeparamref name="T"/> na cadeia de <c>InnerException</c>.</summary>
    internal static T? Find<T>(Exception exception) where T : Exception
    {
        int depth = 0;
        for (var current = exception; current is not null && depth < MaxDepth; current = current.InnerException, depth++)
        {
            if (current is T match)
                return match;
        }
        return null;
    }

    /// <summary>
    /// Exceção de banco ou de conexão: não tem a mensagem registrada (pode conter valores de colunas, servidor ou usuário).
    /// </summary>
    internal static bool IsDatabaseException(Exception exception) =>
        exception is DbUpdateException || Find<OrmConnectionException>(exception) is not null || Find<DbException>(exception) is not null ||
        OrmConnectionFailures.IsMarked(exception);

    /// <summary>Recusa esperada do TEC.ORM (auditoria sem identidade, teto da exclusão lógica): não é falha de programação.</summary>
    internal static bool IsOrmRefusal(Exception exception) =>
        Find<OrmAuditException>(exception) is not null || Find<OrmSoftDeletePropagationException>(exception) is not null;

    private static OrmDbExceptionTranslation? TranslateOrmRefusal(Exception exception)
    {
        if (Find<OrmConnectionException>(exception) is { } connection)
            return new OrmDbExceptionTranslation(connection.Error, true, null);
        if (Find<OrmAuditException>(exception) is { } audit)
            return new OrmDbExceptionTranslation(audit.Error, false, null);
        if (Find<OrmSoftDeletePropagationException>(exception) is { } limit)
            return new OrmDbExceptionTranslation(limit.Error, false, null);
        return null;
    }
}

/// <summary>
/// Tradutor usado sem dialeto (opções montadas à mão, testes): reconhece só as exceções do EF Core, do TEC.ORM e um
/// <see cref="DbException"/> genérico (falha de infraestrutura sem classificação).
/// </summary>
internal sealed class GenericExceptionTranslator : OrmExceptionTranslatorBase
{
    public static readonly GenericExceptionTranslator Instance = new();

    protected override bool IsProviderException(Exception exception) => exception is DbException;

    protected override OrmDbExceptionTranslation TranslateProviderException(Exception providerException) =>
        new(OrmErrors.Failure(), true, null);
}
