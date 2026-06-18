namespace PythonScript.Runtime
{
    /// <summary>
    /// 表示脚本文件（仅包含路径信息）。
    /// </summary>
    public sealed class PythonScriptFile
    {
        public PythonScriptFile(string source)
        {
            Source = source;
        }

        /// <summary>
        /// 脚本路径或源信息。
        /// </summary>
        public string Source { get; }
    }
}

