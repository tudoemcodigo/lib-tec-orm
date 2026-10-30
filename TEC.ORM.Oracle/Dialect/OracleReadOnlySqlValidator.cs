using System.Text;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.Oracle.Dialect;

/// <summary>
/// Verificação de somente leitura do Oracle. Segunda barreira: a leitura já roda numa transação <c>READ ONLY</c> e a
/// proteção principal continua sendo o login da conexão de leitura com permissão só de <c>SELECT</c>.
/// </summary>
/// <remarks>
/// <para>Ignora o conteúdo de literais (<c>'...'</c>, <c>q'[...]'</c>) e comentários (<c>--</c>, <c>/* */</c>). Exige
/// <c>SELECT</c> ou <c>WITH</c> no início, recusa um segundo comando depois de <c>;</c>, as palavras de escrita/sessão,
/// <c>FOR UPDATE</c> e os pacotes do sistema <c>DBMS_*</c>/<c>UTL_*</c> e <c>HTTPURITYPE</c> (rede a partir do banco).</para>
/// <para>Cada parte de um nome qualificado é avaliada (<c>SYS.DBMS_LOCK.SLEEP</c>, <c>SYS . DBMS_LOCK</c>), inclusive entre
/// aspas (<c>"SYS"."DBMS_LOCK"</c>). Sinônimos criados no banco para esses pacotes não são reconhecidos: por isso o login só
/// com <c>SELECT</c>. Funções com <c>PRAGMA AUTONOMOUS_TRANSACTION</c> gravam mesmo numa transação somente leitura.</para>
/// </remarks>
public sealed class OracleReadOnlySqlValidator : IReadOnlySqlValidator
{
    private static readonly HashSet<string> ForbiddenKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "INTO",
        "CREATE", "ALTER", "DROP", "GRANT", "REVOKE",
        "EXEC", "EXECUTE", "DECLARE", "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT",
        "LOCK", "CALL"
    };

    /// <summary>Nomes recusados em qualquer parte do identificador, com ou sem aspas.</summary>
    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.OrdinalIgnoreCase) { "HTTPURITYPE" };

    private static readonly string[] ForbiddenPrefixes = ["DBMS_", "UTL_"];

    /// <inheritdoc />
    public string? Check(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "SQL vazio.";

        var quoted = new List<string>();
        var code = StripLiteralsAndComments(sql, quoted);
        if (code is null)
            return "SQL malformado (comentário ou delimitador não fechado).";

        foreach (string identifier in quoted)
        {
            if (IsForbiddenName(identifier))
                return $"chamada a pacote de sistema não permitida em consulta de leitura ({identifier.ToUpperInvariant()}).";
        }

        string? first = null;
        string? previous = null;
        foreach (var word in Words(code))
        {
            first ??= word;
            if (ForbiddenKeywords.Contains(word))
                return $"comando não permitido em consulta de leitura ({word.ToUpperInvariant()}).";

            if (word.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) && "FOR".Equals(previous, StringComparison.OrdinalIgnoreCase))
                return "comando de bloqueio não permitido em consulta de leitura (FOR UPDATE).";

            // Cada parte do nome qualificado é uma palavra (o ponto separa): SYS.DBMS_LOCK.SLEEP avalia SYS, DBMS_LOCK e SLEEP
            if (IsForbiddenName(word))
                return $"chamada a pacote de sistema não permitida em consulta de leitura ({word.ToUpperInvariant()}).";

            previous = word;
        }

        if (first is null || !(first.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || first.Equals("WITH", StringComparison.OrdinalIgnoreCase)))
            return "a consulta deve começar com SELECT ou WITH.";

        int semicolon = code.IndexOf(';', StringComparison.Ordinal);
        if (semicolon >= 0 && !string.IsNullOrWhiteSpace(code.AsSpan(semicolon + 1).ToString().Replace(';', ' ')))
            return "mais de um comando na mesma consulta.";

        return null;
    }

    private static bool IsForbiddenName(string name)
    {
        if (ForbiddenNames.Contains(name))
            return true;
        foreach (string prefix in ForbiddenPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? StripLiteralsAndComments(string sql, List<string> quoted)
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
                int start = ++i;
                while (i < sql.Length && sql[i] != delimiter)
                {
                    i++;   // no Oracle a barra invertida não escapa; a aspa é duplicada ('')
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

    /// <summary>Letras, dígitos, <c>_</c>, <c>$</c> e <c>#</c> (identificadores do Oracle). O ponto separa as partes do nome.</summary>
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';

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
