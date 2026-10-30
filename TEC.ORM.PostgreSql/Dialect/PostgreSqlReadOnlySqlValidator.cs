using System.Text;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.PostgreSql.Dialect;

/// <summary>
/// Verificação de somente leitura do PostgreSQL. Segunda barreira: a leitura já roda numa transação <c>READ ONLY</c> e a
/// proteção principal continua sendo o login da conexão de leitura com permissão só de <c>SELECT</c>.
/// </summary>
/// <remarks>
/// <para>Ignora o conteúdo de literais (<c>'...'</c>, <c>E'...'</c>, <c>$$...$$</c>/<c>$tag$...$tag$</c>) e comentários
/// (<c>--</c>, <c>/* */</c> aninhados). Exige que o SQL comece com <c>SELECT</c> ou <c>WITH</c> e recusa um segundo comando depois
/// de <c>;</c>, as palavras de escrita/sessão em qualquer posição, os bloqueios (<c>FOR UPDATE</c>, <c>FOR SHARE</c>,
/// <c>FOR KEY SHARE</c>) e funções com efeito colateral (ex.: <c>pg_sleep</c>, <c>set_config</c>, <c>nextval</c>,
/// <c>setval</c>, <c>pg_advisory_*</c>, <c>pg_terminate_backend</c>, <c>dblink</c>, <c>lo_import</c>), inclusive com o nome
/// entre aspas (<c>"pg_sleep"(5)</c>) ou qualificado (<c>pg_catalog.pg_sleep</c>). Identificador com escape Unicode
/// (<c>U&amp;"..."</c>) é recusado: esconderia o nome real.</para>
/// <para>Uma lista de nomes não alcança funções criadas pela aplicação ou sinônimos de esquema: por isso a transação somente
/// leitura e o login só com <c>SELECT</c>.</para>
/// </remarks>
public sealed class PostgreSqlReadOnlySqlValidator : IReadOnlySqlValidator
{
    /// <summary>Comandos e cláusulas de escrita, DDL e sessão (recusados fora de aspas).</summary>
    private static readonly HashSet<string> ForbiddenKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "INTO",
        "CREATE", "ALTER", "DROP", "GRANT", "REVOKE",
        "EXECUTE", "PREPARE", "DEALLOCATE", "CALL", "DO",
        "COPY", "VACUUM", "ANALYZE", "REINDEX", "CLUSTER", "CHECKPOINT",
        "LOCK", "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT", "SET", "RESET"
    };

    /// <summary>Funções com efeito colateral (recusadas também entre aspas).</summary>
    private static readonly HashSet<string> ForbiddenFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "PG_SLEEP", "PG_SLEEP_FOR", "PG_SLEEP_UNTIL",
        "PG_READ_FILE", "PG_READ_BINARY_FILE", "PG_WRITE_FILE", "PG_WRITE_BINARY_FILE",
        "DBLINK", "DBLINK_EXEC", "DBLINK_CONNECT", "DBLINK_SEND_QUERY",
        "LO_IMPORT", "LO_EXPORT", "LO_UNLINK", "LO_CREATE", "LO_FROM_BYTEA", "LO_PUT",
        "SET_CONFIG", "NEXTVAL", "SETVAL",
        "PG_TERMINATE_BACKEND", "PG_CANCEL_BACKEND", "PG_RELOAD_CONF", "PG_NOTIFY"
    };

    /// <summary>Famílias de funções com efeito colateral (bloqueios consultivos).</summary>
    private static readonly string[] ForbiddenPrefixes = ["PG_ADVISORY_", "PG_TRY_ADVISORY_"];

    /// <inheritdoc />
    public string? Check(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "SQL vazio.";

        var quoted = new List<string>();
        var code = StripLiteralsAndComments(sql, quoted, out string? refusal);
        if (code is null)
            return refusal ?? "SQL malformado (comentário ou delimitador não fechado).";

        foreach (string identifier in quoted)
        {
            if (IsForbiddenFunction(identifier))
                return $"função não permitida em consulta de leitura ({identifier.ToUpperInvariant()}).";
        }

        string? first = null, previous = null, beforePrevious = null;
        foreach (var word in Words(code))
        {
            first ??= word;
            if (ForbiddenKeywords.Contains(word))
                return $"comando não permitido em consulta de leitura ({word.ToUpperInvariant()}).";
            if (IsForbiddenFunction(word))
                return $"função não permitida em consulta de leitura ({word.ToUpperInvariant()}).";

            // FOR SHARE e FOR KEY SHARE travam as linhas lidas (FOR UPDATE/FOR NO KEY UPDATE já caem no UPDATE)
            if (word.Equals("SHARE", StringComparison.OrdinalIgnoreCase) &&
                ("FOR".Equals(previous, StringComparison.OrdinalIgnoreCase) ||
                 ("KEY".Equals(previous, StringComparison.OrdinalIgnoreCase) && "FOR".Equals(beforePrevious, StringComparison.OrdinalIgnoreCase))))
                return "comando de bloqueio não permitido em consulta de leitura (FOR SHARE).";

            (beforePrevious, previous) = (previous, word);
        }

        if (first is null || !(first.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || first.Equals("WITH", StringComparison.OrdinalIgnoreCase)))
            return "a consulta deve começar com SELECT ou WITH.";

        int semicolon = code.IndexOf(';', StringComparison.Ordinal);
        if (semicolon >= 0 && !string.IsNullOrWhiteSpace(code.AsSpan(semicolon + 1).ToString().Replace(';', ' ')))
            return "mais de um comando na mesma consulta.";

        return null;
    }

    private static bool IsForbiddenFunction(string name)
    {
        if (ForbiddenFunctions.Contains(name))
            return true;
        foreach (string prefix in ForbiddenPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Troca literais, comentários e identificadores entre aspas por espaço (o conteúdo dos identificadores vai para
    /// <paramref name="quoted"/>); <c>null</c> se algo não fecha ou foi recusado (<paramref name="refusal"/>).
    /// </summary>
    private static string? StripLiteralsAndComments(string sql, List<string> quoted, out string? refusal)
    {
        refusal = null;
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
            else if (c == '&' && next is '"' or '\'' && i > 0 && sql[i - 1] is 'U' or 'u' && (i < 2 || !IsWordChar(sql[i - 2])))
            {
                // U&"..." e U&'...' aceitam escapes Unicode (\0070 = p): o nome real ficaria escondido da verificação
                refusal = "identificador ou literal com escape Unicode (U&) não é aceito em consulta de leitura.";
                return null;
            }
            else if (c is '\'' or '"')
            {
                char delimiter = c;
                // A barra invertida só escapa em literal E'...' (standard_conforming_strings): no literal comum e no identificador
                // ela é um caractere qualquer, e tratá-la como escape esconderia o fim real do literal
                bool backslashEscapes = c == '\'' && i > 0 && sql[i - 1] is 'E' or 'e' && (i < 2 || !IsWordChar(sql[i - 2]));
                int start = ++i;
                while (i < sql.Length && sql[i] != delimiter)
                {
                    if (backslashEscapes && sql[i] == '\\' && i + 1 < sql.Length) i++;
                    i++;
                }
                if (i >= sql.Length) return null;
                if (delimiter == '"')
                    quoted.Add(sql[start..i]);
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
            if (IsWordChar(c))
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
