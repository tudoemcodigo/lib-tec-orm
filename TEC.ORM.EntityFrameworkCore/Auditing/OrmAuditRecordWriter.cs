using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using TEC.ORM.Auditing;

namespace TEC.ORM.EntityFrameworkCore.Auditing;

/// <summary>
/// Grava registros da trilha fora do rastreador: um <c>INSERT</c> por registro, montado a partir do modelo (tabela, colunas e
/// mapeamento de tipos do provedor) e sempre parametrizado. Usado depois do <c>INSERT</c> de entidades com chave gerada pelo
/// banco e na exclusão física; roda na transação corrente do contexto.
/// </summary>
/// <remarks>
/// Não passa pelo <c>SaveChanges</c> de propósito: um segundo salvamento gravaria também o que mais estivesse pendente no
/// rastreador (inclusive, com <c>acceptAllChangesOnSuccess: false</c>, as próprias entidades de novo).
/// </remarks>
internal static class OrmAuditRecordWriter
{
    public static async Task InsertAsync(DbContext context, IReadOnlyList<OrmAuditRecord> records, bool async, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
            return;

        var entityType = context.Model.FindEntityType(typeof(OrmAuditRecord))
            ?? throw new InvalidOperationException(
                $"A trilha histórica está ligada, mas '{context.GetType().Name}' não mapeia a tabela: chame " +
                "modelBuilder.AddTecOrmHistoricalAudit() no OnModelCreating (ou desligue OrmOptions.EnableHistoricalAudit).");
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        var helper = context.GetService<ISqlGenerationHelper>();

        // Chave gerada pelo banco (identidade/sequência) fica de fora; as demais colunas mapeadas entram
        var properties = entityType.GetProperties()
            .Where(p => !(p.IsPrimaryKey() && p.ValueGenerated != ValueGenerated.Never) && p.GetColumnName(table) is not null)
            .ToList();
        string columns = string.Join(", ", properties.Select(p => helper.DelimitIdentifier(p.GetColumnName(table)!)));
        string placeholders = string.Join(", ", properties.Select((_, i) => "{" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"));
        string sql = $"INSERT INTO {helper.DelimitIdentifier(table.Name, table.Schema)} ({columns}) VALUES ({placeholders})";

        var connection = context.Database.GetDbConnection();
        foreach (var record in records)
        {
            // O comando só fabrica parâmetros do provedor (com o mapeamento e o conversor de cada coluna); não é executado
            using var factory = connection.CreateCommand();
            var parameters = new object[properties.Count];
            for (int i = 0; i < properties.Count; i++)
            {
                var property = properties[i];
                object? value = property.PropertyInfo?.GetValue(record) ?? property.FieldInfo?.GetValue(record);
                parameters[i] = property.GetRelationalTypeMapping().CreateParameter(factory,
                    helper.GenerateParameterName("p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), value, property.IsNullable);
            }

#pragma warning disable EF1002 // identificadores vêm do modelo (delimitados pelo provedor); valores sempre como parâmetros
            if (async)
                await context.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
            else
                context.Database.ExecuteSqlRaw(sql, parameters);
#pragma warning restore EF1002
        }
    }
}
