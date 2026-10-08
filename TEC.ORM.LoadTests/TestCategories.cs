namespace TEC.ORM.LoadTests;

/// <summary>
/// Categorias dos testes de carga (<c>[Category]</c>), num único lugar. A seleção no CI é sempre por categoria
/// (<c>--treenode-filter "/*/*/*/*[Category=...]"</c>).
/// </summary>
public static class TestCategories
{
    /// <summary>Concorrência e fumaça de carga (segundos): CI a cada PR, no job com SQL Server.</summary>
    public const string LoadCi = "Carga-CI";

    /// <summary>
    /// Carga pesada ([Explicit]): soak, volume, carga sustentada e tempestade no pool (<c>performance.yml</c> e release). Também é
    /// a chave de [NotInParallel] deles: rodam um de cada vez, porque medem memória, conexões e vazão do processo inteiro.
    /// </summary>
    public const string LoadHeavy = "Carga-Pesada";
}
