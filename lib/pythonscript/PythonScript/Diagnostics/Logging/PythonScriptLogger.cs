using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using PythonScript.Diagnostics.Logging;

namespace PythonScript
{
    /// <summary>
    /// 提供向后兼容的静态日志入口，内部委托给 <see cref="IPythonScriptLogger"/> 实现。
    /// </summary>
    public static class PythonScriptLogger
    {
        private static readonly IReadOnlyDictionary<string, object?> EmptyProperties = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal));

        private static IPythonScriptLogger logger = new DefaultPythonScriptLogger();

        public static LogLevel MinimumLevel => logger.MinimumLevel;

        internal static IPythonScriptLogger CurrentLogger => logger;

        public static void Configure(LogLevel minimumLevel, Action<LogEvent>? handler = null, Func<LogEvent, string>? formatter = null)
        {
            if (logger is DefaultPythonScriptLogger defaultLogger)
            {
                defaultLogger.Configure(minimumLevel, handler, formatter);
            }
            else
            {
                logger = new DefaultPythonScriptLogger(minimumLevel, handler, formatter);
            }
        }

        public static void UseLogger(IPythonScriptLogger customLogger)
        {
            ArgumentNullException.ThrowIfNull(customLogger);
            logger = customLogger;
        }

        public static IDisposable BeginScope(string scopeName, IDictionary<string, object?>? properties = null)
        {
            return logger.BeginScope(scopeName, ToReadOnly(properties));
        }

        public static void Trace(string message, IDictionary<string, object?>? properties = null) => Write(LogLevel.Trace, message, properties);

        public static void Debug(string message, IDictionary<string, object?>? properties = null) => Write(LogLevel.Debug, message, properties);

        public static void Info(string message, IDictionary<string, object?>? properties = null) => Write(LogLevel.Info, message, properties);

        public static void Warn(string message, IDictionary<string, object?>? properties = null) => Write(LogLevel.Warn, message, properties);

        public static void Error(string message, Exception? ex = null, IDictionary<string, object?>? properties = null) => Write(LogLevel.Error, message, AppendException(properties, ex));

        public static void Fatal(string message, Exception? ex = null, IDictionary<string, object?>? properties = null) => Write(LogLevel.Fatal, message, AppendException(properties, ex));

        private static void Write(LogLevel level, string message, IDictionary<string, object?>? properties)
        {
            if (level < logger.MinimumLevel)
            {
                return;
            }

            var logEvent = new LogEvent(level, message, DateTimeOffset.Now, ToReadOnly(properties));
            logger.Log(logEvent);
        }

        private static IDictionary<string, object?> AppendException(IDictionary<string, object?>? properties, Exception? ex)
        {
            var map = properties != null
                ? new Dictionary<string, object?>(properties, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);

            if (ex != null)
            {
                map["exception"] = ex;
            }

            return map;
        }

        private static IReadOnlyDictionary<string, object?> ToReadOnly(IDictionary<string, object?>? properties)
        {
            if (properties == null || properties.Count == 0)
            {
                return EmptyProperties;
            }

            return new ReadOnlyDictionary<string, object?>(properties is Dictionary<string, object?> dict
                ? dict
                : new Dictionary<string, object?>(properties, StringComparer.Ordinal));
        }
    }
}

