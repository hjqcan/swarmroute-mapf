using System;

namespace PythonScript.Diagnostics.Debugging
{
    /// <summary>
    /// 自定义调试视图接口，允许对象提供专属的 XRay 输出。
    /// </summary>
    internal interface IPythonDebuggable
    {
        /// <summary>
        /// 返回给调试器的对象描述。
        /// </summary>
        /// <param name="name">对象名称。</param>
        /// <returns>格式化字符串。</returns>
        string XRay(string name);
    }
}

