using System.Text;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.MySql.Dialect;

/// <summary>
/// Validador somente leitura para dialeto MySQL / MariaDB.
/// </summary>
public sealed class MySqlReadOnlySqlValidator : IReadOnlySqlValidator
{
    private static readonly HashSet<string> ForbiddenKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "REPLACE", "MERGE", "TRUNCATE", "INTO",
        "CREATE", "ALTER", "DROP", "GRANT", "REVOKE",
        "EXECUTE", "PREPARE", "DEALLOCATE", "CALL",
        "LOCK", "UNLOCK", "BEGIN", "COMMIT", "ROLLBACK", "SET",
        "LOAD", "LOAD_FILE", "SLEEP", "BENCHMARK"
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
        string? beforePrevious = null;
        foreach (var word in Words(code))
        {
            first ??= word;
            if (ForbiddenKeywords.Contains(word))
                return $"comando não permitido em consulta de leitura ({word.ToUpperInvariant()}).";

            if (word.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) && "FOR".Equals(previous, StringComparison.OrdinalIgnoreCase))
                return "comando de bloqueio não permitido em consulta de leitura (FOR UPDATE).";

            if (word.Equals("MODE", StringComparison.OrdinalIgnoreCase) && "SHARE".Equals(previous, StringComparison.OrdinalIgnoreCase) && "IN".Equals(beforePrevious, StringComparison.OrdinalIgnoreCase))
                return "comando de bloqueio não permitido em consulta de leitura (LOCK IN SHARE MODE).";

            beforePrevious = previous;
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

            // No MySQL "--" só abre comentário quando seguido de espaço ou controle (ou do fim): "1--1" é uma subtração
            if ((c == '-' && next == '-' && (i + 2 >= sql.Length || char.IsWhiteSpace(sql[i + 2]) || char.IsControl(sql[i + 2]))) || c == '#')
            {
                int end = sql.IndexOfAny(['\r', '\n'], i);
                i = end < 0 ? sql.Length : end;
                output.Append(' ');
            }
            else if (c == '/' && next == '*')
            {
                // /*! ... */ e /*M! ... */ são comentários que o servidor EXECUTA: recusados em vez de ignorados
                if (i + 2 < sql.Length && (sql[i + 2] == '!' || (sql[i + 2] == 'M' && i + 3 < sql.Length && sql[i + 3] == '!')))
                    return null;
                // Comentário de bloco do MySQL não aninha: termina no primeiro "*/"
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return null;
                i = end + 2;
                output.Append(' ');
            }
            else if (c is '\'' or '"' or '`')
            {
                char delimiter = c;
                i++;
                while (i < sql.Length && sql[i] != delimiter)
                {
                    if (sql[i] == '\\' && delimiter != '`' && i + 1 < sql.Length) i++;
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
