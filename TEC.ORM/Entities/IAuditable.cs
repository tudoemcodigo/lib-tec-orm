namespace TEC.ORM.Entities;

/// <summary>
/// Auditoria de autoria: quem, de qual tenant e quando criou, alterou e excluiu logicamente a entidade. Os campos são preenchidos
/// pelo ORM ao salvar, a partir do <c>ICurrentUser</c> (TEC.Core) da operação; a aplicação não os altera.
/// </summary>
/// <remarks>
/// <para>Os "por" guardam <c>ICurrentUser.Id</c>: identificador estável (ex.: <c>oid</c> do Entra ID, <c>apikey:{id}</c>,
/// <c>system:{nome}</c>), nunca nome ou e-mail (que mudam e são dados pessoais).</para>
/// <para>Os "por tenant" guardam <c>ICurrentUser.TenantId</c> no momento da operação (<c>null</c> se a identidade não tem tenant).
/// São apenas informativos: os registros são globais e o ORM <b>não</b> filtra nem restringe linhas por tenant.</para>
/// <para>Falha fechada: sem identidade autenticada, gravar uma entidade auditada falha com <c>ORM_AUDITORIA_SEM_IDENTIDADE</c>
/// em vez de gravar "anônimo".</para>
/// <para>Só há <c>get</c>: a implementação grava pelos metadados do ORM (setter privado ou campo), como em <see cref="ISoftDelete"/>.</para>
/// </remarks>
public interface IAuditable
{
    /// <summary>Quando foi criada (UTC).</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>Id de quem criou.</summary>
    string? CreatedBy { get; }

    /// <summary>Tenant de quem criou, no momento da criação.</summary>
    string? CreatedByTenant { get; }

    /// <summary>Quando foi alterada pela última vez (UTC); <c>null</c> se nunca foi.</summary>
    DateTimeOffset? UpdatedAt { get; }

    /// <summary>Id de quem alterou pela última vez.</summary>
    string? UpdatedBy { get; }

    /// <summary>Tenant de quem alterou pela última vez, no momento da alteração.</summary>
    string? UpdatedByTenant { get; }

    /// <summary>Id de quem excluiu logicamente (o instante fica em <see cref="ISoftDelete.DeletedAt"/>).</summary>
    string? DeletedBy { get; }

    /// <summary>Tenant de quem excluiu logicamente, no momento da exclusão.</summary>
    string? DeletedByTenant { get; }
}
