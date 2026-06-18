using Python.Runtime;
using PythonScript.Exceptions;
using PythonScript.Runtime;
using PythonScript.SyntaxStaticCheck;

namespace PythonScript.Contexts
{
    /// <summary>
    /// 脚本执行上下文（pythonnet）。
    /// </summary>
    public sealed class PythonScriptRuntime : PythonContextBase
    {
        private readonly ReaderWriterLockSlim stateLock = new(LockRecursionPolicy.SupportsRecursion);

        private string? compiledSource;
        private bool executed;
        private readonly PythonSyntaxChecker syntaxCheck = new();

        public PythonScriptRuntime(string script, IPythonRuntimeSession? session = null, PythonBindingRegistry? registry = null)
            : base(session, registry)
        {
            if (string.IsNullOrWhiteSpace(script))
            {
                throw new ArgumentNullException(nameof(script));
            }

            Script = new PythonScriptFile(script);
        }

        /// <summary>
        /// 当前脚本文件（可重新指定）。
        /// </summary>
        public PythonScriptFile? Script { get; set; }

        /// <summary>
        /// 脚本是否已编译。
        /// </summary>
        public bool IsCompiled { get; private set; }

        /// <summary>
        /// 脚本是否处于执行状态。
        /// </summary>
        public bool IsRunning { get; private set; }

        protected override void OnBeforeReset()
        {
            stateLock.EnterWriteLock();
        }

        protected override void OnAfterReset()
        {
            compiledSource = null;
            IsCompiled = false;
            executed = false;
            IsRunning = false;

            stateLock.ExitWriteLock();
        }

        protected override void OnDisposing()
        {
            if (stateLock.IsWriteLockHeld)
            {
                stateLock.ExitWriteLock();
            }
            if (stateLock.IsReadLockHeld)
            {
                stateLock.ExitReadLock();
            }
            stateLock.Dispose();
        }

        /// <summary>
        /// 重置上下文并重新编译脚本。
        /// </summary>
        public bool Reset(out PythonScriptCompilationResult result)
        {
            base.Reset();
            result = Compile();
            return !result.HasError;
        }

