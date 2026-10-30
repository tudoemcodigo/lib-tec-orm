using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TEC.Core.Common.Guards;
using TEC.Core.Common.Results;
using TEC.Core.Text.Masking;
using TEC.ORM.Common;
using TEC.ORM.EntityFrameworkCore.Configuration;
using TEC.ORM.EntityFrameworkCore.Dialects;
using TEC.ORM.EntityFrameworkCore.Internal;

namespace TEC.ORM.EntityFrameworkCore.Diagnostics;

/// <summary>Descrição de uma operação para observabilidade.</summary>
/// <param name="provider">Provedor (<see cref="OrmDiagnostics.EntityFrameworkProvider"/> ou <see cref="OrmDiagnostics.DapperProvider"/>).</param>
/// <param name="operation">Operação, em baixa cardinalidade (ex.: <c>create</c>, <c>list</c>, <c>query</c>).</param>
/// <param name="target">Alvo: nome da entidade ou da consulta.</param>
/// <param name="isWrite">Escrita (log de auditoria em Information) ou leitura (Debug).</param>
public sealed class OrmOperation(string provider, string operation, string target, bool isWrite)
{
    /// <summary>Provedor.</summary>
    public string Provider { get; } = Guard.NotNullOrWhiteSpace(provider);

    /// <summary>Operação.</summary>
    public string Operation { get; } = Guard.NotNullOrWhiteSpace(operation);

    /// <summary>Alvo (entidade ou consulta).</summary>
    public string Target { get; } = Guard.NotNullOrWhiteSpace(target);

    /// <summary>Escrita (auditoria).</summary>
    public bool IsWrite { get; } = isWrite;

    /// <summary>
    /// Identificador do registro (só no log, conforme <see cref="IdentifierLogMode"/>). Pode ser definido durante a operação
    /// (ex.: o identificador gerado na criação).
    /// </summary>
    public object? Identifier { get; set; }

    /// <summary>
    /// Exclusão física (linha removida de fato, irreversível): o sucesso vai para o log de auditoria em <c>Warning</c>, e não
    /// em <c>Information</c>, como alerta. Vale só para escritas.
    /// </summary>
    public bool IsHardDelete { get; init; }

    /// <summary>
    /// A operação pode ser repetida em falha transitória (<see cref="OrmOptions.TransientRetryCount"/>): só leitura fora de
    /// transação. Ignorado em escritas, que nunca são repetidas.
    /// </summary>
    public bool IsRetryable { get; init; }

    /// <summary>
    /// Autor da operação (id do <c>ICurrentUser</c>), registrado no log da exclusão física (3007) conforme
    /// <see cref="IdentifierLogMode"/>. <c>null</c> sem identidade.
    /// </summary>
    public string? Actor { get; set; }

    /// <summary>Linhas afetadas, registradas no log da exclusão física (3007). <c>null</c> se não informado.</summary>
    public long? AffectedRows { get; set; }
}

/// <summary>
/// Executa as operações do TEC.ORM: cada uma gera uma <c>Activity</c>, uma medição de duração e um log de auditoria, com
/// sucesso ou erro (os nomes são os do <see cref="OrmDiagnostics"/>, assinados pelo TEC.Observability). Exceções viram
/// <see cref="Result"/> com os códigos de <see cref="OrmErrors"/>; só o cancelamento é relançado. Leituras marcadas com
/// <see cref="OrmOperation.IsRetryable"/> são repetidas em falha transitória.
/// </summary>
/// <remarks>Ponto de extensão: substitua no DI para enviar a outro destino (os repositórios dependem só da interface).</remarks>
public interface IOrmOperationRunner
{
    /// <summary>Executa e observa uma operação com valor.</summary>
    /// <param name="operation">Descrição da operação.</param>
    /// <param name="action">A operação; recebe o <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <returns>O resultado da operação, ou a falha traduzida da exceção.</returns>
    Task<Result<T>> ExecuteAsync<T>(OrmOperation operation, Func<CancellationToken, Task<Result<T>>> action, CancellationToken cancellationToken);

