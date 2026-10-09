using System.Diagnostics.CodeAnalysis;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.ORM.Common;

namespace TEC.ORM.SqlServer.Internal;

/// <summary>
/// Converte, no pipeline do TEC.Cqrs (e no <c>UseTecExceptionHandler</c>), as exceções de banco que o cliente pode resolver
/// repetindo a operação: concorrência otimista e deadlock viram <c>ORM_CONCORRENCIA</c> (409); violação de chave única ou de
/// chave estrangeira vira <c>ORM_CONFLITO</c> (409).
/// </summary>
/// <remarks>
/// Cobre o caminho que não passa pelo <c>IOrmRepository</c>: o <c>SaveChangesAsync</c> do
/// <c>IUnitOfWork.CommitAsync</c> (chamado pelo <c>TransactionBehavior</c>) e o código que usa o <c>DbContext</c> direto. Sem
/// ele, essas exceções subiam cruas (HTTP 500). As demais falhas de banco não são mapeadas: continuam como erro interno.
/// </remarks>
internal sealed class OrmExceptionErrorMapper : IExceptionErrorMapper
{
    public bool TryMap(Exception exception, [NotNullWhen(true)] out IReadOnlyList<Error>? errors)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (OrmExceptionTranslator.IsDatabaseException(exception))
        {
            var translation = OrmExceptionTranslator.Translate(exception);
            if (translation.Error.Code is OrmErrors.ConcurrencyCode or OrmErrors.ConflictCode)
            {
                errors = [translation.Error];
                return true;
            }
        }

        errors = null;
        return false;
    }
}
