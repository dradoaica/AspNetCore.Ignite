using AspNetCore.Ignite.Logging;
using AspNetCore.IgniteServer.Utils;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using IgniteLogLevel = Apache.Ignite.Core.Log.LogLevel;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace AspNetCore.Ignite.UnitTests;

public sealed class LoggerAdapterTests
{
    [Fact]
    public void SerilogIgniteLogger_WritesVerbatimMessages_AndSkipsDebug()
    {
        CollectingSink sink = new();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        SerilogIgniteLogger igniteLogger = new(logger);

        igniteLogger.Log(IgniteLogLevel.Warn, "a {not a template}", null, null, "org.apache.ignite.X", "native", null);
        igniteLogger.Log(IgniteLogLevel.Info, "value {0}", ["x"], null, null, null, null);
        igniteLogger.Log(IgniteLogLevel.Info, "bad {format} {0}", ["y"], null, null, null, null);
        igniteLogger.Log(IgniteLogLevel.Debug, "debug", null, null, null, null, null);

        Assert.Equal(3, sink.Events.Count);
        var logEvent = sink.Events[0];
        Assert.Equal(LogEventLevel.Warning, logEvent.Level);
        Assert.Equal($"a {{not a template}}{Environment.NewLine}native", logEvent.RenderMessage());
        Assert.Equal("value x", sink.Events[1].RenderMessage());
        Assert.Equal("bad {format} {0} [y]", sink.Events[2].RenderMessage());
        Assert.Equal("\"org.apache.ignite.X\"", logEvent.Properties[Constants.SourceContextPropertyName].ToString());
        Assert.False(igniteLogger.IsEnabled(IgniteLogLevel.Debug));
    }

    [Fact]
    public void IgniteLoggerAdapter_WritesToMicrosoftExtensionsLogging()
    {
        CollectingLogger logger = new();
        IgniteLoggerAdapter igniteLogger = new(logger);

        igniteLogger.Log(IgniteLogLevel.Error, "failed {0}", [42], null, "Client", null, null);

        var (level, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, level);
        Assert.Equal("[Client] failed 42", message);
        Assert.False(igniteLogger.IsEnabled(IgniteLogLevel.Trace));
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
        }
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Information;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}