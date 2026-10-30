using TEC.Core.Common.Results;

namespace TEC.ORM.EntityFrameworkCore.Dialects;

/// <summary>
/// Resultado da tradução de uma exceção de banco de dados em erro padronizado do TEC.ORM.
/// </summary>
/// <param name="Error">Erro padronizado retornado ao consumidor.</param>
/// <param name="IsInfrastructure">Indica se a falha é de infraestrutura/conexão ou de regra de dados/concorrência.</param>
/// <param name="ErrorCode">Código nativo de erro do provedor (ex.: número do erro ou SQLSTATE).</param>
public readonly record struct OrmDbExceptionTranslation(Error Error, bool IsInfrastructure, string? ErrorCode);

/// <summary>
/// Tradutor de exceções nativas de banco de dados para os erros padronizados de <see cref="TEC.ORM.Common.OrmErrors"/>.
/// </summary>
/// <remarks>
/// Os tradutores dos provedores derivam de <see cref="OrmExceptionTranslatorBase"/>, que percorre as <c>InnerException</c>
/// (o EF Core embrulha o erro do banco em <c>DbUpdateException</c>) e trata concorrência otimista, tempo esgotado e as
/// recusas do próprio TEC.ORM.
/// </remarks>
public interface IOrmExceptionTranslator
{
    /// <summary>Traduz a exceção no erro de domínio correspondente.</summary>
    OrmDbExceptionTranslation Translate(Exception exception);

    /// <summary>Indica se a exceção representa um erro transitório que justifica nova tentativa em leituras.</summary>
    bool IsTransient(Exception exception);

    /// <summary>Indica se a falha decorreu de indisponibilidade ou queda de conexão.</summary>
    bool IsConnectionFailure(Exception exception);

    /// <summary>
    /// Indica se a exceção é uma violação de chave única ou primária (e só ela; chave estrangeira não conta). Usado pela reserva
    /// de idempotência, que trata só esse caso como "já existe". Padrão: <c>false</c> (a exceção sobe).
    /// </summary>
    bool IsUniqueViolation(Exception exception) => false;
}
