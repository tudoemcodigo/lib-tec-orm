using System.Text;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.PostgreSql.Dialect;

/// <summary>
/// Validador somente leitura para dialeto PostgreSQL.
/// </summary>
public sealed class PostgreSqlReadOnlySqlValidator : IReadOnlySqlValidator
{
    private static readonly HashSet<string> ForbiddenKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "INTO",
        "CREATE", "ALTER", "DROP", "GRANT", "REVOKE",
        "EXECUTE", "PREPARE", "DEALLOCATE", "CALL", "DO",
        "COPY", "VACUUM", "ANALYZE", "REINDEX", "CLUSTER", "CHECKPOINT",
        "LOCK", "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT", "SET", "RESET",
        "PG_SLEEP", "PG_READ_FILE", "PG_READ_BINARY_FILE", "PG_WRITE_FILE", "PG_WRITE_BINARY_FILE",
        "DBLINK", "DBLINK_EXEC", "LO_IMPORT", "LO_EXPORT"
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

            if (word.Equals("SHARE", StringComparison.OrdinalIgnoreCase) && "FOR".Equals(previous, StringComparison.OrdinalIgnoreCase))
                return "comando de bloqueio não permitido em consulta de leitura (FOR SHARE).";

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
            else if (c == '$' && TryDollarTag(sql, i, out string tag))
            {
                // Literal entre dólares ($$...$$ ou $tag$...$tag$): nada dentro dele é código
                int end = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                if (end < 0) return null;
                i = end + tag.Length;
                output.Append(' ');
            }
            else if (c is '\'' or '"')
            {
                char delimiter = c;
                // A barra invertida só escapa em literal E'...' (standard_conforming_strings): no literal comum e no identificador
                // ela é um caractere qualquer, e tratá-la como escape esconderia o fim real do literal
                bool backslashEscapes = c == '\'' && i > 0 && sql[i - 1] is 'E' or 'e' && (i < 2 || !IsWordChar(sql[i - 2]));
                i++;
                while (i < sql.Length && sql[i] != delimiter)
                {
                    if (backslashEscapes && sql[i] == '\\' && i + 1 < sql.Length) i++;
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

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // $$ ou $tag$ (tag: letra ou _ seguida de letras, dígitos ou _), fora de uma palavra ($1 é parâmetro, não literal)
    private static bool TryDollarTag(string sql, int start, out string tag)
    {
        tag = "";
        if (start > 0 && IsWordChar(sql[start - 1]))
            return false;
        int j = start + 1;
        if (j < sql.Length && (char.IsLetter(sql[j]) || sql[j] == '_'))
        {
            while (j < sql.Length && IsWordChar(sql[j])) j++;
        }
        if (j >= sql.Length || sql[j] != '$')
            return false;
        tag = sql.Substring(start, j - start + 1);
        return true;
    }

    private static IEnumerable<string> Words(string code)
    {
        var sb = new StringBuilder();
        foreach (char c in code)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
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
