using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Python.Runtime;
using PythonScript.Diagnostics.Logging;
using PythonScript.Exceptions;

namespace PythonScript.Diagnostics
{
    /// <summary>
    /// 提供从 Python 脚本调用 C# 日志系统的桥接接口。
    /// 注入到 Python 作用域后，脚本可通过 `log.info("message")` 等方式记录日志。
    /// </summary>
    public sealed class PythonScriptLogBridge
    {
        private readonly IPythonScriptLogger? explicitLogger;

        public PythonScriptLogBridge(IPythonScriptLogger? customLogger = null)
        {
            explicitLogger = customLogger;
        }

        /// <summary>
        /// 记录 Trace 级别日志
        /// </summary>
        public void trace(string message, object? properties = null)
        {
            Write(LogLevel.Trace, message, properties);
        }

        /// <summary>
        /// 记录 Debug 级别日志
        /// </summary>
        public void debug(string message, object? properties = null)
        {
            Write(LogLevel.Debug, message, properties);
        }

        /// <summary>
        /// 记录 Info 级别日志
        /// </summary>
        public void info(string message, object? properties = null)
        {
            Write(LogLevel.Info, message, properties);
        }

        /// <summary>
        /// 记录 Warning 级别日志
        /// </summary>
        public void warn(string message, object? properties = null)
        {
            Write(LogLevel.Warn, message, properties);
        }

        /// <summary>
        /// 记录 Error 级别日志
        /// </summary>
        public void error(string message, object? ex = null, object? properties = null)
        {
            Write(LogLevel.Error, message, properties, ex);
        }

        /// <summary>
        /// 记录 Fatal 级别日志
        /// </summary>
        public void fatal(string message, object? ex = null, object? properties = null)
        {
            Write(LogLevel.Fatal, message, properties, ex);
        }

        private void Write(LogLevel level, string message, object? properties, object? exceptionCandidate = null)
        {
            var normalized = NormalizeProperties(properties);
            var exception = ConvertException(exceptionCandidate, normalized);

            if (explicitLogger != null)
            {
                if (level < explicitLogger.MinimumLevel)
                {
                    return;
                }

                if (exception != null)
                {
                    normalized["exception"] = exception;
                }

                var logEvent = new LogEvent(level, message, DateTimeOffset.Now, new ReadOnlyDictionary<string, object?>(normalized));
                explicitLogger.Log(logEvent);
                return;
            }

            if (level < PythonScriptLogger.MinimumLevel)
            {
                return;
            }

            DispatchToStaticLogger(level, message, normalized, exception);
        }

        private static Dictionary<string, object?> NormalizeProperties(object? properties)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);

            if (properties == null)
            {
                return result;
            }

            if (properties is IReadOnlyDictionary<string, object?> readOnly)
            {
                foreach (var kv in readOnly)
                {
                    result[kv.Key] = kv.Value;
                }
                return result;
            }

            if (properties is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Key is string key)
                    {
                        result[key] = entry.Value;
                    }
                }
                return result;
            }

            if (properties is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var kv in pairs)
                {
                    result[kv.Key] = kv.Value;
                }
                return result;
            }

            if (properties is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is DictionaryEntry entry && entry.Key is string entryKey)
                    {
                        result[entryKey] = entry.Value;
                    }
                    else if (item is object?[] array && array.Length >= 2 && array[0] is string key)
                    {
                        result[key] = array[1];
                    }
                    else if (item is ValueTuple<string, object?> tuple)
                    {
                        result[tuple.Item1] = tuple.Item2;
                    }
                    else if (item is Tuple<string, object?> tuple2)
                    {
                        result[tuple2.Item1] = tuple2.Item2;
                    }
                }
                return result;
            }

            result["value"] = properties;
            return result;
        }

        private static Exception? ConvertException(object? candidate, IDictionary<string, object?> properties)
        {
            if (candidate == null)
            {
                return null;
            }

            if (candidate is Exception ex)
            {
                return ex;
            }

            if (candidate is PythonException pythonException)
            {
                properties["python_exception"] = PythonExceptionFormatter.Format(pythonException);
                return pythonException;
            }

            if (candidate is PyObject pyObject)
            {
                using var gil = Py.GIL();

                if (pyObject.IsNone())
                {
                    return null;
                }

                try
                {
                    string? text = pyObject.ToString();
                    if (string.IsNullOrEmpty(text))
                    {
                        text = pyObject.Repr();
                    }

                    if (!string.IsNullOrEmpty(text))
                    {
                        properties["python_exception"] = text;
                    }
                }
                catch (Exception formatError)
                {
                    properties["python_exception"] = "<format-error:" + formatError.Message + ">";
                }

                return null;
            }

            properties["exception_object"] = candidate;
            return null;
        }

        private static void DispatchToStaticLogger(LogLevel level, string message, IDictionary<string, object?> properties, Exception? exception)
        {
            switch (level)
            {
                case LogLevel.Trace:
                    PythonScriptLogger.Trace(message, properties);
                    break;
                case LogLevel.Debug:
                    PythonScriptLogger.Debug(message, properties);
                    break;
                case LogLevel.Info:
                    PythonScriptLogger.Info(message, properties);
                    break;
                case LogLevel.Warn:
                    PythonScriptLogger.Warn(message, properties);
                    break;
                case LogLevel.Error:
                    PythonScriptLogger.Error(message, exception, properties);
                    break;
                case LogLevel.Fatal:
                    PythonScriptLogger.Fatal(message, exception, properties);
                    break;
                default:
                    PythonScriptLogger.Info(message, properties);
                    break;
            }
        }
    }
}

