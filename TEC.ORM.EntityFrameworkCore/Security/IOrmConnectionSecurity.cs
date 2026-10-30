using System.Data.Common;
using TEC.Core.Common.Results;
using TEC.ORM.EntityFrameworkCore.Configuration;

namespace TEC.ORM.EntityFrameworkCore.Security;

/// <summary>Tipo de conexão.</summary>
public enum OrmConnectionKind
{
    /// <summary>Leitura e escrita (EF Core): segredo <see cref="OrmOptions.ConnectionSecretName"/>.</summary>
    ReadWrite = 0,

    /// <summary>
    /// Somente leitura (Dapper): segredo <see cref="OrmOptions.ReadOnlyConnectionSecretName"/>, fora do <c>TransactionScope</c>
    /// ambiente e, no SQL Server, com <c>ApplicationIntent=ReadOnly</c> (direciona para réplicas de leitura em Always On).
    /// </summary>
    ReadOnly = 1
}

/// <summary>
/// Gerencia a conexão com o banco de forma segura: a string de conexão vem <b>só</b> do TEC.Vault, existe apenas em memória
/// durante a abertura e nunca é devolvida, registrada ou guardada.
/// </summary>
/// <remarks>Ponto de extensão: substitua no DI (ex.: autenticação por token do Entra ID em vez de usuário e senha).</remarks>
public interface IOrmConnectionSecurity
{
    /// <summary>Cria e abre uma conexão do provedor. O chamador descarta.</summary>
    Task<Result<DbConnection>> OpenConnectionAsync(OrmConnectionKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// Aplica a string de conexão do cofre a uma conexão fechada que ainda não tem uma (usado pelo interceptor do EF Core antes
    /// de abrir). Conexão que já tem string de conexão não é alterada.
    /// </summary>
    /// <remarks>
    /// <b>Segurança:</b> uma conexão que já chega com servidor é considerada configurada pela aplicação e passa <b>sem</b> a
    /// política do segredo (criptografia obrigatória, certificado validado, <c>Persist Security Info=False</c>). É intencional
    /// (ex.: testes, ferramentas, conexão entregue pela aplicação ao <c>UseSqlServer(connection)</c>), mas a responsabilidade pela
    /// segurança dessa conexão é de quem a criou.
    /// </remarks>
    Task<Result> ConfigureConnectionAsync(DbConnection connection, OrmConnectionKind kind, CancellationToken cancellationToken);
}
