using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PythonScript.SyntaxStaticCheck
{
    /// <summary>
    /// 编译错误信息。
    /// </summary>
    public sealed class PythonScriptCompilationError
    {
        public PythonScriptCompilationError(string message, int line, int column, string errorCode)
        {
            Message = message;
            Line = line;
            Column = column;
            ErrorCode = errorCode;
        }

        public string Message { get; }

        public int Line { get; }

        public int Column { get; }

        public string ErrorCode { get; }

        public override string ToString()
        {
            return $"[ErrorCode: {ErrorCode}] Line {Line}, Col {Column}: {Message}";
        }
    }
}
