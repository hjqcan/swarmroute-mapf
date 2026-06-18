using PythonScript.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PythonScript.Exceptions
{
    /// <summary>
    /// 脚本编译错误异常。
    /// </summary>
    public class PythonScriptCompilationException : Exception
    {
        public PythonScriptCompilationException(PythonScriptFile? script, string message)
            : base(message)
        {
            Script = script;
        }

        public PythonScriptFile? Script { get; }
    }
}
