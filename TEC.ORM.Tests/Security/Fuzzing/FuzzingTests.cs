using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using TEC.ORM.Common;
using TEC.ORM.Paging;
using TEC.ORM.Queries;
using TEC.ORM.SqlServer.Configuration;
using TEC.ORM.SqlServer.Diagnostics;
using TEC.ORM.SqlServer.Internal;
using TEC.ORM.SqlServer.Security;
using TEC.ORM.Tests.Database.CodeFirst;
using TEC.ORM.Tests.Fakes;

namespace TEC.ORM.Tests.Security.Fuzzing;

/// <summary>
/// Testes de propriedade (FsCheck) com entradas hostis geradas aleatoriamente. Cada propriedade roda centenas de casos;
/// em caso de falha, a mensagem traz o contraexemplo e a semente para reproduzir.
/// </summary>
/// <remarks>
/// A verificação de somente leitura é comparada com o parser oficial do T-SQL (<see cref="TSqlOracle"/>): qualquer SQL que
/// ela aceite precisa ser, para o SQL Server, só leitura.
/// </remarks>
public partial class FuzzingTests
{
    // ---------- SqlQuery: valores nunca entram no texto do SQL ----------

    [Test]
    public void SqlQuery_Interpolated_ValuesBecomeParametersAndNeverReachTheText()
    {
        Prop.ForAll(Hostile.AnyText.NonEmptyListOf().Select(values => values.Take(50).ToArray()).ToArbitrary(), values =>
        {
            string format = "SELECT Id FROM t WHERE " + string.Join(" AND ", values.Select((_, i) => $"c{i} = {{{i}}}"));
            string expected = "SELECT Id FROM t WHERE " + string.Join(" AND ", values.Select((_, i) => $"c{i} = @p{i}"));

            var query = SqlQuery.Interpolated("fuzz", FormattableStringFactory.Create(format, values.Cast<object?>().ToArray()));

            if (query.Sql != expected)
                throw new InvalidOperationException($"SQL inesperado: {Hostile.Show(query.Sql)}");
            if (query.Parameters.Count != values.Length)
                throw new InvalidOperationException($"{query.Parameters.Count} parâmetros para {values.Length} valores");
            for (int i = 0; i < values.Length; i++)
            {
                if (!ReferenceEquals(query.Parameters[$"p{i}"], values[i]))
                    throw new InvalidOperationException($"Parâmetro p{i} diferente do valor {Hostile.Show(values[i])}");
            }
        }).Check(Hostile.Config(400));
    }

