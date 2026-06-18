using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace PythonScript.Diagnostics.Logging
{
    /// <summary>
    /// 将 <see cref="IPythonScriptLogger"/> 与 <see cref="ILogger"/> 对接的适配器。
    /// </summary>
    public sealed class LoggerFactoryAdapter : IPythonScriptLogger
    {
        private static readonly IDisposable NullScope = new NullDisposable();

        private readonly ILogger logger;
        private readonly LogLevel minimumLevel;

        public LoggerFactoryAdapter(ILogger logger, LogLevel minimumLevel = LogLevel.Trace)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.minimumLevel = minimumLevel;
        }

        public LogLevel MinimumLevel => minimumLevel;

        public IDisposable BeginScope(string scopeName, IReadOnlyDictionary<string, object?>? properties = null)
        {
            if (properties == null || properties.Count == 0)
            {
                return logger.BeginScope(scopeName) ?? NullScope;
            }

            return logger.BeginScope(new ScopeState(scopeName, properties)) ?? NullScope;
        }

        public void Log(LogEvent logEvent)
        {
            if (logEvent.Level < minimumLevel)
            {
                return;
            }

            var msLevel = ConvertLevel(logEvent.Level);
            if (!logger.IsEnabled(msLevel))
            {
                return;
            }

            logger.Log(msLevel, default(EventId), logEvent, null, (state, _) => state.FormattedMessage ?? state.Message);
        }

        private static Microsoft.Extensions.Logging.LogLevel ConvertLevel(LogLevel level) => level switch
        {
            LogLevel.Trace => Microsoft.Extensions.Logging.LogLevel.Trace,
            LogLevel.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
            LogLevel.Info => Microsoft.Extensions.Logging.LogLevel.Information,
            LogLevel.Warn => Microsoft.Extensions.Logging.LogLevel.Warning,
            LogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
            LogLevel.Fatal => Microsoft.Extensions.Logging.LogLevel.Critical,
            _ => Microsoft.Extensions.Logging.LogLevel.Information
        };

        private sealed record ScopeState(string ScopeName, IReadOnlyDictionary<string, object?> Properties)
        {
            public override string ToString() => ScopeName;
        }

        private sealed class NullDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
