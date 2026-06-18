using Python.Runtime;
using PythonScript.Runtime;
using System.Collections.Concurrent;
using System.Reflection;

namespace PythonScript.Analysis.Visitors
{
    internal sealed class PythonExpressionVisitor
    {
        private static readonly IReadOnlyDictionary<string, Type> BuiltinTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            { "True", typeof(bool) },
            { "False", typeof(bool) },
            { "None", typeof(void) },
            { "int", typeof(int) },
            { "float", typeof(double) },
            { "str", typeof(string) },
            { "bool", typeof(bool) },
            { "list", typeof(List<object>) },
            { "dict", typeof(Dictionary<object, object>) }
        };

        private readonly IDotnetTypeRegistry typeRegistry;
        private readonly IReadOnlyDictionary<string, Type> variables;
        private readonly ConcurrentDictionary<string, Type?> expressionCache;

        public PythonExpressionVisitor(IDotnetTypeRegistry typeRegistry, IReadOnlyDictionary<string, Type>? variables, ConcurrentDictionary<string, Type?>? cache = null)
        {
            this.typeRegistry = typeRegistry;
            this.variables = variables ?? new Dictionary<string, Type>();
            expressionCache = cache ?? new ConcurrentDictionary<string, Type?>();
        }

        public Type? Resolve(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return null;

            if (expressionCache.TryGetValue(expression, out var cached))
            {
                return cached;
            }

            return PythonHost.WithGil(() =>
            {
                dynamic ast = Py.Import("ast");
                dynamic parsed = ast.parse(expression, mode: "eval");
                using PyObject body = parsed.body;
                var result = ResolveNode(body);
                expressionCache[expression] = result;
                return result;
            });
        }

        private Type? ResolveNode(PyObject node)
        {
            string nodeType = GetNodeName(node);
            return nodeType switch
            {
                "Constant" => ResolveConstant(node),
                "Name" => ResolveName(node),
                "Attribute" => ResolveAttribute(node),
                "Subscript" => ResolveSubscript(node),
                "Call" => ResolveCall(node),
                "BinOp" => ResolveBinary(node),
                "BoolOp" => typeof(bool),
                "Compare" => typeof(bool),
                "UnaryOp" => ResolveUnary(node),
                "Tuple" => typeof(object[]),
                "List" => typeof(List<object>),
                "Dict" => typeof(Dictionary<object, object>),
                _ => typeof(object),
            };
        }

        private Type? ResolveConstant(PyObject node)
        {
            using PyObject valueObj = node.GetAttr("value");
            if (valueObj == null || valueObj.IsNone())
            {
                return null;
            }

            object? managed = valueObj.AsManagedObject(typeof(object));
            return managed?.GetType();
        }

        private Type? ResolveName(PyObject node)
        {
            using PyObject id = node.GetAttr("id");
            string name = id.As<string>();

            if (variables.TryGetValue(name, out var variableType))
            {
                return variableType;
            }

            if (BuiltinTypes.TryGetValue(name, out var builtin))
            {
                return builtin == typeof(void) ? null : builtin;
            }

            if (typeRegistry.TryResolve(name, out var resolved))
            {
                return typeof(Type).IsAssignableFrom(resolved) ? resolved : resolved;
            }

            return typeof(object);
        }

        private Type? ResolveAttribute(PyObject node)
        {
            using PyObject value = node.GetAttr("value");
            using PyObject attr = node.GetAttr("attr");
            string attributeName = attr.As<string>();

            bool isStatic;
            Type? targetType = ResolveAttributeTargetType(value, out isStatic);
            if (targetType == null)
            {
                return typeof(object);
            }

            var memberType = ResolveMemberType(targetType, attributeName, isStatic);
            return memberType ?? typeof(object);
        }

        private Type? ResolveCall(PyObject node)
        {
            using PyObject func = node.GetAttr("func");
            string funcNodeType = GetNodeName(func);
            using PyObject args = node.GetAttr("args");
            int argCount = Convert.ToInt32(args.Length());

            if (funcNodeType == "Attribute")
            {
                using PyObject value = func.GetAttr("value");
                using PyObject attr = func.GetAttr("attr");
                string methodName = attr.As<string>();

                bool isStatic;
                Type? targetType = ResolveAttributeTargetType(value, out isStatic);
                if (targetType == null)
                {
                    return typeof(object);
                }

                MethodInfo? method = ResolveMethod(targetType, methodName, argCount, isStatic);
                return method?.ReturnType ?? typeof(object);
            }

            if (funcNodeType == "Name")
            {
                using PyObject id = func.GetAttr("id");
                string name = id.As<string>();

                if (variables.TryGetValue(name, out var varType) && typeof(Delegate).IsAssignableFrom(varType))
                {
                    return varType.GetMethod("Invoke")?.ReturnType ?? typeof(object);
                }

                if (typeRegistry.TryResolve(name, out var resolvedType))
                {
                    return resolvedType;
                }
            }

            return typeof(object);
        }

