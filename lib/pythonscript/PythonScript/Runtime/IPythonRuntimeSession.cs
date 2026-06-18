using Python.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PythonScript.Runtime
{
    /// <summary>
    /// 封装 pythonnet GIL、作用域与初始化流程的运行时会话接口。
    /// </summary>
    public interface IPythonRuntimeSession : IDisposable
    {
        /// <summary>
        /// 当前会话绑定的 Python 作用域。
        /// </summary>
        PyModule Scope { get; }

        /// <summary>
        /// 是否已完成运行时初始化。
        /// </summary>
        bool IsInitialized { get; }

        /// <summary>
        /// 在持有 GIL 的情况下执行托管委托。
        /// </summary>
        void WithGil(Action action);

        /// <summary>
        /// 在持有 GIL 的情况下执行托管委托并返回结果。
        /// </summary>
        T WithGil<T>(Func<T> action);

        /// <summary>
        /// 创建共享配置的子作用域。
        /// </summary>
        PyModule CreateChildScope();

        /// <summary>
        /// 重置当前作用域（并保留已导入的程序集/模块信息）。
        /// </summary>
        void ResetScope();
    }
}
