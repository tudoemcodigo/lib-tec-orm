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
public interface IOrmExceptionTranslator
{
    /// <summary>Traduz a exceção no erro de domínio correspondente.</summary>
    OrmDbExceptionTranslation Translate(Exception exception);

    /// <summary>Indica se a exceção representa um erro transitório que justifica nova tentativa em leituras.</summary>
    bool IsTransient(Exception exception);

    /// <summary>Indica se a falha decorreu de indisponibilidade ou queda de conexão.</summary>
    bool IsConnectionFailure(Exception exception);
}
