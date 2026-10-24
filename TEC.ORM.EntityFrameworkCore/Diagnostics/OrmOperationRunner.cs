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
public sealed class OrmOperation(string provider, string operation, string target, bool isWrite)
{
    /// <summary>Identificador do provedor.</summary>
    public string Provider { get; } = Guard.NotNullOrWhiteSpace(provider);

    /// <summary>Nome da operação.</summary>
    public string Operation { get; } = Guard.NotNullOrWhiteSpace(operation);

    /// <summary>Alvo da operação.</summary>
    public string Target { get; } = Guard.NotNullOrWhiteSpace(target);

    /// <summary>Indica se é operação de escrita.</summary>
    public bool IsWrite { get; } = isWrite;

    /// <summary>Identificador do registro.</summary>
    public object? Identifier { get; set; }

    /// <summary>Indica se é exclusão física.</summary>
    public bool IsHardDelete { get; init; }

    /// <summary>Indica se a operação pode ser repetida em falha transitória.</summary>
    public bool IsRetryable { get; init; }

    /// <summary>Autor da operação.</summary>
    public string? Actor { get; set; }

    /// <summary>Quantidade de linhas afetadas.</summary>
    public long? AffectedRows { get; set; }
}

/// <summary>Executa as operações do TEC.ORM com observabilidade, resiliência e tratamento de exceções.</summary>
public interface IOrmOperationRunner
{
    /// <summary>Executa operação com retorno de valor.</summary>
    Task<Result<T>> ExecuteAsync<T>(OrmOperation operation, Func<CancellationToken, Task<Result<T>>> action, CancellationToken cancellationToken);

    /// <summary>Executa operação sem retorno de valor.</summary>
    Task<Result> ExecuteAsync(OrmOperation operation, Func<CancellationToken, Task<Result>> action, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IOrmOperationRunner" />
public sealed class OrmOperationRunner(
    ILogger<OrmOperationRunner> logger,
    OrmOptions options,
    IOrmExceptionTranslator? translator = null,
    TimeProvider? time = null)
    : IOrmOperationRunner
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly IOrmExceptionTranslator? _translator = translator ?? options.Dialect?.ExceptionTranslator;
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

        string dbSystem = options.Dialect?.ProviderName ?? "database";
        using var activity = OrmDiagnostics.ActivitySource.StartActivity($"{operation.Operation} {operation.Target}", ActivityKind.Client);
        if (activity is not null)
        {
            activity.SetTag(OrmDiagnostics.DbSystemTag, dbSystem);
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
                var elapsed = Stopwatch.GetElapsedTime(start);
                OrmDiagnostics.RecordOperation(dbSystem, operation.Provider, operation.Operation, operation.Target, elapsed.TotalSeconds,
                    OrmDiagnostics.CanceledErrorType);
                Complete(activity, OrmDiagnostics.CanceledErrorType, retries);
                OrmLog.Canceled(logger, operation.Provider, operation.Operation, operation.Target, FormatIdentifier(operation.Identifier),
                    (long)elapsed.TotalMilliseconds);
                throw;
            }
            catch (Exception exception)
            {
                var translation = Translate(exception);
                if (CanRetry(operation, exception, retries))
                {
                    retries++;
                    OrmLog.TransientFailureRetry(logger, operation.Provider, operation.Operation, operation.Target, translation.Error.Code,
                        translation.ErrorCode ?? "-", retries, options.TransientRetryCount);

                    if (!await DelayAsync(retries, cancellationToken).ConfigureAwait(false))
                    {
                        var elapsed = Stopwatch.GetElapsedTime(start);
                        OrmDiagnostics.RecordOperation(dbSystem, operation.Provider, operation.Operation, operation.Target, elapsed.TotalSeconds,
                            OrmDiagnostics.CanceledErrorType);
                        Complete(activity, OrmDiagnostics.CanceledErrorType, retries);
                        OrmLog.Canceled(logger, operation.Provider, operation.Operation, operation.Target,
                            FormatIdentifier(operation.Identifier), (long)elapsed.TotalMilliseconds);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    continue;
                }

                result = failure(translation.Error);
                LogException(operation, exception, translation, Stopwatch.GetElapsedTime(start));
                Finish(dbSystem, operation, activity, result, Stopwatch.GetElapsedTime(start), logged: true, retries);
                return result;
            }
        }

        Finish(dbSystem, operation, activity, result, Stopwatch.GetElapsedTime(start), logged: false, retries);
        return result;
    }

    private OrmDbExceptionTranslation Translate(Exception exception)
    {
        if (_translator != null)
            return _translator.Translate(exception);

        if (exception is InvalidOperationException inv && inv.Message.Contains("Concurrency", StringComparison.OrdinalIgnoreCase))
            return new OrmDbExceptionTranslation(OrmErrors.Concurrency(), false, null);

        return new OrmDbExceptionTranslation(OrmErrors.Failure(), false, null);
    }

    private bool CanRetry(OrmOperation operation, Exception exception, int retries)
    {
        if (operation.IsWrite || !operation.IsRetryable || retries >= options.TransientRetryCount)
            return false;

        return _translator?.IsTransient(exception) ?? false;
    }

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

    private void Finish(string dbSystem, OrmOperation operation, Activity? activity, Result result, TimeSpan elapsed, bool logged, int retries)
    {
        string? errorCode = result.Error?.Code;
        OrmDiagnostics.RecordOperation(dbSystem, operation.Provider, operation.Operation, operation.Target, elapsed.TotalSeconds, errorCode);
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

        if (translation.IsInfrastructure)
            OrmLog.InfrastructureFailure(logger, operation.Provider, operation.Operation, operation.Target, identifier,
                translation.Error.Code, exceptionType, translation.ErrorCode ?? "-", milliseconds);
        else if (operation.IsWrite)
            OrmLog.WriteFailure(logger, operation.Provider, operation.Operation, operation.Target, identifier, translation.Error.Code,
                milliseconds);
        else
            OrmLog.ExpectedFailure(logger, operation.Provider, operation.Operation, operation.Target, identifier,
                translation.Error.Code, milliseconds);
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

    internal string FormatIdentifier(object? identifier)
    {
        if (identifier is null || options.IdentifierLogMode == IdentifierLogMode.Omitted)
            return "-";

        string text = identifier is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : identifier.ToString() ?? string.Empty;

        bool hashed = options.IdentifierLogMode == IdentifierLogMode.Hashed ||
                      (options.IdentifierLogMode == IdentifierLogMode.Auto && !IsTechnicalKey(identifier));
        return hashed ? Hash(text) : Sanitize(text);
    }

    internal string FormatActor(string? actor)
    {
        if (string.IsNullOrEmpty(actor) || options.IdentifierLogMode == IdentifierLogMode.Omitted)
            return "-";
        bool hashed = options.IdentifierLogMode == IdentifierLogMode.Hashed ||
                      (options.IdentifierLogMode == IdentifierLogMode.Auto &&
                       !Guid.TryParse(actor, out _) && !long.TryParse(actor, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        return hashed ? Hash(actor) : Sanitize(actor);
    }

    private static bool IsTechnicalKey(object identifier) =>
        identifier is int or long or short or byte or uint or ulong or ushort or sbyte or Guid;

    private static string Hash(string text) => SensitiveDataMasker.DescribeUntrusted(text);

    private static string Sanitize(string text)
    {
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
