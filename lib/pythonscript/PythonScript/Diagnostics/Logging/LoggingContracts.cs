using System;
using System.Collections.Generic;

namespace PythonScript.Diagnostics.Logging
{
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4,
        Fatal = 5
    }

    public readonly record struct LogEvent(LogLevel Level, string Message, DateTimeOffset Timestamp, IReadOnlyDictionary<string, object?> Properties)
    {
        public string? FormattedMessage { get; init; }
    }

    public interface IPythonScriptLogger
    {
        LogLevel MinimumLevel { get; }

        IDisposable BeginScope(string scopeName, IReadOnlyDictionary<string, object?>? properties = null);

        void Log(LogEvent logEvent);
    }
}
