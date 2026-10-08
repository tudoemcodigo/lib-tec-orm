using TEC.Core.Common.Results;
using TEC.ORM.Common;

namespace TEC.ORM.Paging;

/// <summary>Pedido de página da listagem.</summary>
/// <param name="Page">Página (a partir de 1).</param>
/// <param name="PageSize">Itens por página (1 até o limite da implementação).</param>
/// <param name="SortBy">
/// Nome de uma propriedade mapeada da entidade (sem diferenciar maiúsculas). Pode vir direto da requisição: a implementação
/// só aceita propriedades que existem no modelo, e nunca concatena o texto em SQL.
/// </param>
/// <param name="Descending">Ordem decrescente de <paramref name="SortBy"/>.</param>
public sealed record PageRequest(int Page = 1, int PageSize = 20, string? SortBy = null, bool Descending = false)
{
    /// <summary>Tamanho máximo aceito para <see cref="SortBy"/>.</summary>
    public const int MaxSortByLength = 128;

    /// <summary>Quantidade de itens a pular.</summary>
    public int Offset => (Page - 1) * PageSize;

    /// <summary>Valida a página contra o limite de itens da implementação; <c>null</c> se válida.</summary>
    public Error? Validate(int maxPageSize)
    {
        if (Page < 1)
            return OrmErrors.InvalidInput("page", "A página deve ser maior ou igual a 1.");
        if (PageSize < 1 || PageSize > maxPageSize)
            return OrmErrors.InvalidInput("pageSize", $"O tamanho da página deve estar entre 1 e {maxPageSize}.");
        // (Page - 1) * PageSize não pode estourar int
        if (Page - 1 > (int.MaxValue - PageSize) / PageSize)
            return OrmErrors.InvalidInput("page", "Página fora do limite.");
        if (SortBy is not null && (SortBy.Length == 0 || SortBy.Length > MaxSortByLength))
            return OrmErrors.InvalidInput("sortBy", "Ordenação inválida.");
        return null;
    }
}
