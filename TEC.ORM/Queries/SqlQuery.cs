using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using TEC.Core.Common.Guards;

namespace TEC.ORM.Queries;

/// <summary>
/// Consulta SQL de leitura <b>sempre parametrizada</b>. É a única forma de enviar SQL às leituras complexas
/// (<see cref="Abstractions.IOrmQueryExecutor"/>): não existe sobrecarga que receba texto solto.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Interpolated"/> transforma cada valor interpolado em parâmetro (<c>@p0</c>, <c>@p1</c>...): o valor nunca entra
/// no texto do SQL, então <c>$"... WHERE Nome = {nome}"</c> é seguro mesmo com <c>nome = "' OR 1=1 --"</c>. Nunca monte
/// o texto antes (<c>string.Format</c>, concatenação ou interpolação em <c>string</c>) e passe já pronto.
/// </para>
/// <para>
/// <see cref="Name"/> identifica a consulta em logs, traces e métricas (baixa cardinalidade, ex.: <c>relatorio.vendas-por-cliente</c>).
/// O texto do SQL e os valores dos parâmetros nunca são registrados; <see cref="ToString"/> também os omite.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var query = SqlQuery.Interpolated("pedidos.por-cliente",
///     $"SELECT p.Id, p.Valor, c.Nome FROM Pedidos p JOIN Clientes c ON c.Id = p.ClienteId WHERE c.Id = {clienteId}");
///
/// var totais = SqlQuery.Create("vendas.totais", "SELECT SUM(Valor) FROM Pedidos WHERE CriadoEm >= @Inicio", new { Inicio = inicio });
/// </code>
/// </example>
[DebuggerDisplay("{ToString(),nq}")]
public sealed partial class SqlQuery
{
    /// <summary>Tamanho máximo do texto do SQL.</summary>
    public const int MaxSqlLength = 32_768;

    /// <summary>Quantidade máxima de parâmetros (limite do SQL Server: 2100).</summary>
    public const int MaxParameters = 2_000;

    private SqlQuery(string name, string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        Name = name;
        Sql = sql;
        Parameters = parameters;
    }

    /// <summary>Nome da consulta para observabilidade (letras, dígitos, <c>.</c>, <c>-</c> e <c>_</c>; até 100 caracteres).</summary>
    public string Name { get; }

    /// <summary>Texto do SQL, com marcadores de parâmetro (<c>@nome</c>).</summary>
    public string Sql { get; }

    /// <summary>Parâmetros (nome sem <c>@</c> → valor), somente leitura: não podem ser trocados depois da validação.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>Cria a consulta a partir de uma string interpolada; cada valor interpolado vira um parâmetro.</summary>
    public static SqlQuery Interpolated(string name, FormattableString sql)
    {
        ValidateName(name);
        Guard.NotNull(sql);

        var arguments = sql.GetArguments();
        Guard.Against(arguments.Length > MaxParameters, $"A consulta excede {MaxParameters} parâmetros.", nameof(sql));
        var placeholders = new object[arguments.Length];
        var parameters = new Dictionary<string, object?>(arguments.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < arguments.Length; i++)
        {
            string parameterName = "p" + i.ToString(CultureInfo.InvariantCulture);
            placeholders[i] = "@" + parameterName;
            parameters[parameterName] = arguments[i];
        }

        string text = string.Format(CultureInfo.InvariantCulture, sql.Format, placeholders);
        return new SqlQuery(name, ValidateSql(text), new ReadOnlyDictionary<string, object?>(parameters));
    }

    /// <summary>
    /// Cria a consulta com SQL <b>constante</b> e um objeto de parâmetros (propriedades públicas → <c>@Propriedade</c>).
    /// </summary>
    public static SqlQuery Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TParameters>(
        string name, string sql, TParameters parameters) where TParameters : class
    {
        Guard.NotNull(parameters);
        // Um Dictionary<string, object?> resolve para esta sobrecarga genérica (conversão de identidade vence a de interface):
        // encaminha para a de dicionário em vez de ler as propriedades do próprio dicionário (Count, Keys...)
        if (parameters is IReadOnlyDictionary<string, object?> dictionary)
            return Create(name, sql, dictionary);

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in typeof(TParameters).GetProperties())
        {
            if (property.GetIndexParameters().Length == 0 && property.CanRead)
                values[property.Name] = property.GetValue(parameters);
        }
        return Create(name, sql, (IReadOnlyDictionary<string, object?>)values);
    }

    /// <summary>Cria a consulta com SQL <b>constante</b> e parâmetros nomeados (nome sem <c>@</c>).</summary>
    public static SqlQuery Create(string name, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ValidateName(name);
        var copy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (parameters is not null)
        {
            Guard.Against(parameters.Count > MaxParameters, $"A consulta excede {MaxParameters} parâmetros.", nameof(parameters));
            foreach (var (key, value) in parameters)
            {
                Guard.Against(key is null || !ParameterNamePattern().IsMatch(key), "Nome de parâmetro inválido.", nameof(parameters));
                copy[key!] = value;
            }
        }
        return new SqlQuery(name, ValidateSql(sql), new ReadOnlyDictionary<string, object?>(copy));
    }

    /// <inheritdoc />
    /// <remarks>Omite o SQL e os valores dos parâmetros.</remarks>
    public override string ToString() => $"SqlQuery {{ Name = {Name}, Parameters = {Parameters.Count} }}";

    private static void ValidateName(string name)
    {
        Guard.NotNullOrWhiteSpace(name);
        Guard.Against(!NamePattern().IsMatch(name), "O nome da consulta aceita letras, dígitos, '.', '-' e '_' (até 100 caracteres).", nameof(name));
    }

    private static string ValidateSql(string sql)
    {
        Guard.NotNullOrWhiteSpace(sql);
        Guard.Against(sql.Length > MaxSqlLength, $"O SQL excede {MaxSqlLength} caracteres.", nameof(sql));
        return sql;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,99}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ParameterNamePattern();
}
