using Python.Runtime;
using PythonScript.Analysis.Visitors;
using PythonScript.Runtime;
using System.Collections.Concurrent;
using System.Reflection;

namespace PythonScript.Analysis
{
    /// <summary>
    /// 基于 CPython AST 的类型分析器，负责常量判定、成员推断与赋值检测。
    /// </summary>
    public sealed class PythonAstAnalyzer
    {
        private readonly IDotnetTypeRegistry typeRegistry;
        private readonly Dictionary<string, Type> globalVariables = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Type?> expressionCache = new(StringComparer.Ordinal);

        public PythonAstAnalyzer()
        {
            typeRegistry = new DotnetTypeRegistry();
            PythonHost.EnsureInitialized();
        }

        public PythonAstAnalyzer(IDotnetTypeRegistry registry)
        {
            typeRegistry = registry ?? throw new ArgumentNullException(nameof(registry));
            PythonHost.EnsureInitialized();
        }

        public bool IsConstantExpression(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return false;
            }

            return PythonHost.WithGil(() =>
            {
                using dynamic ast = Py.Import("ast");
                using dynamic parsed = ast.parse(expression, mode: "eval");
                return IsConstantNode(parsed.body);
            });
        }

        public string? GetStringConstantValue(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return null;
            }

            return PythonHost.WithGil(() =>
            {
                using dynamic ast = Py.Import("ast");
                using dynamic parsed = ast.parse(expression, mode: "eval");
                if (IsConstantNode(parsed.body))
                {
                    using PyObject value = ast.literal_eval(parsed);
                    return value.IsNone() ? null : value.As<string>();
                }

                return null;
            });
        }

        public bool IsPropertyAccess(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return false;
            }

            return PythonHost.WithGil(() =>
            {
                using dynamic ast = Py.Import("ast");
                using dynamic parsed = ast.parse(expression, mode: "eval");
                string nodeType = GetNodeName(parsed.body);
                return nodeType == "Attribute" || nodeType == "Name";
            });
        }

        public bool CanBeAssigned(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return false;
            }

            return PythonHost.WithGil(() =>
            {
                dynamic ast = Py.Import("ast");
                dynamic parsed = ast.parse(expression, mode: "eval");
                string nodeType = GetNodeName(parsed.body);
                return nodeType == "Name" || nodeType == "Attribute" || nodeType == "Subscript";
            });
        }

        public bool IsSingleAssignmentExpression(string source, out string? leftExpression)
        {
            leftExpression = null;
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            bool result = false;
            string? localLeft = null;

            PythonHost.WithGil(() =>
            {
                using dynamic ast = Py.Import("ast");
                using dynamic parsed = ast.parse(source, mode: "exec");

                using PyObject body = parsed.body;
                if (body.Length() != 1)
                {
                    return;
                }

                using PyObject stmt = body[0];
                string stmtType = GetNodeName(stmt);

                if (stmtType == "Assign")
                {
                    using PyObject targets = stmt.GetAttr("targets");
                    if (targets.Length() == 1)
                    {
                        using PyObject target = targets[0];
                        string? expr = Unparse(target);
                        localLeft = expr;
                        result = !string.IsNullOrEmpty(expr);
                    }
                }
                else if (stmtType == "AugAssign")
                {
                    using PyObject target = stmt.GetAttr("target");
                    string? expr = Unparse(target);
                    localLeft = expr;
                    result = !string.IsNullOrEmpty(expr);
                }
            });

            leftExpression = localLeft;
            return result;
        }

        public void LoadAssembly(Assembly assembly)
        {
            if (assembly == null)
            {
                return;
            }

            typeRegistry.RegisterAssembly(assembly);
        }

        public void RegisterType(Type type)
        {
            if (type == null)
            {
                return;
            }

            typeRegistry.RegisterType(type);
        }

        public void SetVariableType(string name, Type type)
        {
            if (string.IsNullOrWhiteSpace(name) || type == null)
            {
                return;
            }

            globalVariables[name] = type;
        }

        public void RemoveVariable(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            globalVariables.Remove(name);
        }

        public void ClearVariables() => globalVariables.Clear();

        public Type? ResolveExpressionType(string expression, IReadOnlyDictionary<string, Type>? localVariables = null)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return null;
            }

            IReadOnlyDictionary<string, Type> scope = MergeVariables(localVariables);
            var visitor = new PythonExpressionVisitor(typeRegistry, scope, expressionCache);
            return visitor.Resolve(expression);
        }

        private IReadOnlyDictionary<string, Type> MergeVariables(IReadOnlyDictionary<string, Type>? localVariables)
        {
            if (localVariables == null || localVariables.Count == 0)
            {
                return globalVariables;
            }

            var merged = new Dictionary<string, Type>(globalVariables, StringComparer.Ordinal);
            foreach (var kv in localVariables)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
                {
                    merged[kv.Key] = kv.Value;
                }
            }

            return merged;
        }

        private static bool IsConstantNode(PyObject node)
        {
            string nodeType = GetNodeName(node);
            switch (nodeType)
            {
                case "Constant":
                    return true;
                case "Tuple":
                case "List":
                    using (PyObject elements = node.GetAttr("elts"))
                    {
                        foreach (PyObject item in elements.As<PyObject[]>() ?? Array.Empty<PyObject>())
                        {
                            if (!IsConstantNode(item))
                            {
                                return false;
                            }
                        }
                    }
                    return true;
                case "BinOp":
                    using (PyObject left = node.GetAttr("left"))
                    using (PyObject right = node.GetAttr("right"))
                    {
                        return IsConstantNode(left) && IsConstantNode(right);
                    }
                case "UnaryOp":
                    using (PyObject operand = node.GetAttr("operand"))
                    {
                        return IsConstantNode(operand);
                    }
                default:
                    return false;
            }
        }

        private static string GetNodeName(PyObject node)
        {
            using PyObject cls = node.GetAttr("__class__");
            using PyObject name = cls.GetAttr("__name__");
            return name.As<string>();
        }

        private static string? Unparse(PyObject node)
        {
            using dynamic ast = Py.Import("ast");
            using PyObject unparseFunc = ast.unparse;
            using PyObject result = unparseFunc.Invoke(node);
            return result.IsNone() ? null : result.As<string>();
        }
    }
}
