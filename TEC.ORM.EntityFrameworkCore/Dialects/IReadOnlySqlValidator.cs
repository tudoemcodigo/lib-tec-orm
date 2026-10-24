namespace TEC.ORM.EntityFrameworkCore.Dialects;

/// <summary>
/// Validador de consultas SQL para garantir conformidade somente-leitura em comandos Dapper.
/// </summary>
public interface IReadOnlySqlValidator
{
    /// <summary>
    /// Verifica se a consulta SQL é estritamente somente-leitura no dialeto específico do banco.
    /// </summary>
    /// <param name="sql">Comando SQL a ser validado.</param>
    /// <returns>Motivo da recusa ou <c>null</c> se o comando for seguro para leitura.</returns>
    string? Check(string sql);
}
