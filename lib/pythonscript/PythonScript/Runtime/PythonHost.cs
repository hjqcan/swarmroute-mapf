using System;
using System.IO;
using Python.Runtime;

namespace PythonScript.Runtime
{
    /// <summary>
    /// pythonnet 静态入口，用于统一初始化、作用域创建与 GIL 管理。
    /// </summary>
    public static class PythonHost
    {
        private static readonly object InitLock = new();
        private static bool initialized;
        private static IntPtr mainThreadState;
        private static int initializingThreadId = -1;

        public static void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            lock (InitLock)
            {
                if (initialized)
                {
                    return;
                }

                // 根据 pythonnet 官方文档：https://github.com/pythonnet/pythonnet/wiki/Threading
                // Initialize() 后主线程自动持有 GIL，无需再用 Py.GIL() 包裹
                PythonEngine.Initialize();
                
                // 此时主线程已持有 GIL，可以直接执行 Python 操作
                Py.Import("importlib");
                Py.Import("clr");

                // 释放 GIL，允许其他线程通过 Py.GIL() 获取
                mainThreadState = PythonEngine.BeginAllowThreads();
                initializingThreadId = Environment.CurrentManagedThreadId;
                initialized = true;
            }
        }

        public static bool TryEnsureInitialized(out string? error)
        {
            try
            {
                EnsureInitialized();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 创建 Python 作用域。
        /// ⚠️ 警告：返回的 PyModule 对象的所有操作（包括访问属性和 Dispose）都必须在 GIL 保护下进行！
        /// 建议使用 WithGil() 包裹所有对返回对象的访问和释放。
        /// </summary>
        /// <returns>新创建的 Python 作用域（PyModule 对象）</returns>
        /// <example>
        /// var scope = PythonHost.CreateScope();
        /// PythonHost.WithGil(() => {
        ///     // 使用 scope
        ///     scope.DoSomething();
        ///     // 手动释放
        ///     scope.Dispose();
        /// });
        /// </example>
        public static PyModule CreateScope()
        {
            EnsureInitialized();
            using var gil = Py.GIL();
            var scope = Py.CreateScope();
            scope.Exec("import sys\nimport clr");
            return scope;
        }
        
        /// <summary>
        /// 在 GIL 保护下创建并使用 Python 作用域（推荐方式）。
        /// 作用域会在回调执行完毕后自动释放。
        /// </summary>
        /// <param name="action">在作用域中执行的操作</param>
        public static void WithScope(Action<PyModule> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            EnsureInitialized();
            using var gil = Py.GIL();
            using var scope = Py.CreateScope();
            scope.Exec("import sys\nimport clr");
            action(scope);
        }
        
        /// <summary>
        /// 在 GIL 保护下创建并使用 Python 作用域，并返回结果（推荐方式）。
        /// 作用域会在回调执行完毕后自动释放。
        /// </summary>
        /// <typeparam name="T">返回值类型</typeparam>
        /// <param name="func">在作用域中执行的操作</param>
        /// <returns>操作的返回值</returns>
        public static T WithScope<T>(Func<PyModule, T> func)
        {
            ArgumentNullException.ThrowIfNull(func);
            EnsureInitialized();
            using var gil = Py.GIL();
            using var scope = Py.CreateScope();
            scope.Exec("import sys\nimport clr");
            return func(scope);
        }

        public static void AddSysPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            EnsureInitialized();
            using var gil = Py.GIL();
            dynamic sys = Py.Import("sys");
            string normalized = Path.GetFullPath(path);

            foreach (PyObject item in sys.path)
            {
                if (string.Equals(item.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            sys.path.append(normalized);
        }

        public static void WithGil(Action action)
        {
            EnsureInitialized();
            using var gil = Py.GIL();
            action();
        }

        public static T WithGil<T>(Func<T> func)
        {
            EnsureInitialized();
            using var gil = Py.GIL();
            return func();
        }

        public static void Shutdown()
        {
            lock (InitLock)
            {
                if (!initialized)
                {
                    return;
                }

                try
                {
                    if (mainThreadState != IntPtr.Zero)
                    {
                        if (Environment.CurrentManagedThreadId == initializingThreadId)
                        {
                            PythonEngine.EndAllowThreads(mainThreadState);
                        }
                        else
                        {
                            // 非初始化线程调用 Shutdown 时跳过 EndAllowThreads，避免官方文档中描述的死锁
                        }
                        mainThreadState = IntPtr.Zero;
                    }

                    PythonEngine.Shutdown();
                }
                catch (NotSupportedException ex)
                {
                    if (!ex.Message.Contains("BinaryFormatter", StringComparison.Ordinal))
                    {
                        throw;
                    }
                    // 在部分受限环境中，pythonnet 的 Shutdown 依赖 BinaryFormatter，会导致异常；此处忽略让进程自然退出
                }
                initialized = false;
                initializingThreadId = -1;
            }
        }
    }
}
