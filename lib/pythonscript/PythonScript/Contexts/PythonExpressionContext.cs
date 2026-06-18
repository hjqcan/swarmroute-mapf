using System;
using System.IO;
using Python.Runtime;
using PythonScript.Exceptions;
using PythonScript.Runtime;

namespace PythonScript.Contexts
{
    /// <summary>
    /// 提供表达式执行能力的上下文。
    /// </summary>
    public sealed class PythonExpressionContext : PythonContextBase
    {
        public PythonExpressionContext(IPythonRuntimeSession? session = null, PythonBindingRegistry? registry = null)
            : base(session, registry)
        {
        }

        public object? ExecuteSource(string source)
        {
            EnsureNotDisposed();
            if (string.IsNullOrWhiteSpace(source)) return null;

            try
            {
                return RuntimeSession.WithGil(() =>
                {
                    using PyDict globals = new PyDict(Scope.GetAttr("__dict__"));
                    using PyObject result = PythonEngine.Eval(source, globals, globals);
                    return ToManaged(result);
                });
            }
            catch (PythonException ex)
            {
                string formatted = PythonExceptionFormatter.Format(ex);
                PythonScriptLogger.Error(formatted, ex);
                // 使用FromPythonException避免xUnit访问PythonException.StackTrace导致崩溃
                throw PythonScriptExecutionException.FromPythonException(formatted, ex, formatted);
            }
        }

        public bool TryExecuteSource(string source, out object? result, out string? error)
        {
            result = null;
            error = null;

            try
            {
                result = ExecuteSource(source);
                return true;
            }
            catch (PythonScriptExecutionException ex)
            {
                // PythonScriptExecutionException已经包含格式化的traceback
                error = ex.PythonTraceback ?? ex.Message;
                return false;
            }
        }

        public object? ExecuteFile(string path)
        {
            EnsureNotDisposed();
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException("Python file not found.", path);

            string code = File.ReadAllText(path);
            return ExecuteSource(code);
        }

        private static object? ToManaged(PyObject? value)
        {
            if (value == null || value.IsNone())
            {
                return null;
            }
            return value.AsManagedObject(typeof(object));
        }
    }
}

