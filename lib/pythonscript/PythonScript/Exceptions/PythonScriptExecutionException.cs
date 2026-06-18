using System;
using System.Linq;
using System.Text;
using Python.Runtime;

namespace PythonScript.Exceptions
{
    /// <summary>
    /// 脚本执行异常。
    /// </summary>
    public class PythonScriptExecutionException : Exception
    {
        public string? PythonTraceback { get; private set; }
        public string? PythonExceptionType { get; private set; }
        public string? PythonExceptionMessage { get; private set; }

        public PythonScriptExecutionException(string message)
            : base(message)
        {
        }

        public PythonScriptExecutionException(string message, Exception innerException)
            : this(message, innerException, null)
        {
        }

        public PythonScriptExecutionException(string message, Exception innerException, string? pythonTraceback)
            : base(message, innerException)
        {
            PythonTraceback = pythonTraceback ?? (innerException is PythonException pythonException
                ? PythonExceptionFormatter.Format(pythonException)
                : null);
            
            if (innerException is PythonException pyEx)
            {
                PythonExceptionType = pyEx.GetType().Name;
                PythonExceptionMessage = pyEx.Message;
            }
        }
        
        /// <summary>
        /// 创建一个不包含 InnerException 的包装异常，避免 xUnit 访问 PythonException.StackTrace 导致崩溃
        /// </summary>
        public static PythonScriptExecutionException FromPythonException(string message, PythonException pythonException, string? pythonTraceback = null)
        {
            var traceback = pythonTraceback ?? PythonExceptionFormatter.Format(pythonException);
            
            // 提取Python异常类型名称（如RuntimeError, TypeError等）
            string? pythonExceptionType = null;
            try
            {
                var typeStr = pythonException.Type?.ToString();
                pythonExceptionType = typeStr?.Split('.').LastOrDefault();
            }
            catch
            {
                // 如果无法提取，使用C#异常类型作为fallback
                pythonExceptionType = pythonException.GetType().Name;
            }
            
            return new PythonScriptExecutionException(message)
            {
                PythonTraceback = traceback,
                PythonExceptionType = pythonExceptionType ?? "PythonException",
                PythonExceptionMessage = pythonException.Message
            };
        }

        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append(GetType().FullName)
                   .Append(": ")
                   .Append(Message);

            // 输出Python异常类型和消息（如果有）
            if (!string.IsNullOrEmpty(PythonExceptionType))
            {
                builder.AppendLine()
                       .Append("Python Exception Type: ")
                       .Append(PythonExceptionType);
            }

            if (!string.IsNullOrEmpty(PythonExceptionMessage))
            {
                builder.AppendLine()
                       .Append("Python Exception Message: ")
                       .Append(PythonExceptionMessage);
            }

            if (!string.IsNullOrEmpty(PythonTraceback))
            {
                builder.AppendLine()
                       .Append("Python Traceback:")
                       .AppendLine()
                       .Append(PythonTraceback);
            }

            if (InnerException != null)
            {
                builder.AppendLine()
                       .Append("Inner Exception: ")
                       .Append(FormatInnerException(InnerException));
            }

            if (!string.IsNullOrEmpty(StackTrace))
            {
                builder.AppendLine()
                       .Append(StackTrace);
            }

            return builder.ToString();
        }

        private static string FormatInnerException(Exception ex)
        {
            if (ex is PythonException pythonException)
            {
                return PythonExceptionFormatter.Format(pythonException);
            }

            var builder = new StringBuilder();
            AppendException(builder, ex, 0);
            return builder.ToString();
        }

        private static void AppendException(StringBuilder builder, Exception ex, int depth)
        {
            builder.Append(ex.GetType().FullName)
                   .Append(": ")
                   .Append(ex.Message ?? string.Empty);

            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                builder.AppendLine()
                       .Append(ex.StackTrace);
            }

            if (ex is AggregateException aggregateException)
            {
                int index = 0;
                foreach (var inner in aggregateException.InnerExceptions)
                {
                    builder.AppendLine()
                           .Append(new string(' ', (depth + 1) * 2))
                           .Append('[')
                           .Append(index++)
                           .Append("] ");
                    AppendException(builder, inner, depth + 1);
                }
                return;
            }

            if (ex.InnerException != null)
            {
                builder.AppendLine()
                       .Append(new string(' ', depth * 2))
                       .Append("---> ");
                AppendException(builder, ex.InnerException, depth + 1);
            }
        }
    }
}
