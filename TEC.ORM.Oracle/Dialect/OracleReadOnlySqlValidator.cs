using System.Text;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.Oracle.Dialect;

/// <summary>
/// Validador somente leitura para dialeto Oracle Database.
/// </summary>
public sealed class OracleReadOnlySqlValidator : IReadOnlySqlValidator
{
    private static readonly HashSet<string> ForbiddenKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "INTO",
        "CREATE", "ALTER", "DROP", "GRANT", "REVOKE",
        "EXEC", "EXECUTE", "DECLARE", "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT",
        "LOCK", "CALL"
    };

    /// <inheritdoc />
    public string? Check(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "SQL vazio.";

        var code = StripLiteralsAndComments(sql);
        if (code == null)
            return "SQL malformado (comentário ou delimitador não fechado).";

        string? first = null;
        string? previous = null;
        foreach (var word in Words(code))
        {
            first ??= word;
            if (ForbiddenKeywords.Contains(word))
                return $"comando não permitido em consulta de leitura ({word.ToUpperInvariant()}).";

            if (word.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) && "FOR".Equals(previous, StringComparison.OrdinalIgnoreCase))
                return "comando de bloqueio não permitido em consulta de leitura (FOR UPDATE).";

            if (word.StartsWith("DBMS_", StringComparison.OrdinalIgnoreCase))
                return $"chamada a pacote de sistema não permitida em consulta de leitura ({word.ToUpperInvariant()}).";

            previous = word;
        }

        if (first == null || !(first.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || first.Equals("WITH", StringComparison.OrdinalIgnoreCase)))
            return "a consulta deve começar com SELECT ou WITH.";

        int semicolon = code.IndexOf(';', StringComparison.Ordinal);
        if (semicolon >= 0 && !string.IsNullOrWhiteSpace(code.AsSpan(semicolon + 1).ToString().Replace(';', ' ')))
            return "mais de um comando na mesma consulta.";

        return null;
    }

    private static string? StripLiteralsAndComments(string sql)
    {
        var output = new StringBuilder(sql.Length);
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            char next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                int end = sql.IndexOfAny(['\r', '\n'], i);
                i = end < 0 ? sql.Length : end;
                output.Append(' ');
            }
            else if (c == '/' && next == '*')
            {
                int depth = 1;
                i += 2;
                while (i < sql.Length && depth > 0)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                if (depth > 0) return null;
                output.Append(' ');
            }
            else if (c is '\'' or '"')
            {
                char delimiter = c;
                i++;
                while (i < sql.Length && sql[i] != delimiter)
                {
                    if (sql[i] == '\\' && i + 1 < sql.Length) i++;
                    i++;
                }
                if (i >= sql.Length) return null;
                i++;
                output.Append(' ');
            }
            else
            {
                output.Append(c);
                i++;
            }
        }
        return output.ToString();
    }

    private static IEnumerable<string> Words(string code)
    {
        var sb = new StringBuilder();
        foreach (char c in code)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.')
                sb.Append(c);
            else if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }
        if (sb.Length > 0)
            yield return sb.ToString();
    }
}
