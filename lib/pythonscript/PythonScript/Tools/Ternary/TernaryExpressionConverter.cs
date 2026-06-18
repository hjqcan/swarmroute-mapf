using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PythonScript
{
    /// <summary>
    /// 将 C# 三元表达式转换为 Python 表达式。
    /// </summary>
    public static class TernaryExpressionConverter
    {
        public static string ConvertCode(string inputCode)
        {
            string normalized = NormalizeStrings(inputCode);

            SyntaxTree tree = CSharpSyntaxTree.ParseText(normalized);
            var rewriter = new TernaryExpressionRewriter();
            SyntaxNode newRoot = rewriter.Visit(tree.GetRoot());

            return newRoot.ToFullString();
        }

        public static void ConvertFile(string inputFilePath, string outputFilePath)
        {
            string inputCode = File.ReadAllText(inputFilePath);
            string outputCode = ConvertCode(inputCode);
            File.WriteAllText(outputFilePath, outputCode);
        }

        private static string NormalizeStrings(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return code;
            }

            if ((code.StartsWith("\"") && code.EndsWith("\"") && code.Count(c => c == '"') >= 2) ||
                (code.StartsWith("'") && code.EndsWith("'") && code.Count(c => c == '\'') >= 2))
            {
                string content = code.Substring(1, code.Length - 2);
                return $"'''{content}'''";
            }

            const string pattern = @"(?<!\\)(([""'])(?:\\.|(?!\2).)*?\2)";
            return Regex.Replace(code, pattern, match =>
            {
                string matched = match.Value;
                string content = matched.Substring(1, matched.Length - 2);

                if (content.Contains('\r') || content.Contains('\n'))
                {
                    return $"'''{content}'''";
                }

                return matched;
            });
        }
    }
}

