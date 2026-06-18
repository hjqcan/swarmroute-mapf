using Python.Runtime;
using PythonScript.Runtime;

namespace PythonScript.Exceptions
{
    internal static class PythonExceptionFormatter
    {
        public static string Format(PythonException ex)
        {
            if (ex == null) return string.Empty;

            return PythonHost.WithGil(() =>
            {
                try
                {
                    var tb = ex.Traceback;
                    if (tb != null)
                    {
                        using (tb)
                        {
                            using dynamic tbModule = Py.Import("traceback");
                            using PyObject pyList = tbModule.format_exception(ex.Type, ex.Value, tb);
                            var lines = pyList.As<IEnumerable<string>>() ?? Enumerable.Empty<string>();
                            return string.Join(Environment.NewLine, lines);
                        }
                    }
                }
                catch
                {
                    // ignore formatter failure
                }

                return ex.Message;
            });
        }
    }
}

