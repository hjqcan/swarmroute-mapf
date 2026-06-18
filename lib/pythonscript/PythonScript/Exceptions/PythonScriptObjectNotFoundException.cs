using PythonScript.Contexts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PythonScript.Exceptions
{
    /// <summary>
    /// 脚本对象未找到异常。
    /// </summary>
    public class PythonScriptObjectNotFoundException : Exception
    {
        public PythonScriptObjectNotFoundException(PythonContextBase context, string name)
            : base($"Object '{name}' not found in context '{context?.GetType().FullName}'.")
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(name);

            Context = context;
            ObjectName = name;
        }

        public PythonContextBase Context { get; }

        public string ObjectName { get; }
    }
}
