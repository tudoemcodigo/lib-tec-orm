using System.Text;
using TEC.ORM.EntityFrameworkCore.Dialects;

namespace TEC.ORM.MySql.Dialect;

/// <summary>
/// Verificação de somente leitura do MySQL / MariaDB. Segunda barreira: a leitura já roda numa transação
/// <c>READ ONLY</c> e a proteção principal continua sendo o login da conexão de leitura com permissão só de <c>SELECT</c>.
/// </summary>
/// <remarks>
/// <para>Ignora o conteúdo de literais (<c>'...'</c>, <c>"..."</c>, com escape por barra invertida) e comentários (<c>--</c>
/// seguido de espaço, <c>#</c>, <c>/* */</c>). Comentários executáveis (<c>/*! ... */</c>, <c>/*M! ... */</c>) são recusados:
/// o servidor executa o conteúdo. Exige <c>SELECT</c> ou <c>WITH</c> no início e recusa um segundo comando depois de
/// <c>;</c>, as palavras de escrita/sessão, os bloqueios (<c>FOR UPDATE</c>, <c>FOR SHARE</c>, <c>LOCK IN SHARE MODE</c>) e
/// funções com efeito colateral (<c>SLEEP</c>, <c>BENCHMARK</c>, <c>LOAD_FILE</c>, <c>GET_LOCK</c>...), inclusive entre crases.</para>
/// <para><c>REPLACE</c>, <c>TRUNCATE</c> e <c>INSERT</c> seguidos de <c>(</c> são as funções de texto/número do MySQL e passam;
/// como comando (sem <c>(</c> logo depois) continuam recusados.</para>
/// </remarks>
public sealed class MySqlReadOnlySqlValidator : IReadOnlySqlValidator
{
    private static readonly HashSet<string> ForbiddenKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "UPDATE", "DELETE", "MERGE", "INTO",
        "CREATE", "ALTER", "DROP", "GRANT", "REVOKE",
        "EXECUTE", "PREPARE", "DEALLOCATE", "CALL",
        "LOCK", "UNLOCK", "BEGIN", "COMMIT", "ROLLBACK", "SET", "LOAD"
    };

    /// <summary>Comandos que também são nomes de função: recusados só quando não vêm seguidos de <c>(</c>.</summary>
    private static readonly HashSet<string> StatementsAlsoFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "REPLACE", "TRUNCATE"
    };

    /// <summary>Funções com efeito colateral ou espera (recusadas também entre crases).</summary>
    private static readonly HashSet<string> ForbiddenFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "LOAD_FILE", "SLEEP", "BENCHMARK",
        "GET_LOCK", "RELEASE_LOCK", "RELEASE_ALL_LOCKS",
        "MASTER_POS_WAIT", "SOURCE_POS_WAIT", "WAIT_FOR_EXECUTED_GTID_SET"
    };

    /// <inheritdoc />
    public string? Check(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "SQL vazio.";

        var quoted = new List<string>();
        var code = StripLiteralsAndComments(sql, quoted);
        if (code is null)
            return "SQL malformado ou comentário executável (/*! */, /*M! */).";

        foreach (string identifier in quoted)
        {
            if (ForbiddenFunctions.Contains(identifier))
                return $"função não permitida em consulta de leitura ({identifier.ToUpperInvariant()}).";
        }

        string? first = null, previous = null, beforePrevious = null;
        foreach (var (word, followedByParenthesis) in Words(code))
        {
            first ??= word;
            if (ForbiddenKeywords.Contains(word) || ForbiddenFunctions.Contains(word) ||
                (StatementsAlsoFunctions.Contains(word) && !followedByParenthesis))
                return $"comando não permitido em consulta de leitura ({word.ToUpperInvariant()}).";

            // FOR SHARE (MySQL 8) e LOCK IN SHARE MODE travam as linhas lidas; FOR UPDATE já cai no UPDATE
            if (word.Equals("SHARE", StringComparison.OrdinalIgnoreCase) && "FOR".Equals(previous, StringComparison.OrdinalIgnoreCase))
                return "comando de bloqueio não permitido em consulta de leitura (FOR SHARE).";
            if (word.Equals("MODE", StringComparison.OrdinalIgnoreCase) && "SHARE".Equals(previous, StringComparison.OrdinalIgnoreCase)
                && "IN".Equals(beforePrevious, StringComparison.OrdinalIgnoreCase))
                return "comando de bloqueio não permitido em consulta de leitura (LOCK IN SHARE MODE).";

            (beforePrevious, previous) = (previous, word);
        }

        if (first is null || !(first.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || first.Equals("WITH", StringComparison.OrdinalIgnoreCase)))
            return "a consulta deve começar com SELECT ou WITH.";

        int semicolon = code.IndexOf(';', StringComparison.Ordinal);
        if (semicolon >= 0 && !string.IsNullOrWhiteSpace(code.AsSpan(semicolon + 1).ToString().Replace(';', ' ')))
            return "mais de um comando na mesma consulta.";

        return null;
    }

    private static string? StripLiteralsAndComments(string sql, List<string> quoted)
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
                int start = ++i;
                while (i < sql.Length && sql[i] != delimiter)
                {
                    if (sql[i] == '\\' && delimiter != '`' && i + 1 < sql.Length) i++;
                    i++;
                }
                if (i >= sql.Length) return null;
                if (delimiter == '`')
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

    /// <summary>Palavras do código, cada uma com a indicação de vir seguida de <c>(</c> (espaços no meio são ignorados).</summary>
    private static IEnumerable<(string Word, bool FollowedByParenthesis)> Words(string code)
    {
        int i = 0;
        while (i < code.Length)
        {
            if (!IsWordChar(code[i]))
            {
                i++;
                continue;
            }
            int start = i;
            while (i < code.Length && IsWordChar(code[i]))
                i++;
            int next = i;
            while (next < code.Length && char.IsWhiteSpace(code[next]))
                next++;
            yield return (code[start..i], next < code.Length && code[next] == '(');
        }
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
