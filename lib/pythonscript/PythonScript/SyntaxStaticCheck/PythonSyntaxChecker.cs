using Python.Runtime;
using PythonScript.Exceptions;
using PythonScript.Runtime;

namespace PythonScript.SyntaxStaticCheck
{
    /// <summary>
    /// 基于 CPython ast 的脚本静态检查。
    /// </summary>
    public sealed class PythonSyntaxChecker
    {
        public PythonSyntaxChecker()
        {
            PythonHost.EnsureInitialized();
        }

        public IReadOnlyList<PythonSyntaxError> AnalyzeSyntaxErrors(string code, string filename = "<unknown>")
        {
            var result = new List<PythonSyntaxError>();
            if (string.IsNullOrWhiteSpace(code))
            {
                return result;
            }

            return PythonHost.WithGil(() =>
            {
                try
                {
                    using dynamic ast = Py.Import("ast");
                    ast.parse(code, filename);
                    return result;
                }
                catch (PythonException ex)
                {
                    var formatted = PythonExceptionFormatter.Format(ex);
                    int line = 0;
                    int column = 0;

                    try
                    {
                        using PyObject? value = ex.Value;
                        if (value != null)
                        {
                            if (value.HasAttr("lineno"))
                            {
                                using PyObject lineno = value.GetAttr("lineno");
                                line = Convert.ToInt32(lineno.As<long>());
                            }
                            if (value.HasAttr("offset"))
                            {
                                using PyObject offset = value.GetAttr("offset");
                                column = Convert.ToInt32(offset.As<long>());
                            }
                        }
                    }
                    catch { }

                    result.Add(new PythonSyntaxError(formatted, line, column, "SYNTAX"));
                    return result;
                }
            });
        }
    }
}

