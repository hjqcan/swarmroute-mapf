using System.Collections.ObjectModel;
using System.Text;

namespace PythonScript.SyntaxStaticCheck
{
    /// <summary>
    /// Python 脚本编译结果。
    /// </summary>
    public sealed class PythonScriptCompilationResult
    {
        private readonly List<PythonScriptCompilationError> errors = new();

        /// <summary>
        /// 是否存在编译错误。
        /// </summary>
        public bool HasError => errors.Count > 0;

        /// <summary>
        /// 错误集合（只读）。
        /// </summary>
        public ReadOnlyCollection<PythonScriptCompilationError> Errors => errors.AsReadOnly();

        /// <summary>
        /// 添加错误信息。
        /// </summary>
        public void AddError(string message, int line = 0, int column = 0, string errorCode = "SYNTAX")
        {
            errors.Add(new PythonScriptCompilationError(message, line, column, errorCode));
        }

        /// <summary>
        /// 合并错误集合。
        /// </summary>
        public void AddErrors(IEnumerable<PythonScriptCompilationError> compileErrors)
        {
            if (compileErrors == null) return;
            errors.AddRange(compileErrors);
        }

        /// <summary>
        /// 合并语法检查错误集合。
        /// </summary>
        public void AddErrors(IEnumerable<PythonSyntaxError> syntaxErrors)
        {
            if (syntaxErrors == null) return;

            foreach (var error in syntaxErrors)
            {
                if (error == null) continue;
                AddError(error.Message, error.Line, error.Column, error.ErrorCode);
            }
        }

        public override string ToString()
        {
            if (errors.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            foreach (var error in errors)
            {
                sb.AppendLine(error.ToString());
            }
            return sb.ToString();
        }
    }
}

