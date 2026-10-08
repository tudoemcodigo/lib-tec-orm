using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace TEC.ORM.LoadTests.Infrastructure;

/// <summary>Logger que guarda tudo o que foi escrito (mensagem + exceção), para provar que valores sensíveis não vazam sob carga.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, EventId EventId, string Text)> Entries { get; } = new();

    public string AllText => string.Join('\n', Entries.Select(e => e.Text));

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider.Entries.Enqueue((logLevel, eventId, formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception)));
    }
}
