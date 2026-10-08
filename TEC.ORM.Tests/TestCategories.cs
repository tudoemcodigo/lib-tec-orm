namespace TEC.ORM.Tests;

/// <summary>
/// Categorias dos testes (<c>[Category]</c>), num único lugar. A seleção no CI é sempre por categoria
/// (<c>--treenode-filter "/*/*/*/*[Category=...]"</c>): sem categoria = unitários (PR, matriz de TFMs e sem ICU).
/// </summary>
internal static class TestCategories
{
    /// <summary>Dependências reais (SQL Server): job de integração, com o banco em container (e o Key Vault na main).</summary>
    public const string Integration = "Integracao";

    /// <summary>Segurança pesada ([Explicit]): <c>performance.yml</c> e release.</summary>
    public const string HeavySecurity = "Seguranca-Pesada";
}
