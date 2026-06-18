using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Python.Runtime;
using PythonScript.Exceptions;

namespace PythonScript.Diagnostics.Logging
{
    internal sealed class DefaultPythonScriptLogger : IPythonScriptLogger
    {
        private readonly object syncRoot = new();
        private readonly AsyncLocal<ScopeNode?> currentScope = new();

        private LogLevel minimumLevel;
        private Action<LogEvent> handler;
        private Func<LogEvent, string> formatter;

        public DefaultPythonScriptLogger(LogLevel minimumLevel = LogLevel.Info, Action<LogEvent>? handler = null, Func<LogEvent, string>? formatter = null)
        {
            this.minimumLevel = minimumLevel;
            this.handler = handler ?? DefaultHandler;
            this.formatter = formatter ?? DefaultFormatter;
        }

        public LogLevel MinimumLevel
        {
            get
            {
                lock (syncRoot)
                {
                    return minimumLevel;
                }
            }
        }

        public IDisposable BeginScope(string scopeName, IReadOnlyDictionary<string, object?>? properties = null)
        {
            var node = new ScopeNode(this, scopeName, properties, currentScope.Value);
            currentScope.Value = node;
            return node;
        }

        public void Log(LogEvent logEvent)
        {
            LogLevel currentLevel;
            Action<LogEvent> currentHandler;
            Func<LogEvent, string> currentFormatter;

            lock (syncRoot)
            {
                currentLevel = minimumLevel;
                currentHandler = handler;
                currentFormatter = formatter;
            }

            if (logEvent.Level < currentLevel)
            {
                return;
            }

            var mergedProperties = MergeProperties(currentScope.Value, logEvent.Properties);
            var mergedEvent = logEvent with { Properties = mergedProperties };
            string formatted = mergedEvent.FormattedMessage ?? currentFormatter(mergedEvent);
            currentHandler(mergedEvent with { FormattedMessage = formatted });
        }

        public void Configure(LogLevel minimumLevel, Action<LogEvent>? newHandler = null, Func<LogEvent, string>? newFormatter = null)
        {
            lock (syncRoot)
            {
                this.minimumLevel = minimumLevel;
                if (newHandler != null)
                {
                    handler = CreateCompositeHandler(newHandler);
                }
                if (newFormatter != null)
                {
                    formatter = newFormatter;
                }
            }
        }

        private static Action<LogEvent> CreateCompositeHandler(Action<LogEvent> customHandler)
        {
            return logEvent =>
            {
                try
                {
                    DefaultHandler(logEvent);
                }
                catch
                {
                    // 保持默认行为，即便自定义处理逻辑失败也不影响后续日志
                }

                customHandler(logEvent);
            };
        }

        private static IReadOnlyDictionary<string, object?> MergeProperties(ScopeNode? scope, IReadOnlyDictionary<string, object?> properties)
        {
            var merged = new Dictionary<string, object?>(StringComparer.Ordinal);

            if (scope != null)
            {
                var stack = new Stack<ScopeNode>();
                while (scope != null)
                {
                    stack.Push(scope);
                    scope = scope.Parent;
                }

                bool first = true;
                while (stack.Count > 0)
                {
                    var node = stack.Pop();
                    if (first)
                    {
                        merged["scope"] = node.ScopeName;
                        first = false;
                    }
                    else
                    {
                        merged[$"scope_{merged.Count}"] = node.ScopeName;
                    }

                    if (node.Properties != null)
                    {
                        foreach (var kv in node.Properties)
                        {
                            merged.TryAdd(kv.Key, kv.Value);
                        }
                    }
                }
            }

            if (properties != null)
            {
                foreach (var kv in properties)
                {
                    merged[kv.Key] = kv.Value;
                }
            }

            return merged;
        }

