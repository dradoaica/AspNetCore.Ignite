using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using System;
using IgniteLogLevel = Apache.Ignite.Core.Log.LogLevel;
using IIgniteLogger = Apache.Ignite.Core.Log.ILogger;

namespace AspNetCore.IgniteServer.Utils;

/// <summary>Writes Ignite (Java and .NET) logs to Serilog.</summary>
internal sealed class SerilogIgniteLogger(ILogger logger) : IIgniteLogger
{
    public void Log(
        IgniteLogLevel level,
        string message,
        object[]? args,
        IFormatProvider? formatProvider,
        string? category,
        string? nativeErrorInfo,
        Exception? ex
    )
    {
        if (!IsEnabled(level))
        {
            return;
        }

        var text = Format(message, args, formatProvider);
        if (!string.IsNullOrEmpty(nativeErrorInfo))
        {
            text += Environment.NewLine + nativeErrorInfo;
        }

        var log = string.IsNullOrEmpty(category) ? logger
            : logger.ForContext(Constants.SourceContextPropertyName, category);
        // a text token keeps the message verbatim: Ignite messages are not Serilog message templates
        log.Write(
            new LogEvent(DateTimeOffset.Now, ConvertLevel(level), ex, new MessageTemplate([new TextToken(text),]), [])
        );
    }

    public bool IsEnabled(IgniteLogLevel level) =>
        level >= IgniteLogLevel.Info && logger.IsEnabled(ConvertLevel(level));

    // Debug and Trace are not forwarded: enabling them makes the Java side log at that level, which is very verbose
    internal static string Format(string message, object[]? args, IFormatProvider? formatProvider)
    {
        if (args is not { Length: > 0, })
        {
            return message;
        }

        try
        {
            return string.Format(formatProvider, message, args);
        }
        catch (FormatException)
        {
            // never fail the logging callback: keep the message and append the arguments
            return $"{message} [{string.Join(", ", args)}]";
        }
    }

    internal static LogEventLevel ConvertLevel(IgniteLogLevel level) => level switch
    {
        IgniteLogLevel.Trace => LogEventLevel.Verbose,
        IgniteLogLevel.Debug => LogEventLevel.Debug,
        IgniteLogLevel.Info => LogEventLevel.Information,
        IgniteLogLevel.Warn => LogEventLevel.Warning,
        IgniteLogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Fatal,
    };
}