    [Test]
    public void SqlQuery_ArbitraryNamesAndParameterNames_FailOnlyWithArgumentException()
    {
        var caseGen =
            from name in Gen.OneOf(Hostile.AnyText, Gen.Elements("clientes.por-id", "a", "relatorio_2026", "x-y.z"))
            from parameter in Gen.OneOf(Hostile.AnyText, Gen.Elements("Id", "_p", "p0", "Nome_1"))
            select (Name: name, Parameter: parameter);

        Prop.ForAll(caseGen.ToArbitrary(), c =>
        {
            SqlQuery query;
            try
            {
                query = SqlQuery.Create(c.Name, "SELECT 1 WHERE @x = 1", new Dictionary<string, object?> { [c.Parameter] = 1 });
            }
            catch (ArgumentException)
            {
                return;
            }

            // Aceito: nome e parâmetro dentro do formato (nada que quebre logs, métricas ou o marcador @)
            if (!ValidName().IsMatch(query.Name) || !ValidParameter().IsMatch(c.Parameter))
                throw new InvalidOperationException($"Aceitou nome {Hostile.Show(c.Name)} / parâmetro {Hostile.Show(c.Parameter)}");
        }).Check(Hostile.Config(400));
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,99}\z")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}\z")]
    private static partial Regex ValidParameter();

    // ---------- Verificação de somente leitura ----------

    [Test]
    public void ReadOnlyGuard_ArbitraryInput_NeverThrows()
    {
        Prop.ForAll(Hostile.AnyText.ToArbitrary(), sql =>
        {
            _ = ReadOnlySqlGuard.Check(sql);
            _ = ReadOnlySqlGuard.Check("SELECT " + sql);
        }).Check(Hostile.Config(1_000));
    }

    [Test]
    public void ReadOnlyGuard_WriteKeywordAfterAnySeparator_IsRejected()
    {
        // Prefixo de leitura válido + separador + palavra de escrita (maiúsculas sorteadas) + qualquer resto
        var caseGen =
            from prefix in Gen.Elements(TSqlCorpus.ReadStatements)
            from separator in Gen.Elements(" ", "\t", "\r", "\n", "\r\n", "/**/", "/* x */", "--x\r", "--x\n", ")", "(", ";", "; ", "'x'", "[x]", "\"x\"")
            from keyword in Gen.Elements(TSqlCorpus.WriteKeywords).SelectMany(Hostile.RandomCase)
            from rest in Gen.Elements("", " t", " TABLE t", "(1)", " @x", ";")
            select prefix + separator + keyword + rest;

        Prop.ForAll(caseGen.ToArbitrary(), sql =>
        {
            if (ReadOnlySqlGuard.Check(sql) is null)
                throw new InvalidOperationException($"Aceitou escrita: {Hostile.Show(sql)}");
        }).Check(Hostile.Config(1_000));
    }

    [Test]
    public void ReadOnlyGuard_HostileContentInsideLiteralsIdentifiersAndComments_DoesNotChangeTheVerdict()
    {
        // O conteúdo de literais, identificadores delimitados e comentários fechados nunca é código: aceito sempre
        Prop.ForAll(Hostile.Text.ToArbitrary(), text =>
        {
            string literal = text.Replace("'", "''", StringComparison.Ordinal);
            string bracket = text.Replace("]", "]]", StringComparison.Ordinal);
            string quoted = text.Replace("\"", "\"\"", StringComparison.Ordinal);
            string block = text.Replace("/*", "/ *", StringComparison.Ordinal).Replace("*/", "* /", StringComparison.Ordinal);
            string line = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

            string sql = $"SELECT [{bracket}] AS \"{quoted}\" /* {block} */ FROM t WHERE c = N'{literal}' -- {line}";
            if (ReadOnlySqlGuard.Check(sql) is { } reason)
                throw new InvalidOperationException($"Recusou leitura ({reason}): {Hostile.Show(sql)}");
        }).Check(Hostile.Config(1_000));
    }

    [Test]
    public async Task ReadOnlyGuard_AgreesWithTheSqlServerParser()
    {
        var stats = RunDifferential(cases: 3_000);
        Console.WriteLine($"Fuzzing diferencial: {stats}");

        // O oráculo precisa ter sido exercitado de verdade: muitos lotes válidos aceitos e muitos recusados
        await Assert.That(stats.AcceptedAndParsed).IsGreaterThan(200);
        await Assert.That(stats.RejectedDangerous).IsGreaterThan(200);
    }

    [Test]
    [Explicit]
    [Category(TestCategories.HeavySecurity)]
    public async Task ReadOnlyGuard_AgreesWithTheSqlServerParser_Extended()
    {
        var stats = RunDifferential(cases: 200_000);
        Console.WriteLine($"Fuzzing diferencial: {stats}");
        await Assert.That(stats.AcceptedAndParsed).IsGreaterThan(10_000);
    }

    internal sealed record DifferentialStats(int AcceptedAndParsed, int RejectedDangerous, int ParseErrors)
    {
        public override string ToString() =>
            $"{AcceptedAndParsed} aceitos e válidos, {RejectedDangerous} recusados com escrita, {ParseErrors} com erro de sintaxe";
    }

    /// <summary>
    /// Lotes gerados pela gramática de <see cref="TSqlCorpus"/>: se a verificação aceita e o parser do SQL Server entende o
    /// lote, nenhum comando dele pode escrever (dados, schema, permissões, sessão ou servidor).
    /// </summary>
    private static DifferentialStats RunDifferential(int cases)
    {
        int acceptedAndParsed = 0, rejectedDangerous = 0, parseErrors = 0;

        Prop.ForAll(TSqlCorpus.Batches.ToArbitrary(), sql =>
        {
            var verdict = TSqlOracle.Analyze(sql);
            bool accepted = ReadOnlySqlGuard.Check(sql) is null;

            if (!verdict.Parsed)
            {
                Interlocked.Increment(ref parseErrors);
                return;
            }

            if (accepted && verdict.Dangerous is { } statement)
                throw new InvalidOperationException($"Aceitou um lote que o SQL Server executa como {statement}: {Hostile.Show(sql)}");

            if (accepted)
                Interlocked.Increment(ref acceptedAndParsed);
            else if (verdict.Dangerous is not null)
                Interlocked.Increment(ref rejectedDangerous);
        }).Check(Hostile.Config(cases));

        return new DifferentialStats(acceptedAndParsed, rejectedDangerous, parseErrors);
    }

    // ---------- Paginação e ordenação ----------

    [Test]
    public void PageRequest_ArbitraryValues_ValidateNeverThrowsAndOffsetNeverOverflows()
    {
        var number = Gen.OneOf(Gen.Choose(int.MinValue, int.MaxValue), Gen.Elements(int.MinValue, -1, 0, 1, 2, 100, 10_000, int.MaxValue - 1, int.MaxValue));
        var caseGen =
            from page in number
            from size in number
            from max in Gen.Choose(1, 10_000)
            from sortBy in Gen.OneOf(Gen.Constant<string?>(null), Hostile.AnyText.Select(s => (string?)s))
            select (Page: page, Size: size, Max: max, SortBy: sortBy);

        Prop.ForAll(caseGen.ToArbitrary(), c =>
        {
            var request = new PageRequest(c.Page, c.Size, c.SortBy);
            if (request.Validate(c.Max) is { } error)
            {
                if (error.Code != OrmErrors.InvalidInputCode)
                    throw new InvalidOperationException($"Código inesperado: {error.Code}");
                return;
            }

            long end = (long)request.Offset + request.PageSize;
            if (request.Offset < 0 || end > int.MaxValue || request.PageSize > c.Max || request.Page < 1)
                throw new InvalidOperationException($"Página aceita fora do limite: {c}");
        }).Check(Hostile.Config(2_000));
    }

    [Test]
    public void ListAsync_ArbitrarySortBy_OnlyAllowedPropertiesOrInvalidInput()
    {
        using var context = TestOrm.InMemoryContext();
        var customers = TestOrm.Orm<Customer, Guid>(context);
        foreach (string name in new[] { "Ana", "Bia", "Caio" })
            customers.CreateAsync(new Customer { Name = name, Email = $"{name}@exemplo.com" }).GetAwaiter().GetResult();

        string[] allowed = ["Id", "Name", "Email", "Active"];
        var sortGen = Gen.OneOf(
            Hostile.AnyText,
            Gen.Elements([.. allowed, "IsDeleted", "DeletedAt", "Orders", "Name ", " Name", "Name--", "Name;DROP"]).SelectMany(Hostile.RandomCase));

        Prop.ForAll(sortGen.ToArbitrary(), sortBy =>
        {
            var result = customers.ListAsync(new PageRequest(1, 10, sortBy)).GetAwaiter().GetResult();
            bool isAllowed = allowed.Contains(sortBy, StringComparer.OrdinalIgnoreCase);

            if (isAllowed && (!result.IsSuccess || result.Value.Items.Count != 3))
                throw new InvalidOperationException($"Ordenação permitida recusada: {Hostile.Show(sortBy)}");
            if (!isAllowed && result.Error?.Code != OrmErrors.InvalidInputCode)
                throw new InvalidOperationException($"Ordenação não permitida aceita: {Hostile.Show(sortBy)} → {result.Error?.Code ?? "success"}");
            // A mensagem é fixa: o texto recebido nunca volta ao cliente
            if (!isAllowed && result.Error!.Message is not ("Ordenação por um campo não permitido." or "Ordenação inválida."))
                throw new InvalidOperationException($"Mensagem ecoa a entrada: {Hostile.Show(result.Error.Message)}");
        }).Check(Hostile.Config(500));
    }

    // ---------- Repositório com chave de texto vinda de fora ----------

    [Test]
    public void Repository_ArbitraryStringIds_OnlyNotFoundOrInvalidInput()
    {
        using var context = TestOrm.InMemoryContext();
        var categories = TestOrm.Orm<Category, string>(context);
        categories.CreateAsync(new Category { Id = "existe", Name = "Existente" }).GetAwaiter().GetResult();

        Prop.ForAll(Hostile.AnyText.ToArbitrary(), id =>
        {
            if (id == "existe")
                return;

            var read = categories.GetByIdAsync(id).GetAwaiter().GetResult();
            var exists = categories.ExistsAsync(id).GetAwaiter().GetResult();
            var deleted = categories.DeleteAsync(id).GetAwaiter().GetResult();

            if (read.Error?.Code != OrmErrors.NotFoundCode || deleted.Error?.Code != OrmErrors.NotFoundCode || !exists.IsSuccess || exists.Value)
                throw new InvalidOperationException($"Id {Hostile.Show(id)}: {read.Error?.Code}/{exists.Error?.Code}/{deleted.Error?.Code}");
        }).Check(Hostile.Config(300));

        // O registro existente continua intacto depois de todas as tentativas
        if (!categories.ExistsAsync("existe").GetAwaiter().GetResult().Value)
            throw new InvalidOperationException("O registro existente foi afetado.");
    }

    // ---------- Identificador nos logs ----------

    [Test]
    public void FormatIdentifier_ArbitraryInput_IsBoundedAndWithoutControlCharacters()
    {
        var plain = new OrmOperationRunner(NullLogger<OrmOperationRunner>.Instance, TestOrm.Options());
        var hashed = new OrmOperationRunner(NullLogger<OrmOperationRunner>.Instance, TestOrm.Options(o => o.IdentifierLogMode = IdentifierLogMode.Hashed));
        var omitted = new OrmOperationRunner(NullLogger<OrmOperationRunner>.Instance, TestOrm.Options(o => o.IdentifierLogMode = IdentifierLogMode.Omitted));

        Prop.ForAll(Hostile.AnyText.ToArbitrary(), identifier =>
        {
            string text = plain.FormatIdentifier(identifier);
            if (text.Any(UnsafeForLog) || text.Length > 65)
                throw new InvalidOperationException($"Identificador no log: {Hostile.Show(text)}");
            if (identifier.Length is > 0 and <= 64 && !identifier.Any(UnsafeForLog) && text != identifier)
                throw new InvalidOperationException($"Identificador comum alterado: {Hostile.Show(identifier)} → {Hostile.Show(text)}");

            string hash = hashed.FormatIdentifier(identifier);
            if (!HashedIdentifier().IsMatch(hash) || hash != hashed.FormatIdentifier(identifier))
                throw new InvalidOperationException($"Identificador com hash fora do formato: {hash}");

            if (omitted.FormatIdentifier(identifier) != "-")
                throw new InvalidOperationException("Identificador omitido apareceu no log.");
        }).Check(Hostile.Config(1_000));
    }

    /// <summary>Controle, formatação (ex.: U+202E) e separadores de linha: nunca no log.</summary>
    private static bool UnsafeForLog(char c) =>
        char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator;

    [GeneratedRegex("^(<vazio>|<[0-9]+ caracteres, hmac:[0-9a-f]{12}>)$")]
    private static partial Regex HashedIdentifier();

    // ---------- Segredo da conexão ----------

    private const string PasswordMarker = "SenhaFuzz9f3k2";

    private static readonly string[] ConnectionKeys =
    [
        "Server", "Data Source", "Addr", "Database", "Initial Catalog", "User ID", "UID", "Password", "PWD", "Encrypt",
        "TrustServerCertificate", "Trust Server Certificate", "Persist Security Info", "PersistSecurityInfo", "Application Name",
        "Integrated Security", "Connect Timeout", "Command Timeout", "Application Intent", "MultipleActiveResultSets", "Pooling",
    ];

    private static readonly string[] ConnectionValues =
    [
        "db", "tcp:db,1433", "dbExemplo", "app", PasswordMarker, "True", "False", "true", "false", "yes", "no", "Optional",
        "Mandatory", "Strict", "optional", "1", "0", "", "\"a;Encrypt=False\"", "'x;TrustServerCertificate=True'",
        "{" + PasswordMarker + "}", "\"" + PasswordMarker + ";Encrypt=Optional\"",
    ];

    [Test]
    public void ConnectionSecret_ArbitraryContent_AcceptedOnlyWithinThePolicyAndNeverLogged()
    {
        var pairGen =
            from key in Gen.OneOf(Gen.Elements(ConnectionKeys).SelectMany(Hostile.RandomCase), Hostile.Text)
            from value in Gen.OneOf(Gen.Elements(ConnectionValues), Hostile.Text)
            from separator in Gen.Elements("=", " = ", "==", "")
            select key + separator + value;
        var caseGen =
            from pairs in pairGen.ListOf()
            from basePosition in Gen.Choose(0, 3)
            from allowTrust in Gen.Elements(true, false)
            select (Secret: Compose(pairs.ToList(), basePosition), AllowTrust: allowTrust);

        Prop.ForAll(caseGen.ToArbitrary(), c =>
        {
            var logs = new CapturingLoggerProvider();
            var options = TestOrm.Options(o =>
            {
                o.ApplicationName = "Fuzz";
                o.AllowTrustServerCertificate = c.AllowTrust;
            });
            var security = new OrmConnectionSecurity(TestOrm.Secrets((TestOrm.SecretName, c.Secret)), options, logs.CreateLogger<OrmConnectionSecurity>());

            var built = security.BuildConnectionStringAsync(OrmConnectionKind.ReadWrite, CancellationToken.None).GetAwaiter().GetResult();
            if (logs.AllText.Contains(PasswordMarker, StringComparison.Ordinal))
                throw new InvalidOperationException($"Senha no log para o segredo {Hostile.Show(c.Secret)}");

            if (built.IsFailure)
            {
                if (built.Error!.Code is not (OrmErrors.InvalidConnectionSecretCode or OrmErrors.ConnectionUnavailableCode)
                    || built.Error.Message.Contains(PasswordMarker, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Falha inesperada {built.Error.Code}: {Hostile.Show(built.Error.Message)}");
                return;
            }

            // Aceito: a política vale para a string que o SqlClient realmente vai usar (inclusive com chaves repetidas)
            var applied = new SqlConnectionStringBuilder(built.Value.Reveal());
            if (applied.Encrypt == SqlConnectionEncryptOption.Optional
                || (applied.TrustServerCertificate && !c.AllowTrust)
                || applied.PersistSecurityInfo
                || applied.ApplicationName != "Fuzz"
                || string.IsNullOrWhiteSpace(applied.DataSource)
                || string.IsNullOrWhiteSpace(applied.InitialCatalog)
                || built.Value.ToString() != "***")
                throw new InvalidOperationException($"Aceitou fora da política: {Hostile.Show(c.Secret)}");
        }).Check(Hostile.Config(400));

        // Base válida inserida em posição sorteada no meio das chaves hostis (as chaves posteriores sobrepõem as anteriores)
        static string Compose(List<string> pairs, int basePosition)
        {
            const string valid = $"Server=db;Database=dbExemplo;User ID=app;Password={PasswordMarker};Encrypt=True";
            pairs.Insert(Math.Min(basePosition, pairs.Count), valid);
            return string.Join(';', pairs);
        }
    }
}