        private static string DefaultFormatter(LogEvent logEvent)
        {
            var builder = new StringBuilder();
            builder.Append('[')
                   .Append(logEvent.Timestamp.ToString("HH:mm:ss"))
                   .Append(' ')
                   .Append(logEvent.Level.ToString().ToUpperInvariant())
                   .Append("] ")
                   .Append(logEvent.Message);

            if (logEvent.Properties.Count > 0)
            {
                builder.Append(" | ");
                bool first = true;
                foreach (var kv in logEvent.Properties)
                {
                    if (!first)
                    {
                        builder.Append(", ");
                    }
                    builder.Append(kv.Key).Append('=').Append(FormatPropertyValue(kv.Value));
                    first = false;
                }
            }

            return builder.ToString();
        }

        private static void DefaultHandler(LogEvent logEvent)
        {
            string formatted = logEvent.FormattedMessage ?? DefaultFormatter(logEvent);
            if (logEvent.Level >= LogLevel.Error)
            {
                Console.Error.WriteLine(formatted);
            }
            else
            {
                Console.WriteLine(formatted);
            }
        }

        private static string FormatPropertyValue(object? value)
        {
            // 这里需要显式处理 PythonException，防止在未持有 GIL 时触发 pythonnet 的析构逻辑导致空引用异常
            if (value is null)
            {
                return "null";
            }

            try
            {
                if (value is PythonException pythonException)
                {
                    return FormatException(pythonException);
                }

                if (value is Exception exception)
                {
                    return FormatException(exception);
                }

                return value.ToString() ?? string.Empty;
            }
            catch (Exception formatError)
            {
                return $"<format-error:{formatError.Message}>";
            }
        }

        private static string FormatException(Exception exception)
        {
            if (exception is PythonException pythonException)
            {
                return SafeFormatPythonException(pythonException);
            }

            var builder = new StringBuilder();

            try
            {
                AppendException(builder, exception, 0);
            }
            catch (Exception formatError)
            {
                builder.Clear();
                builder.Append(exception.GetType().FullName)
                       .Append(": ")
                       .Append(exception.Message)
                       .Append(" (format-error: ")
                       .Append(formatError.Message)
                       .Append(')');
            }

            return builder.ToString();
        }

        private static void AppendException(StringBuilder builder, Exception exception, int depth)
        {
            if (exception is PythonException pythonException)
            {
                builder.Append(SafeFormatPythonException(pythonException));
                return;
            }

            builder.Append(exception.GetType().FullName)
                   .Append(": ")
                   .Append(exception.Message);

            if (exception is PythonScriptExecutionException scriptExecutionException && !string.IsNullOrEmpty(scriptExecutionException.PythonTraceback))
            {
                builder.AppendLine()
                       .Append(scriptExecutionException.PythonTraceback);
            }

            if (!string.IsNullOrEmpty(exception.StackTrace))
            {
                builder.AppendLine()
                       .Append(exception.StackTrace);
            }

            if (exception is AggregateException aggregateException)
            {
                int index = 0;
                foreach (var inner in aggregateException.InnerExceptions)
                {
                    builder.AppendLine()
                           .Append(new string(' ', (depth + 1) * 2))
                           .Append("[" + index++ + "] ");
                    AppendException(builder, inner, depth + 2);
                }
                return;
            }

            if (exception.InnerException != null)
            {
                builder.AppendLine()
                       .Append(new string(' ', depth * 2))
                       .Append("---> ");
                AppendException(builder, exception.InnerException, depth + 1);
            }
        }

        private static string SafeFormatPythonException(PythonException pythonException)
        {
            try
            {
                return PythonExceptionFormatter.Format(pythonException);
            }
            catch (Exception formatError)
            {
                return pythonException.GetType().FullName + ": " + pythonException.Message +
                       " (traceback unavailable: " + formatError.Message + ")";
            }
        }

        private sealed class ScopeNode : IDisposable
        {
            private readonly DefaultPythonScriptLogger owner;

            public ScopeNode(DefaultPythonScriptLogger owner, string scopeName, IReadOnlyDictionary<string, object?>? properties, ScopeNode? parent)
            {
                this.owner = owner;
                ScopeName = scopeName;
                Properties = properties;
                Parent = parent;
            }

            public string ScopeName { get; }
            public IReadOnlyDictionary<string, object?>? Properties { get; }
            public ScopeNode? Parent { get; }

            public void Dispose()
            {
                owner.currentScope.Value = Parent;
            }
        }
    }
}
