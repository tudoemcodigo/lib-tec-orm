using Microsoft.EntityFrameworkCore.Infrastructure;

namespace TEC.ORM.EntityFrameworkCore.Dialects;

/// <summary>Banco do provedor do EF Core configurado no contexto (para o SQL que vai no modelo: filtro de índice, collation).</summary>
internal enum OrmDatabase
{
    /// <summary>Provedor não relacional (InMemory) ou desconhecido: nada específico de banco é aplicado.</summary>
    Other = 0,

    /// <summary>Microsoft SQL Server / Azure SQL.</summary>
    SqlServer,

    /// <summary>PostgreSQL (Npgsql).</summary>
    PostgreSql,

    /// <summary>MySQL / MariaDB (MySql.EntityFrameworkCore ou Pomelo).</summary>
    MySql,

    /// <summary>Oracle Database.</summary>
    Oracle
}

/// <summary>
/// Descobre o banco pelo assembly da extensão do provedor do EF Core (o mesmo critério do <c>Database.ProviderName</c>). Vale
/// também para contextos sem o registro do TEC.ORM (ex.: <c>OrmDbContext</c> numa fábrica de tempo de projeto).
/// </summary>
internal static class OrmDatabaseProvider
{
    public static OrmDatabase Detect(IDbContextOptions? options)
    {
        if (options is null)
            return OrmDatabase.Other;

        foreach (var extension in options.Extensions)
        {
            if (!extension.Info.IsDatabaseProvider)
                continue;
            return extension.GetType().Assembly.GetName().Name switch
            {
                "Microsoft.EntityFrameworkCore.SqlServer" => OrmDatabase.SqlServer,
                "Npgsql.EntityFrameworkCore.PostgreSQL" => OrmDatabase.PostgreSql,
                "MySql.EntityFrameworkCore" or "Pomelo.EntityFrameworkCore.MySql" => OrmDatabase.MySql,
                "Oracle.EntityFrameworkCore" => OrmDatabase.Oracle,
                _ => OrmDatabase.Other
            };
        }
        return OrmDatabase.Other;
    }
}