    /// <summary>Executa e observa uma operação sem valor.</summary>
    /// <param name="operation">Descrição da operação.</param>
    /// <param name="action">A operação; recebe o <paramref name="cancellationToken"/>.</param>
    /// <param name="cancellationToken">Cancelamento.</param>
    /// <returns>O resultado da operação, ou a falha traduzida da exceção.</returns>
    Task<Result> ExecuteAsync(OrmOperation operation, Func<CancellationToken, Task<Result>> action, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IOrmOperationRunner" />
/// <param name="logger">Log de auditoria.</param>
/// <param name="options">Opções (modo do identificador no log, novas tentativas e dialeto).</param>
/// <param name="translator">Tradutor de exceções; <c>null</c> usa o do dialeto (<see cref="OrmOptions.Dialect"/>).</param>
/// <param name="time">Relógio das esperas entre tentativas.</param>
public sealed class OrmOperationRunner(
    ILogger<OrmOperationRunner> logger,
    OrmOptions options,
    IOrmExceptionTranslator? translator = null,
    TimeProvider? time = null)
    : IOrmOperationRunner
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private readonly IOrmExceptionTranslator _translator = translator ?? options.Dialect?.ExceptionTranslator ?? GenericExceptionTranslator.Instance;

    private readonly string _dbSystem = options.Dialect?.DbSystemName ?? OrmDiagnostics.UnknownDbSystem;

    private const int MaxIdentifierLength = 64;

    /// <inheritdoc />
    public Task<Result<T>> ExecuteAsync<T>(OrmOperation operation, Func<CancellationToken, Task<Result<T>>> action,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(operation, action, static error => Result<T>.Failure(error), cancellationToken);

    /// <inheritdoc />
    public Task<Result> ExecuteAsync(OrmOperation operation, Func<CancellationToken, Task<Result>> action,
        CancellationToken cancellationToken) =>
        ExecuteCoreAsync(operation, action, static error => Result.Failure(error), cancellationToken);

    private async Task<TResult> ExecuteCoreAsync<TResult>(OrmOperation operation, Func<CancellationToken, Task<TResult>> action,
        Func<Error, TResult> failure, CancellationToken cancellationToken) where TResult : Result
    {
        Guard.NotNull(operation);
        Guard.NotNull(action);

        using var activity = OrmDiagnostics.ActivitySource.StartActivity($"{operation.Operation} {operation.Target}", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag(OrmDiagnostics.DbSystemTag, _dbSystem);
            activity.SetTag(OrmDiagnostics.ProviderTag, operation.Provider);
            activity.SetTag(OrmDiagnostics.OperationTag, operation.Operation);
            activity.SetTag(OrmDiagnostics.TargetTag, operation.Target);
        }

        long start = Stopwatch.GetTimestamp();
        int retries = 0;
        TResult result;
        while (true)
        {
            try
            {
                result = await action(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RecordCanceled(operation, activity, start, retries);
                throw;
            }
            catch (Exception exception)
            {
                var translation = _translator.Translate(exception);
                if (CanRetry(operation, exception, retries))
                {
                    retries++;
                    OrmLog.TransientFailureRetry(logger, operation.Provider, operation.Operation, operation.Target, translation.Error.Code,
                        translation.ErrorCode ?? "-", retries, options.TransientRetryCount);
                    // Cancelado durante a espera: registrado como cancelamento da operação, como no catch acima
                    if (!await DelayAsync(retries, cancellationToken).ConfigureAwait(false))
                    {
                        RecordCanceled(operation, activity, start, retries);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    continue;
                }

                result = failure(translation.Error);
                LogException(operation, exception, translation, Stopwatch.GetElapsedTime(start));
                Finish(operation, activity, result, Stopwatch.GetElapsedTime(start), logged: true, retries);
                return result;
            }
        }

        Finish(operation, activity, result, Stopwatch.GetElapsedTime(start), logged: false, retries);
        return result;
    }

    private void RecordCanceled(OrmOperation operation, Activity? activity, long start, int retries)
    {
        var elapsed = Stopwatch.GetElapsedTime(start);
        OrmDiagnostics.RecordOperation(_dbSystem, operation.Provider, operation.Operation, operation.Target, elapsed.TotalSeconds,
            OrmDiagnostics.CanceledErrorType);
        Complete(activity, OrmDiagnostics.CanceledErrorType, retries);
        OrmLog.Canceled(logger, operation.Provider, operation.Operation, operation.Target, FormatIdentifier(operation.Identifier),
            (long)elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Só leitura fora de transação (<see cref="OrmOperation.IsRetryable"/>), com falha transitória e tentativas restantes.
    /// Escritas nunca: um <c>SaveChanges</c>/<c>COMMIT</c> interrompido tem resultado desconhecido e repetir poderia duplicar.
    /// </summary>
    private bool CanRetry(OrmOperation operation, Exception exception, int retries) =>
        !operation.IsWrite && operation.IsRetryable && retries < options.TransientRetryCount && _translator.IsTransient(exception);

    /// <summary>Espera exponencial com variação aleatória; <c>false</c> se cancelada.</summary>
    private async Task<bool> DelayAsync(int retry, CancellationToken cancellationToken)
    {
        double factor = Math.Pow(2, retry - 1) * (1 + (RandomNumberGenerator.GetInt32(0, 501) / 1000.0));
        var delay = TimeSpan.FromMilliseconds(options.TransientRetryDelay.TotalMilliseconds * factor);
        try
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void Finish(OrmOperation operation, Activity? activity, Result result, TimeSpan elapsed, bool logged, int retries)
    {
        string? errorCode = result.Error?.Code;
        OrmDiagnostics.RecordOperation(_dbSystem, operation.Provider, operation.Operation, operation.Target, elapsed.TotalSeconds, errorCode);
        Complete(activity, errorCode, retries);
        if (logged)
            return;

        string identifier = FormatIdentifier(operation.Identifier);
        long milliseconds = (long)elapsed.TotalMilliseconds;
        if (errorCode is null)
        {
            if (operation.IsWrite && operation.IsHardDelete)
                OrmLog.HardDeleteSucceeded(logger, operation.Provider, operation.Operation, operation.Target, identifier,
                    FormatActor(operation.Actor), operation.AffectedRows?.ToString(CultureInfo.InvariantCulture) ?? "-", milliseconds);
            else if (operation.IsWrite)
                OrmLog.WriteSucceeded(logger, operation.Provider, operation.Operation, operation.Target, identifier, milliseconds);
            else
                OrmLog.ReadSucceeded(logger, operation.Provider, operation.Operation, operation.Target, identifier, milliseconds);
        }
        else if (result.Error!.Type == ErrorType.ExternalService)
            OrmLog.InfrastructureFailure(logger, operation.Provider, operation.Operation, operation.Target, identifier, errorCode,
                "-", "-", milliseconds);
        else if (operation.IsWrite)
            OrmLog.WriteFailure(logger, operation.Provider, operation.Operation, operation.Target, identifier, errorCode, milliseconds);
        else
            OrmLog.ExpectedFailure(logger, operation.Provider, operation.Operation, operation.Target, identifier, errorCode, milliseconds);
    }

    private void LogException(OrmOperation operation, Exception exception, OrmDbExceptionTranslation translation, TimeSpan elapsed)
    {
        string identifier = FormatIdentifier(operation.Identifier);
        long milliseconds = (long)elapsed.TotalMilliseconds;
        string exceptionType = exception.GetType().Name;

        // Exceções de banco não vão para o log: a mensagem do banco pode trazer valores (ex.: chave duplicada). Ficam o tipo, o
        // código do erro do banco e o código traduzido. As recusas do próprio TEC.ORM (auditoria sem identidade, teto da
        // exclusão lógica) também são resultados esperados, sem pilha.
        if (OrmExceptionTranslatorBase.IsDatabaseException(exception) || OrmExceptionTranslatorBase.IsOrmRefusal(exception))
        {
            if (translation.IsInfrastructure)
                OrmLog.InfrastructureFailure(logger, operation.Provider, operation.Operation, operation.Target, identifier,
                    translation.Error.Code, exceptionType, translation.ErrorCode ?? "-", milliseconds);
            else if (operation.IsWrite)
                OrmLog.WriteFailure(logger, operation.Provider, operation.Operation, operation.Target, identifier, translation.Error.Code,
                    milliseconds);
            else
                OrmLog.ExpectedFailure(logger, operation.Provider, operation.Operation, operation.Target, identifier,
                    translation.Error.Code, milliseconds);
            return;
        }

        // Falha de programação ou do ORM (ex.: navegação inexistente no Include): o stack trace é necessário para corrigir.
        // Exceções de conversão trazem o valor lido do banco na mensagem (FormatException: "The input string '...'";
        // DataException do Dapper: "Error parsing column N (Nome=valor - tipo)"): vão para o log sem mensagem, só tipo e pilha.
        var logged = IsConversionFailure(exception) ? new OrmRedactedException(exception) : exception;
        OrmLog.UnexpectedException(logger, logged, operation.Provider, operation.Operation, operation.Target, identifier,
            exceptionType, translation.Error.Code);
    }

    /// <summary>A exceção (ou uma interna) é falha de conversão de valor, cuja mensagem pode repetir o valor da coluna.</summary>
    private static bool IsConversionFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is FormatException or InvalidCastException or OverflowException or System.Data.DataException)
                return true;
        }
        return false;
    }

    private static void Complete(Activity? activity, string? errorType, int retries)
    {
        if (activity is null)
            return;

        activity.SetTag(OrmDiagnostics.SuccessTag, errorType is null);
        if (retries > 0)
            activity.SetTag(OrmDiagnostics.RetriesTag, retries);
        if (errorType is null)
            return;
        activity.SetTag(OrmDiagnostics.ErrorTypeTag, errorType);
        activity.SetStatus(ActivityStatusCode.Error, errorType);
    }

    /// <summary>
    /// Identificador para o log, conforme <see cref="OrmOptions.IdentifierLogMode"/>. Em claro (<see cref="IdentifierLogMode.Plain"/>,
    /// ou <see cref="IdentifierLogMode.Auto"/> com chave técnica), caracteres de controle, de formatação (ex.: inversão
    /// bidirecional U+202E) e separadores de linha viram <c>?</c>.
    /// </summary>
    internal string FormatIdentifier(object? identifier)
    {
        if (identifier is null || options.IdentifierLogMode == IdentifierLogMode.Omitted)
            return "-";

        string text = identifier is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : identifier.ToString() ?? string.Empty;

        // Auto: chave técnica em claro; texto e demais tipos (chave natural, possivelmente dado pessoal) com HMAC
        bool hashed = options.IdentifierLogMode == IdentifierLogMode.Hashed ||
                      (options.IdentifierLogMode == IdentifierLogMode.Auto && !IsTechnicalKey(identifier));
        return hashed ? Hash(text) : Sanitize(text);
    }

    /// <summary>
    /// Autor da exclusão física para o log, conforme <see cref="OrmOptions.IdentifierLogMode"/>; em
    /// <see cref="IdentifierLogMode.Auto"/>, em claro só se for um <c>Guid</c> ou um número (id técnico), senão com HMAC.
    /// </summary>
    internal string FormatActor(string? actor)
    {
        if (string.IsNullOrEmpty(actor) || options.IdentifierLogMode == IdentifierLogMode.Omitted)
            return "-";
        bool hashed = options.IdentifierLogMode == IdentifierLogMode.Hashed ||
                      (options.IdentifierLogMode == IdentifierLogMode.Auto &&
                       !Guid.TryParse(actor, out _) && !long.TryParse(actor, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        return hashed ? Hash(actor) : Sanitize(actor);
    }

    /// <summary>Chave técnica (inteiro ou <c>Guid</c>): não é dado pessoal, vai em claro no modo <see cref="IdentifierLogMode.Auto"/>.</summary>
    private static bool IsTechnicalKey(object identifier) =>
        identifier is int or long or short or byte or uint or ulong or ushort or sbyte or Guid;

    /// <summary>Tamanho + HMAC com chave aleatória do processo (TEC.Core): correlaciona sem expor o valor.</summary>
    private static string Hash(string text) => SensitiveDataMasker.DescribeUntrusted(text);

    private static string Sanitize(string text)
    {
        // Evita injeção de linhas e falsificação visual no log (identificadores string vindos do usuário)
        if (text.Length > MaxIdentifierLength)
            text = text[..MaxIdentifierLength] + "…";
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
                span[i] = IsUnsafeForLog(source[i]) ? '?' : source[i];
        });
    }

    private static bool IsUnsafeForLog(char c) =>
        char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
}

/// <summary>
/// Exceção de conversão registrada sem a mensagem (e sem as internas), que pode trazer o valor lido do banco: só os tipos da
/// cadeia e a pilha da original, suficientes para achar o mapeamento errado.
/// </summary>
internal sealed class OrmRedactedException(Exception original) : Exception(Describe(original))
{
    private readonly string? _stackTrace = original.StackTrace;

    /// <inheritdoc />
    public override string? StackTrace => _stackTrace;

    private static string Describe(Exception exception)
    {
        var types = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            types.Add(current.GetType().FullName ?? current.GetType().Name);
        return string.Join(" <- ", types) + ": mensagem omitida (pode conter o valor de uma coluna).";
    }
}
