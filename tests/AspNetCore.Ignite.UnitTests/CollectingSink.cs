using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Collections.Concurrent;

namespace AspNetCore.Ignite.UnitTests;

/// <summary>Keeps the events logged to it, from any thread, for the tests that check what was logged.</summary>
internal sealed class CollectingSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> events = new();

    public IReadOnlyList<LogEvent> Events => [.. events,];

    public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);

    /// <summary>Whether a message that contains <paramref name="text" /> was logged.</summary>
    public bool Logged(string text) =>
        events.Any(logEvent => logEvent.RenderMessage().Contains(text, StringComparison.Ordinal));

    /// <summary>A logger that writes to this sink.</summary>
    public Logger CreateLogger() => new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(this).CreateLogger();
}
