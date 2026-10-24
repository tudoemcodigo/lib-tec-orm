using System.Data.Common;
using TEC.Core.Common.Results;

namespace TEC.ORM.EntityFrameworkCore.Security;

/// <summary>Tipo de conexão.</summary>
public enum OrmConnectionKind
{
    /// <summary>Leitura e escrita (EF Core).</summary>
    ReadWrite = 0,

    /// <summary>Somente leitura (Dapper).</summary>
    ReadOnly = 1
}

/// <summary>
/// Gerencia a conexão com o banco de dados de forma segura, obtendo a string do cofre e aplicando as diretivas.
/// </summary>
public interface IOrmConnectionSecurity
{
    /// <summary>Cria e abre uma conexão gerenciada. O chamador descarta.</summary>
    Task<Result<DbConnection>> OpenConnectionAsync(OrmConnectionKind kind, CancellationToken cancellationToken);

    /// <summary>Aplica a string de conexão do cofre a uma conexão fechada.</summary>
    Task<Result> ConfigureConnectionAsync(DbConnection connection, OrmConnectionKind kind, CancellationToken cancellationToken);
}
