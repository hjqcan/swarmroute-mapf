namespace PythonScript.SyntaxStaticCheck
{
    /// <summary>
    /// 表示 Python 脚本语法错误。
    /// </summary>
    public sealed class PythonSyntaxError
    {
        public PythonSyntaxError(string message, int line, int column, string errorCode)
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
    }
}