        private Type? ResolveSubscript(PyObject node)
        {
            using PyObject value = node.GetAttr("value");
            using PyObject slice = node.GetAttr("slice");

            Type? targetType = ResolveNode(value);
            if (targetType == null)
            {
                return typeof(object);
            }

            if (targetType.IsArray)
            {
                return targetType.GetElementType();
            }

            if (ImplementsGenericInterface(targetType, typeof(IDictionary<,>), out var valueType, secondType: true))
            {
                return valueType ?? typeof(object);
            }

            if (ImplementsGenericInterface(targetType, typeof(IList<>), out var elementType))
            {
                return elementType ?? typeof(object);
            }

            if (ImplementsGenericInterface(targetType, typeof(IEnumerable<>), out elementType))
            {
                return elementType ?? typeof(object);
            }

            if (typeof(System.Collections.IList).IsAssignableFrom(targetType))
            {
                return typeof(object);
            }

            return typeof(object);
        }

        private Type? ResolveBinary(PyObject node)
        {
            using PyObject left = node.GetAttr("left");
            using PyObject right = node.GetAttr("right");

            Type? leftType = ResolveNode(left);
            Type? rightType = ResolveNode(right);

            if (leftType == rightType)
            {
                return leftType;
            }

            if (IsNumeric(leftType) && IsNumeric(rightType))
            {
                return (leftType == typeof(double) || rightType == typeof(double))
                    ? typeof(double)
                    : typeof(int);
            }

            return typeof(object);
        }

        private Type? ResolveUnary(PyObject node)
        {
            using PyObject operand = node.GetAttr("operand");
            return ResolveNode(operand);
        }

        private Type? ResolveAttributeTargetType(PyObject value, out bool isStatic)
        {
            string nodeType = GetNodeName(value);
            isStatic = false;

            if (nodeType == "Name")
            {
                string name = value.GetAttr("id").As<string>();

                if (variables.TryGetValue(name, out var varType))
                {
                    return varType;
                }

                if (typeRegistry.TryResolve(name, out var resolved))
                {
                    isStatic = true;
                    return resolved;
                }
            }

            return ResolveNode(value);
        }

        private Type? ResolveMemberType(Type targetType, string memberName, bool isStatic)
        {
            BindingFlags flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);

            var property = targetType.GetProperty(memberName, flags);
            if (property != null)
            {
                return property.PropertyType;
            }

            var field = targetType.GetField(memberName, flags);
            if (field != null)
            {
                return field.FieldType;
            }

            var method = targetType.GetMethod(memberName, flags);
            if (method != null)
            {
                return typeof(Delegate);
            }

            return null;
        }

        private MethodInfo? ResolveMethod(Type targetType, string methodName, int argCount, bool isStatic)
        {
            BindingFlags flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            var methods = targetType.GetMethods(flags)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));

            var filtered = methods.Where(m => m.GetParameters().Length == argCount).ToArray();
            if (filtered.Length == 1)
            {
                return filtered[0];
            }

            return methods.FirstOrDefault();
        }

        private static bool IsNumeric(Type? type)
        {
            if (type == null) return false;
            return type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(float) || type == typeof(decimal);
        }

        private static bool ImplementsGenericInterface(Type type, Type genericInterface, out Type? elementType, bool secondType = false)
        {
            foreach (var interfaceType in type.GetInterfaces())
            {
                if (interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == genericInterface)
                {
                    var args = interfaceType.GetGenericArguments();
                    elementType = secondType ? args.LastOrDefault() : args.FirstOrDefault();
                    return elementType != null;
                }
            }

            elementType = null;
            return false;
        }

        private static string GetNodeName(PyObject node)
        {
            using PyObject cls = node.GetAttr("__class__");
            using PyObject name = cls.GetAttr("__name__");
            return name.As<string>();
        }
    }
}
