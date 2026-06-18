using System;
using System.Reflection;

namespace PythonScript.Analysis
{
    /// <summary>
    /// 抽象 .NET 类型注册中心，用于表达式分析与运行时绑定复用。
    /// </summary>
    public interface IDotnetTypeRegistry
    {
        /// <summary>
        /// 注册程序集并缓存可导出类型。
        /// </summary>
        void RegisterAssembly(Assembly assembly);

        /// <summary>
        /// 注册单个类型，供名称解析使用。
        /// </summary>
        void RegisterType(Type type);

        /// <summary>
        /// 按名称解析类型。
        /// </summary>
        bool TryResolve(string name, out Type? type);
    }
}
