using System.Text;

namespace TEC.ORM.SqlServer.Internal;

/// <summary>
/// Verificação de que um SQL (T-SQL) é somente leitura. Defesa em profundidade: a proteção principal é o login da conexão
/// de leitura ter só <c>SELECT</c>, e os valores sempre irem como parâmetros (<c>SqlQuery</c>).
/// </summary>
/// <remarks>
/// Ignora o conteúdo de literais (<c>'...'</c>), identificadores delimitados (<c>[...]</c>, <c>"..."</c>) e comentários
/// (<c>--</c> até CR ou LF, <c>/* */</c> aninhados como no SQL Server), então <c>WHERE Status = 'DELETE'</c> e uma coluna <c>[Update]</c> passam. O T-SQL não
/// exige <c>;</c> entre comandos (<c>SELECT 1 DROP TABLE x</c> é um lote válido), por isso as palavras de escrita são
/// recusadas em qualquer posição, e não só no início. Nem espaço ele exige depois de um número: <c>SELECT 1DROP TABLE x</c>
/// executa o <c>DROP</c>, então o que vem depois de um literal numérico também é conferido.
/// </remarks>
internal static class ReadOnlySqlGuard
{
    private static readonly HashSet<string> ForbiddenKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "INTO",
        "CREATE", "ALTER", "DROP", "GRANT", "REVOKE", "DENY",
        "EXEC", "EXECUTE", "SP_EXECUTESQL", "DECLARE", "SET", "USE", "GO",
        "BEGIN", "COMMIT", "ROLLBACK", "SAVE", "WAITFOR", "SHUTDOWN", "KILL", "RECONFIGURE",
        "BACKUP", "RESTORE", "DBCC", "BULK", "OPENROWSET", "OPENQUERY", "OPENDATASOURCE",
        // Escrita/sessão fora do DML comum
        "UPDATETEXT", "WRITETEXT", "CHECKPOINT", "SETUSER", "REVERT", "RECEIVE",
        // DDL sem CREATE/ALTER (ADD SIGNATURE, ADD SENSITIVITY CLASSIFICATION, DISABLE/ENABLE TRIGGER), chaves e cursores
        // (OPEN/CLOSE SYMMETRIC KEY, MASTER KEY, cursor) e Service Broker (SEND, END/MOVE/GET CONVERSATION)
        "ADD", "DISABLE", "ENABLE", "OPEN", "CLOSE", "DEALLOCATE", "SEND", "CONVERSATION"
    };

    private static readonly char[] LineBreaks = ['\r', '\n'];

    /// <summary>Motivo da recusa, ou <c>null</c> se o SQL é somente leitura.</summary>
    public static string? Check(string sql)
    {
        var code = StripLiteralsAndComments(sql);
        if (code is null)
            return "SQL malformado (literal, identificador ou comentário sem fechamento).";

        string? first = null, previous = null, beforePrevious = null;
        foreach (var word in Words(code))
        {
            first ??= word;
            if (ForbiddenKeywords.Contains(word))
                return $"comando não permitido em consulta de leitura ({word.ToUpperInvariant()}).";
            // NEXT VALUE FOR avança a sequência: escreve no banco mesmo dentro de um SELECT
            if (word.Equals("FOR", StringComparison.OrdinalIgnoreCase) && "VALUE".Equals(previous, StringComparison.OrdinalIgnoreCase)
                && "NEXT".Equals(beforePrevious, StringComparison.OrdinalIgnoreCase))
                return "comando não permitido em consulta de leitura (NEXT VALUE FOR).";
            (beforePrevious, previous) = (previous, word);
        }

        if (first is null || !(first.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || first.Equals("WITH", StringComparison.OrdinalIgnoreCase)))
            return "a consulta deve começar com SELECT ou WITH.";

        int semicolon = code.IndexOf(';', StringComparison.Ordinal);
        if (semicolon >= 0 && !string.IsNullOrWhiteSpace(code.AsSpan(semicolon + 1).ToString().Replace(';', ' ')))
            return "mais de um comando na mesma consulta.";

        return null;
    }

    /// <summary>Troca literais, identificadores delimitados e comentários por espaço; <c>null</c> se algum não fecha.</summary>
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
                // O comentário de linha termina em CR ou LF (o SQL Server aceita os dois): só LF deixaria "--x\rDROP ..." passar
                int end = sql.IndexOfAny(LineBreaks, i);
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
                if (depth > 0)
                    return null;
                output.Append(' ');
            }
            else if (c is '\'' or '"' or '[')
            {
                char close = c == '[' ? ']' : c;
                i++;
                bool closed = false;
                while (i < sql.Length)
                {
                    if (sql[i] == close)
                    {
                        // Fechamento duplicado é escape ('' , "" e ]])
                        if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; }
                        i++;
                        closed = true;
                        break;
                    }
                    i++;
                }
                if (!closed)
                    return null;
                output.Append(c == '\'' ? " '' " : " x ");
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
        int start = -1;
        for (int i = 0; i <= code.Length; i++)
        {
            bool isWordChar = i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] is '_' or '@' or '#' or '$');
            if (isWordChar && start < 0)
                start = i;
            else if (!isWordChar && start >= 0)
            {
                foreach (var word in SplitAfterNumber(code, start, i))
                    yield return word;
                start = -1;
            }
        }
    }

    /// <summary>
    /// A palavra e, se ela começa com dígito ou <c>$</c>, também o que pode vir depois do literal numérico: o SQL Server começa
    /// um token novo onde o literal termina (<c>1DROP</c> = <c>1</c> + <c>DROP</c>; <c>1e5EXEC</c> = <c>1e5</c> + <c>EXEC</c>;
    /// <c>0x1FRECONFIGURE</c> = <c>0x1F</c> + <c>RECONFIGURE</c>; <c>$1DROP</c> = <c>$1</c> + <c>DROP</c>). Também a palavra
    /// logo depois de <c>&lt;dígito&gt;.</c>, que pode ser o expoente de um float (<c>1.e5DROP</c> = <c>1.e5</c> + <c>DROP</c>).
    /// </summary>
    /// <remarks>
    /// Conservador: devolve todo sufixo que começa numa letra depois de um prefixo só com caracteres de literal numérico
    /// (dígitos, <c>x</c>, dígitos hexadecimais, expoente), então <c>SELECT 0x1FADD</c> (hexadecimal válido) também é recusado.
    /// Só os sufixos do tamanho de uma palavra proibida são gerados: tempo linear mesmo com uma palavra de 32 KB.
    /// Identificadores (letra, <c>_</c>, <c>@</c>, <c>#</c>) não são cortados.
    /// </remarks>
    private static IEnumerable<string> SplitAfterNumber(string code, int start, int end)
    {
        yield return code[start..end];
        // "1.e5DROP": o ponto quebra a palavra, mas "e5" ainda é o expoente do literal 1.e5 e o DROP vira outro token
        bool exponentAfterDot = start >= 2 && code[start - 1] == '.' && char.IsDigit(code[start - 2]);
        if (!char.IsDigit(code[start]) && code[start] != '$' && !exponentAfterDot)
            yield break;

        int numeric = start + 1;
        while (numeric < end && IsNumericLiteralChar(code[numeric]))
            numeric++;

        for (int i = Math.Max(start + 1, end - MaxKeywordLength); i <= Math.Min(numeric, end - 1); i++)
        {
            if (char.IsLetter(code[i]))
                yield return code[i..end];
        }
    }

    private static readonly int MaxKeywordLength = ForbiddenKeywords.Max(keyword => keyword.Length);

    private static bool IsNumericLiteralChar(char c) => char.IsAsciiHexDigit(c) || c is 'x' or 'X';
}
