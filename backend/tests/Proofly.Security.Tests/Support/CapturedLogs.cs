using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Proofly.Security.Tests.Support;

/// <summary>Captures every log entry at every level, including structured state, scopes and the full exception text.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();
    public string All => string.Join('\n', _lines);
    public int Count => _lines.Count;

    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);
    public void Dispose() { }

    private sealed class Capture(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            owner._lines.Enqueue($"{category} SCOPE {Render(state)}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner._lines.Enqueue($"{category} {logLevel} {formatter(state, exception)} | {Render(state)} | {exception}");

        private static string Render<TState>(TState state)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> kv)
            {
                var sb = new StringBuilder();
                foreach (var (k, v) in kv) { sb.Append(k).Append('=').Append(v).Append(';'); }
                return sb.ToString();
            }
            return state?.ToString() ?? "";
        }
    }
}
