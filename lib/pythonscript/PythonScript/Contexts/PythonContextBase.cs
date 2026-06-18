using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Python.Runtime;
using PythonScript.Diagnostics.Logging;
using PythonScript.Runtime;

namespace PythonScript.Contexts
{
    /// <summary>
    /// 基于 pythonnet 的脚本上下文基类，负责管理作用域、程序集、模块、命名空间、变量、类与静态方法导入。
    /// </summary>
    public abstract class PythonContextBase : IDisposable
    {
        protected PythonContextBase(IPythonRuntimeSession? session = null, PythonBindingRegistry? registry = null)
        {
            runtimeSession = session ?? new PythonRuntimeSession();
            bindingRegistry = registry ?? new PythonBindingRegistry(runtimeSession);

            ownsSession = session is null;
            ownsRegistry = registry is null;

            InitScope();
        }

        private readonly IPythonRuntimeSession runtimeSession;
        private readonly PythonBindingRegistry bindingRegistry;
        private readonly bool ownsSession;
        private readonly bool ownsRegistry;

        private bool disposed;

        /// <summary>
        /// 当前上下文对应的 Python 作用域。
        /// </summary>
        protected PyModule Scope => runtimeSession.Scope;

        protected IPythonRuntimeSession RuntimeSession => runtimeSession;

        protected PythonBindingRegistry BindingRegistry => bindingRegistry;

        #region 查询属性

        public IReadOnlyDictionary<string, Assembly> Assemblies => bindingRegistry.Assemblies;
        public IReadOnlyCollection<string> Modules => bindingRegistry.Modules;
        public IReadOnlyCollection<string> Namespaces => bindingRegistry.Namespaces;
        public IReadOnlyDictionary<string, (Type type, string alias)> Classes => bindingRegistry.Classes;
        public IReadOnlyCollection<(Type type, string method, string alias)> StaticMethods => bindingRegistry.StaticMethods;

        #endregion

        #region 生命周期

        protected virtual void InitScope()
        {
            runtimeSession.WithGil(() =>
            {
                var debug = new Diagnostics.PythonDebugHelper();
                Scope.Set("debug", debug);

                var log = new Diagnostics.PythonScriptLogBridge();
                Scope.Set("log", log);

                dynamic builtins = Py.Import("builtins");
                builtins.debug = debug;
                builtins.log = log;

                Scope.Set("name_of", (Func<object?, string>)Utils.PropertyHelper.NameOf);
                builtins.name_of = (Func<object?, string>)Utils.PropertyHelper.NameOf;
            });
        }

        protected virtual void OnBeforeReset() { }

        protected virtual void OnAfterReset() { }

        protected virtual void OnDisposing() { }

        public virtual void Reset()
        {
            EnsureNotDisposed();

            OnBeforeReset();

            runtimeSession.ResetScope();

            InitScope();
            bindingRegistry.RehydrateBindings(Scope);

            OnAfterReset();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            OnDisposing();

            if (ownsSession)
            {
                runtimeSession.Dispose();
            }
        }

        protected void EnsureNotDisposed()
        {
            if (disposed) throw new ObjectDisposedException(GetType().FullName);
        }

        #endregion

        #region 程序集与命名空间

        public void LoadAssembly(Assembly assembly)
        {
            EnsureNotDisposed();
            bindingRegistry.ImportAssembly(assembly);
        }

        public Assembly LoadAssembly(string assemblyPathOrName)
        {
            EnsureNotDisposed();
            return bindingRegistry.LoadAssembly(assemblyPathOrName);
        }

        public void ImportNamespace(string namespaceName)
        {
            EnsureNotDisposed();
            bindingRegistry.ImportNamespace(namespaceName, Scope);
        }

        public void ImportNamespace(string namespaceName, Assembly assembly)
        {
            EnsureNotDisposed();
            bindingRegistry.ImportNamespace(namespaceName, assembly, Scope);
        }

        #endregion

        #region 模块管理

        public void ImportModule(string moduleName)
        {
            EnsureNotDisposed();
            bindingRegistry.ImportModule(moduleName, Scope);
        }

        #endregion

        #region 变量管理

        public void SetVariable(string name, object? value)
        {
            EnsureNotDisposed();
            bindingRegistry.SetVariable(name, value, Scope);
        }

        public bool TryGetVariable(string name, out object? value)
        {
            EnsureNotDisposed();
            return bindingRegistry.TryGetVariable(name, out value);
        }

        public bool RemoveVariable(string name)
        {
            EnsureNotDisposed();
            return bindingRegistry.RemoveVariable(name, Scope);
        }

        #endregion

        #region 类与静态方法

        public void ImportClass(Type type, string alias = "") => bindingRegistry.ImportClass(type, alias, Scope);

        public void ImportStaticMethod(Type type, string methodName, string alias = "")
            => bindingRegistry.ImportStaticMethod(type, methodName, alias, Scope);

        #endregion
    }
}
