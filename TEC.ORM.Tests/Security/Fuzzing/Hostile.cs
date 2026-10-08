using FsCheck;
using FsCheck.Fluent;

namespace TEC.ORM.Tests.Security.Fuzzing;

/// <summary>
/// Geradores de entradas hostis para fuzzing do ORM: textos montados com pedaços que costumam quebrar parsers de SQL,
/// verificadores e logs (aspas, colchetes, comentários, separadores de comando, palavras de escrita do T-SQL, quebras de
/// linha CR/LF, NUL, aspas e ponto e vírgula Unicode parecidos com os ASCII, espaços Unicode, emojis, marcas de combinação).
/// </summary>
internal static class Hostile
{
    private static readonly string[] Tokens =
    [
        "a", "Z", "0", "9", "ç", "É", " ", "  ", "\t", "\r", "\n", "\r\n", "\0", "'", "''", "\"", "[", "]", "]]", "`",
        ";", ",", "(", ")", "=", "*", "/", "-", "--", "/*", "*/", "@", "@@", "#", "$", "%", "_", ".", "\\",
        "OR", "or", "1=1", "' OR '1'='1", "'; DROP TABLE x --", "UNION", "SELECT", "DROP", "DELETE", "EXEC", "xp_cmdshell",
        "WAITFOR", "DELAY", "INTO", "N'", "0x41", "1e5", "CHAR(39)", "＇", "；", "‘", "ʼ", "＝", U(0x00A0), U(0x2028), U(0x3000),
        U(0xFEFF), U(0x0301), U(0x202E), "😀", "🇧🇷", "%27", "%00", "&#39;", "<script>", "${x}", "{0}", "{{", "}}",
    ];

    // Invisíveis e de controle de direção pelo código (no fonte, só como número): NBSP, separador de linha, espaço ideográfico,
    // BOM, acento combinante e RLO
    private static string U(int codePoint) => char.ConvertFromUtf32(codePoint);

    // Caracteres isolados (sem surrogates, que não existem sozinhos em UTF-8 válido)
    private static readonly char[] Chars = [.. Tokens.Where(t => t.Length == 1).Select(t => t[0])];

    /// <summary>Texto UTF-16 válido (sem surrogates isolados), de 0 a ~100 pedaços.</summary>
    public static Gen<string> Text { get; } = Gen.Elements(Tokens).ListOf().Select(string.Concat);

    /// <summary>Texto que pode conter surrogates isolados (UTF-16 inválido), como vem de entradas corrompidas.</summary>
    public static Gen<string> AnyText { get; } = Gen.OneOf(
        Text,
        Text.Select(s => s + "\uD800"),
        Text.Select(s => "\uDC00" + s),
        Gen.Elements(Tokens).ListOf().Select(parts => string.Join("\uD83D", parts)));

    /// <summary>Caractere isolado hostil.</summary>
    public static Gen<char> Char { get; } = Gen.Elements(Chars);

    /// <summary>A mesma palavra com maiúsculas e minúsculas sorteadas (ex.: <c>dRoP</c>).</summary>
    public static Gen<string> RandomCase(string word) =>
        Gen.Elements(true, false).ArrayOf(word.Length)
            .Select(upper => string.Concat(word.Select((c, i) => upper[i] ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c))));

    /// <summary>Configuração padrão: falha com o contraexemplo na mensagem; <paramref name="maxTest"/> casos por propriedade.</summary>
    public static Config Config(int maxTest = 300) => FsCheck.Config.QuickThrowOnFailure.WithMaxTest(maxTest).WithQuietOnSuccess(true);

    /// <summary>Texto visível no relatório de falha (caracteres de controle e invisíveis escapados).</summary>
    public static string Show(string? value) =>
        value is null ? "null" : string.Concat(value.Select(c => c is < ' ' or > '~' ? $"\\u{(int)c:X4}" : c.ToString()));
}
