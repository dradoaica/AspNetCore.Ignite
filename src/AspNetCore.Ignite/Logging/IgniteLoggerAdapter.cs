using Microsoft.Extensions.Logging;
using System;
using IgniteLogLevel = Apache.Ignite.Core.Log.LogLevel;
using IIgniteLogger = Apache.Ignite.Core.Log.ILogger;

namespace AspNetCore.Ignite.Logging;

/// <summary>Writes the logs of the Ignite thin client to Microsoft.Extensions.Logging.</summary>
public sealed class IgniteLoggerAdapter(ILogger logger) : IIgniteLogger
{
    public const string CategoryName = "Apache.Ignite";

    public IgniteLoggerAdapter(ILoggerFactory loggerFactory) : this(loggerFactory.CreateLogger(CategoryName))
    {
    }

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
        var logLevel = ConvertLevel(level);
        if (!logger.IsEnabled(logLevel))
        {
            return;
        }

        var text = Format(message, args, formatProvider);
        if (!string.IsNullOrEmpty(nativeErrorInfo))
        {
            text += Environment.NewLine + nativeErrorInfo;
        }

        // the message is passed as state, not as a template: Ignite messages may contain braces
        logger.Log(logLevel, default, (text, category), ex, static (state, _) => state.category is null ? state.text
            : $"[{state.category}] {state.text}");
    }

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

    public bool IsEnabled(IgniteLogLevel level) => logger.IsEnabled(ConvertLevel(level));

    internal static LogLevel ConvertLevel(IgniteLogLevel level) => level switch
    {
        IgniteLogLevel.Trace => LogLevel.Trace,
        IgniteLogLevel.Debug => LogLevel.Debug,
        IgniteLogLevel.Info => LogLevel.Information,
        IgniteLogLevel.Warn => LogLevel.Warning,
        IgniteLogLevel.Error => LogLevel.Error,
        _ => LogLevel.Critical,
    };
}
