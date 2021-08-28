using NLog;
using NLog.Targets;
using Serilog;
using Serilog.Events;
using System;
using System.Linq;
using System.Text;

namespace AspNetCore.IgniteServer.Utils
{
    [Target("SerilogTarget")]
    public sealed class SerilogTarget : TargetWithLayout
    {
        public SerilogTarget(string name) => Name = name;

        protected override void Write(LogEventInfo logEvent)
        {
            Serilog.ILogger log = Log.ForContext(Serilog.Core.Constants.SourceContextPropertyName, logEvent.LoggerName);
            LogEventLevel logEventLevel = ConvertLevel(logEvent.Level);
            if ((logEvent.Parameters?.Length ?? 0) == 0)
            {
                // NLog treats a single string as a verbatim string; Serilog treats it as a String.Format format and hence collapses doubled braces
                // This is the most direct way to emit this without it being re-processed by Serilog (via @nblumhardt)
                MessageTemplate template = new MessageTemplate(new[] { new Serilog.Parsing.TextToken(logEvent.FormattedMessage) });
                log.Write(new LogEvent(DateTimeOffset.Now, logEventLevel, logEvent.Exception == null ? null : new Exception(DumpException(logEvent.Exception)), template, Enumerable.Empty<LogEventProperty>()));
            }
            else
            {
                // Risk: tunneling an NLog format and assuming it will Just Work as a Serilog format
                log.Write(logEventLevel, logEvent.Exception == null ? null : new Exception(DumpException(logEvent.Exception)), logEvent.Message, logEvent.Parameters);
            }
        }

        private static LogEventLevel ConvertLevel(LogLevel logEventLevel)
        {
            if (logEventLevel == LogLevel.Info)
            {
                return LogEventLevel.Information;
            }
            else if (logEventLevel == LogLevel.Trace)
            {
                return LogEventLevel.Verbose;
            }
            else if (logEventLevel == LogLevel.Debug)
            {
                return LogEventLevel.Debug;
            }
            else if (logEventLevel == LogLevel.Warn)
            {
                return LogEventLevel.Warning;
            }
            else if (logEventLevel == LogLevel.Error)
            {
                return LogEventLevel.Error;
            }

            return LogEventLevel.Fatal;
        }

        private static string DumpException(Exception e)
        {
            StringBuilder sb = new StringBuilder();
            while (e != null)
            {
                sb.AppendLine(e.Message);
                sb.AppendLine(e.StackTrace);
                e = e.InnerException;
            }

            return sb.ToString();
        }
    }
}