        /// <summary>
        /// 编译当前脚本文件。
        /// </summary>
        public PythonScriptCompilationResult Compile()
        {
            stateLock.EnterWriteLock();
            try
            {
                var result = CompileInternal();
                if (!result.HasError)
                {
                    executed = false;
                }
                return result;
            }
            finally
            {
                stateLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// 执行脚本中指定函数。
        /// </summary>
        public object? ExecuteFunction(string name, params object?[] args)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException(nameof(name));

            EnsureCompiled();

            stateLock.EnterWriteLock();
            try
            {
                EnsureExecuted();

                return RuntimeSession.WithGil(() =>
                {
                    if (!Scope.HasAttr(name))
                    {
                        throw new PythonScriptObjectNotFoundException(this, name);
                    }

                    using var func = Scope.GetAttr(name);
                    using var result = InvokeFunction(func, args);
                    return ToManaged(result);
                });
            }
            catch (PythonException ex)
            {
                // 立即在 GIL 上下文中提取异常信息，避免 xUnit 在没有 GIL 时访问 PythonException.StackTrace
                string formatted = PythonExceptionFormatter.Format(ex);
                PythonScriptLogger.Error(formatted, ex);
                
                // 创建一个不包含 PythonException 作为 InnerException 的异常，避免 xUnit 访问导致崩溃
                var wrappedException = PythonScriptExecutionException.FromPythonException($"执行函数 '{name}' 出错: {ex.Message}", ex, formatted);
                throw wrappedException;
            }
            finally
            {
                stateLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// 执行脚本 Main 函数。
        /// </summary>
        public int ExecuteMainFunction(params object?[] args)
        {
            object? value = ExecuteFunction(FUNCTION_MAIN, args);
            try
            {
                return Convert.ToInt32(value);
            }
            catch (Exception ex)
            {
                if (ex is PythonException pythonException)
                {
                    string info = PythonExceptionFormatter.Format(pythonException);
                    PythonScriptLogger.Error(info ?? pythonException.Message, pythonException);
                    // 使用FromPythonException避免xUnit访问PythonException.StackTrace导致崩溃
                    throw PythonScriptExecutionException.FromPythonException("Main 返回值无法转换为 int。", pythonException, info);
                }

                throw new PythonScriptExecutionException("Main 返回值无法转换为 int。", ex, null);
            }
        }

        /// <summary>
        /// 获取脚本中的 Python 对象。
        /// </summary>
        public bool TryGetPythonObject(string name, out object? value)
        {
            EnsureCompiled();

            stateLock.EnterReadLock();
            try
            {
                bool success = false;
                object? resultValue = null;

                RuntimeSession.WithGil(() =>
                {
                    if (!Scope.HasAttr(name))
                    {
                        return;
                    }

                    using var obj = Scope.GetAttr(name);
                    resultValue = ToManaged(obj);
                    success = true;
                });

                value = resultValue;
                return success;
            }
            catch (PythonException ex)
            {
                var info = PythonExceptionFormatter.Format(ex);
                PythonScriptLogger.Error(info ?? ex.Message, ex);
                value = null;
                return false;
            }
            finally
            {
                stateLock.ExitReadLock();
            }
        }

        /// <summary>
        /// 检查脚本语法，不执行代码。
        /// </summary>
        public PythonScriptCompilationResult CheckScriptSyntax()
        {
            stateLock.EnterReadLock();
            try
            {
                return CompileInternal(checkOnly: true);
            }
            finally
            {
                stateLock.ExitReadLock();
            }
        }

        private PythonScriptCompilationResult CompileInternal(bool checkOnly = false)
        {
            var result = new PythonScriptCompilationResult();

            if (Script == null || string.IsNullOrEmpty(Script.Source))
            {
                result.AddError("脚本路径为空。");
                return result;
            }

            if (!File.Exists(Script.Source))
            {
                result.AddError($"脚本文件不存在: {Script.Source}");
                return result;
            }

            string sourceCode = File.ReadAllText(Script.Source);

            try
            {
                RuntimeSession.WithGil(() =>
                {
                    using var codeObj = PythonEngine.Compile(sourceCode, Script.Source, RunFlagType.File);
                    if (checkOnly)
                    {
                        result.AddErrors(syntaxCheck.AnalyzeSyntaxErrors(sourceCode));
                        return;
                    }

                    compiledSource = sourceCode;
                    IsCompiled = true;
                });
            }
            catch (PythonException ex)
            {
                // 访问 PythonException 的属性需要在 GIL 保护下进行
                var (line, column, message) = RuntimeSession.WithGil(() => ExtractCompileErrorInfo(ex));
                result.AddError(message, line, column);
                compiledSource = null;
                IsCompiled = false;
            }

            if (!checkOnly && result.HasError == false)
            {
                result.AddErrors(syntaxCheck.AnalyzeSyntaxErrors(sourceCode));
            }

            return result;
        }

        private void EnsureCompiled()
        {
            if (IsCompiled) return;

            var compileResult = Compile();
            if (compileResult.HasError)
            {
                throw new PythonScriptCompilationException(Script, compileResult.ToString());
            }
        }

        private void EnsureExecuted()
        {
            if (executed) return;

            if (compiledSource == null)
            {
                throw new InvalidOperationException("脚本尚未编译。");
            }

            IsRunning = true;

            try
            {
                var source = compiledSource;
                if (source == null)
                {
                    throw new InvalidOperationException("脚本尚未编译。");
                }

                RuntimeSession.WithGil(() => Scope.Exec(source));
                executed = true;
            }
            catch (PythonException ex)
            {
                // 立即在 GIL 上下文中提取异常信息
                var info = PythonExceptionFormatter.Format(ex);
                PythonScriptLogger.Error(info ?? ex.Message, ex);
                
                // 创建不包含 PythonException 作为 InnerException 的包装异常
                var wrappedException = PythonScriptExecutionException.FromPythonException(info ?? ex.Message, ex, info);
                throw wrappedException;
            }
            finally
            {
                IsRunning = false;
            }
        }

        private PyObject InvokeFunction(PyObject function, object?[]? args)
        {
            if (args == null || args.Length == 0)
            {
                return function.Invoke();
            }

            var pyArgs = new PyObject[args.Length];
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    pyArgs[i] = ToPython(args[i]);
                }

                return function.Invoke(pyArgs);
            }
            finally
            {
                for (int i = 0; i < pyArgs.Length; i++)
                {
                    pyArgs[i]?.Dispose();
                }
            }
        }

        private PyObject ToPython(object? value)
        {
            if (value is null)
            {
                return Python.Runtime.Runtime.None;
            }
            
            // 使用 .ToPython() 扩展方法，它会正确转换基本类型为 Python 原生类型
            // 例如 int -> Python int, string -> Python str
            return value.ToPython();
        }

        private static object? ToManaged(PyObject? value)
        {
            if (value == null || value.IsNone())
            {
                return null;
            }
            
            // 尝试将 Python 基本类型转换为对应的 C# 类型
            // 这样 Assert.Equal(16, sum) 才能正确比较
            try
            {
                // 尝试转换为常见的 C# 基本类型
                var typeName = value.GetPythonType().Name;
                
                // Python int -> C# int/long
                if (typeName == "int")
                {
                    try
                    {
                        return value.As<int>();
                    }
                    catch
                    {
                        return value.As<long>();
                    }
                }
                
                // Python float -> C# double
                if (typeName == "float")
                {
                    return value.As<double>();
                }
                
                // Python str -> C# string
                if (typeName == "str")
                {
                    return value.As<string>();
                }
                
                // Python bool -> C# bool
                if (typeName == "bool")
                {
                    return value.As<bool>();
                }
            }
            catch
            {
                // 如果转换失败，使用默认转换
            }
            
            // 其他类型使用默认转换
            return value.AsManagedObject(typeof(object));
        }

        private static (int line, int column, string message) ExtractCompileErrorInfo(PythonException ex)
        {
            int line = 0;
            int column = 0;
            string message = ex.Message;

            try
            {
                var value = ex.Value;
                if (value != null)
                {
                    if (value.HasAttr("lineno"))
                    {
                        using var lineno = value.GetAttr("lineno");
                        line = lineno.As<int>();
                    }
                    if (value.HasAttr("offset"))
                    {
                        using var offset = value.GetAttr("offset");
                        column = offset.As<int>();
                    }
                    if (value.HasAttr("msg"))
                    {
                        using var msg = value.GetAttr("msg");
                        message = msg.As<string>();
                    }
                }
            }
            catch
            {
                // ignore
            }

            return (line, column, message);
        }

        private static string? ExtractRuntimeErrorInfo(PythonException ex)
        {
            try
            {
                var tb = ex.Traceback;
                if (tb != null)
                {
                    using (tb)
                    {
                        using dynamic tbModule = Py.Import("traceback");
                        using PyObject pyList = tbModule.format_exception(ex.Type, ex.Value, tb);
                        var lines = pyList.As<IEnumerable<string>>() ?? Enumerable.Empty<string>();
                        return string.Join(Environment.NewLine, lines);
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private const string FUNCTION_MAIN = "Main";
    }
}

