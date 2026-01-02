using NLog;
using NLog.Targets;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using System;
using System.Text;

namespace AspNetCore.IgniteServer.Utils;

[Target("SerilogTarget")]
internal sealed class SerilogTarget : TargetWithLayout
{
    public SerilogTarget(string name)
    {
        Name = name;
    }

    protected override void Write(LogEventInfo logEvent)
    {
        var log = Log.ForContext(Constants.SourceContextPropertyName, logEvent.LoggerName);
        var logEventLevel = ConvertLevel(logEvent.Level);
        if ((logEvent.Parameters?.Length ?? 0) == 0)
        {
            // NLog treats a single string as a verbatim string; Serilog treats it as a String.Format format and hence collapses doubled braces
            // This is the most direct way to emit this without it being re-processed by Serilog (via @nblumhardt)
            MessageTemplate template = new(
                [
                    new TextToken(logEvent.FormattedMessage),
                ]
            );
            log.Write(
                new LogEvent(
                    DateTimeOffset.Now,
                    logEventLevel,
                    logEvent.Exception == null ? null : new Exception(DumpException(logEvent.Exception)),
                    template,
                    []
                )
            );
        }
        else
        {
            // Risk: tunneling an NLog format and assuming it will Just Work as a Serilog format
            log.Write(
                logEventLevel,
                logEvent.Exception == null ? null : new Exception(DumpException(logEvent.Exception)),
                logEvent.Message,
                logEvent.Parameters
            );
        }

        var nativeErrorInfo = logEvent.Properties.TryGetValue("nativeErrorInfo", out var property) ? property as string
            : null;
        if (!string.IsNullOrEmpty(nativeErrorInfo))
        {
            log.Write(logEventLevel, nativeErrorInfo);
        }
    }

    private static LogEventLevel ConvertLevel(LogLevel logEventLevel)
    {
        if (logEventLevel == LogLevel.Info)
        {
            return LogEventLevel.Information;
        }

        if (logEventLevel == LogLevel.Trace)
        {
            return LogEventLevel.Verbose;
        }

        if (logEventLevel == LogLevel.Debug)
        {
            return LogEventLevel.Debug;
        }

        if (logEventLevel == LogLevel.Warn)
        {
            return LogEventLevel.Warning;
        }

        return logEventLevel == LogLevel.Error ? LogEventLevel.Error : LogEventLevel.Fatal;
    }

    private static string DumpException(Exception ex)
    {
        StringBuilder sb = new();
        while (ex != null)
        {
            sb.AppendLine(ex.Message);
            sb.AppendLine(ex.StackTrace);
            ex = ex.InnerException;
        }

        return sb.ToString();
    }
}
