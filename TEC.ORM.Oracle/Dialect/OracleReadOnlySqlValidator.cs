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

            if (word.StartsWith("DBMS_", StringComparison.OrdinalIgnoreCase) || word.StartsWith("UTL_", StringComparison.OrdinalIgnoreCase))
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
                // Comentário de bloco do Oracle não aninha: termina no primeiro "*/"
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return null;
                i = end + 2;
                output.Append(' ');
            }
            else if (c is 'q' or 'Q' && next == '\'' && i + 2 < sql.Length && !(i > 0 && IsWordChar(sql[i - 1])))
            {
                // Literal alternativo q'[...]' / q'!...!': termina no delimitador seguido de aspa
                char open = sql[i + 2];
                char close = open switch { '[' => ']', '(' => ')', '{' => '}', '<' => '>', _ => open };
                int end = sql.IndexOf(close + "'", i + 3, StringComparison.Ordinal);
                if (end < 0) return null;
                i = end + 2;
                output.Append(' ');
            }
            else if (c is '\'' or '"')
            {
                char delimiter = c;
                i++;
                while (i < sql.Length && sql[i] != delimiter)
                {
                    i++;   // no Oracle a barra invertida não escapa; a aspa é duplicada ('')
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

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';

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
