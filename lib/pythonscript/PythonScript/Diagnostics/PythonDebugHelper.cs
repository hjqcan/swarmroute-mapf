using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Python.Runtime;
using PythonScript.Diagnostics.Debugging;
using PythonScript.Diagnostics.Logging;

namespace PythonScript.Diagnostics
{
    /// <summary>
    /// pythonnet 调试辅助，提供对象透视功能。
    /// </summary>
    internal sealed class PythonDebugHelper
    {
        /// <summary>
        /// 查看对象公共成员变量值。
        /// </summary>
        public string Watch(object? obj, string name)
        {
            string message = XRay(obj, name);
            Write(LogLevel.Debug, "watch", message, name, obj);
            return message;
        }

        /// <summary>
        /// 查看对象类型。
        /// </summary>
        public string WatchType(object? obj)
        {
            string result = obj == null ? "null" : obj.GetType().FullName ?? obj.GetType().Name;
            Write(LogLevel.Debug, "watch_type", result, target: obj);
            return result;
        }

        /// <summary>
        /// 查看对象类型定义，仅输出类型名称。
        /// </summary>
        public string WatchTypeDefine(object? obj)
        {
            string result = obj?.GetType().ToString() ?? "null";
            Write(LogLevel.Debug, "watch_type_define", result, target: obj);
            return result;
        }

        /// <summary>
        /// 将对象透视为字符串。
        /// </summary>
        public string XRay(object? obj, string name)
        {
            if (obj == null)
            {
                string result = $"{name}[null]: null";
                Write(LogLevel.Debug, "xray", result, name, obj);
                return result;
            }

            if (obj is IPythonDebuggable custom)
            {
                string customResult = custom.XRay(name);
                Write(LogLevel.Debug, "xray", customResult, name, obj);
                return customResult;
            }

            string output = IsBasicValueType(obj) ? FormatValue(obj, name) : FormatObject(obj, name);
            Write(LogLevel.Debug, "xray", output, name, obj);
            return output;
        }

        private static bool IsBasicValueType(object obj)
        {
            Type type = obj.GetType();
            if (!type.IsValueType || type.IsEnum)
            {
                return type == typeof(string) || type == typeof(StringBuilder);
            }

            return type.GetFields().Length == 0 && type.GetProperties().Length == 0;
        }

        private static string FormatValue(object obj, string name)
        {
            Type type = obj.GetType();
            if (type.IsEnum)
            {
                return $"{name}[{type.FullName}]:{Enum.GetName(type, obj)}|{obj}";
            }

            return $"{name}[{type.Name}]: {obj}";
        }

        private static string FormatObject(object obj, string name)
        {
            try
            {
                return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return $"{name}[{obj.GetType().FullName}]: {obj}";
            }
        }

        private static void Write(LogLevel level, string action, string message, string? name = null, object? target = null)
        {
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["action"] = action,
                ["target_name"] = name,
                ["target_type"] = target?.GetType().FullName
            };

            switch (level)
            {
                case LogLevel.Trace:
                    PythonScriptLogger.Trace(message, payload);
                    break;
                case LogLevel.Debug:
                    PythonScriptLogger.Debug(message, payload);
                    break;
                case LogLevel.Info:
                    PythonScriptLogger.Info(message, payload);
                    break;
                case LogLevel.Warn:
                    PythonScriptLogger.Warn(message, payload);
                    break;
                case LogLevel.Error:
                    PythonScriptLogger.Error(message, null, payload);
                    break;
                case LogLevel.Fatal:
                    PythonScriptLogger.Fatal(message, null, payload);
                    break;
            }
        }
    }
}
