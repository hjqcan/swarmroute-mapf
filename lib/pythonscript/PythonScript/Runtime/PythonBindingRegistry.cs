using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Linq;
using System.Threading;
using Python.Runtime;
using PythonScript.Analysis;
using System.Threading;

namespace PythonScript.Runtime
{
    /// <summary>
    /// 负责导入程序集、模块、命名空间、变量和静态方法的注册中心。
    /// </summary>
    public sealed class PythonBindingRegistry
    {
        private readonly ConcurrentDictionary<string, Assembly> assemblies = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> modules = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> namespaces = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, object?> variables = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (Type type, string alias)> classes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (Type type, string method, string alias)> staticMethods = new(StringComparer.Ordinal);

        private readonly object resetLock = new();

        public PythonBindingRegistry(IPythonRuntimeSession session, IDotnetTypeRegistry? typeRegistry = null)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            TypeRegistry = typeRegistry ?? new DotnetTypeRegistry();
        }

        internal IReadOnlyDictionary<string, Assembly> Assemblies => new ReadOnlyDictionary<string, Assembly>(assemblies);
        internal IReadOnlyCollection<string> Modules => modules.Keys.ToArray();
        internal IReadOnlyCollection<string> Namespaces => namespaces.Keys.ToArray();
        internal IReadOnlyDictionary<string, (Type type, string alias)> Classes => new ReadOnlyDictionary<string, (Type type, string alias)>(classes);
        internal IReadOnlyCollection<(Type type, string method, string alias)> StaticMethods => staticMethods.Values.ToArray();

        public IPythonRuntimeSession Session { get; }

        public IDotnetTypeRegistry TypeRegistry { get; }

        public void ImportAssembly(Assembly assembly)
        {
            if (assembly == null || string.IsNullOrEmpty(assembly.FullName))
            {
                return;
            }

            if (!assemblies.TryAdd(assembly.FullName, assembly))
            {
                return;
            }

            TypeRegistry.RegisterAssembly(assembly);
            ImportAssemblyIntoPython(assembly);
        }

        public Assembly LoadAssembly(string assemblyPathOrName)
        {
            if (string.IsNullOrWhiteSpace(assemblyPathOrName))
            {
                throw new ArgumentNullException(nameof(assemblyPathOrName));
            }

            Assembly assembly = File.Exists(assemblyPathOrName)
                ? Assembly.LoadFrom(Path.GetFullPath(assemblyPathOrName))
                : Assembly.Load(assemblyPathOrName);

            ImportAssembly(assembly);
            return assembly;
        }

        public void ImportModule(string moduleName, PyModule? scope = null)
        {
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                throw new ArgumentNullException(nameof(moduleName));
            }

            if (!modules.TryAdd(moduleName, 0))
            {
                return;
            }

            Session.WithGil(() => EffectiveScope(scope).Exec($"import {moduleName}"));
        }

        public void ImportNamespace(string namespaceName, PyModule? scope = null)
        {
            if (string.IsNullOrWhiteSpace(namespaceName))
            {
                throw new ArgumentNullException(nameof(namespaceName));
            }

            namespaces.TryAdd(namespaceName, 0);
            Session.WithGil(() => EffectiveScope(scope).Exec($"from {namespaceName} import *"));
        }

        public void ImportNamespace(string namespaceName, Assembly assembly, PyModule? scope = null)
        {
            ImportAssembly(assembly);
            ImportNamespace(namespaceName, scope);
        }

        public void SetVariable(string name, object? value, PyModule? scope = null)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            Session.WithGil(() => EffectiveScope(scope).Set(name, value));
            variables[name] = value;
        }

        public bool TryGetVariable(string name, out object? value)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                value = null;
                return false;
            }

            try
            {
                using var py = Session.WithGil(() => Session.Scope.Get(name));
                value = py.AsManagedObject(typeof(object));
                return true;
            }
            catch
            {
                return variables.TryGetValue(name, out value);
            }
        }

        public bool RemoveVariable(string name, PyModule? scope = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            bool existed = variables.TryRemove(name, out _);
            Session.WithGil(() => EffectiveScope(scope).Exec($"globals().pop('{name}', None)"));
            return existed;
        }

        public void ImportClass(Type type, string alias = "", PyModule? scope = null)
        {
            ImportClassInternal(type, alias, record: true, scope);
        }

        public void ImportStaticMethod(Type type, string methodName, string alias = "", PyModule? scope = null)
        {
            ImportStaticMethodInternal(type, methodName, null, alias, record: true, scope);
        }

        /// <summary>
        /// 导入静态方法，支持指定参数类型以解决重载歧义
        /// </summary>
        /// <param name="type">包含静态方法的类型</param>
        /// <param name="methodName">方法名称</param>
        /// <param name="parameterTypes">参数类型数组，用于指定重载方法签名。null表示自动选择</param>
        /// <param name="alias">Python中的别名，为空则使用方法名</param>
        /// <param name="scope">目标作用域，为null则使用session默认scope</param>
        public void ImportStaticMethod(Type type, string methodName, Type[]? parameterTypes, string alias = "", PyModule? scope = null)
        {
            ImportStaticMethodInternal(type, methodName, parameterTypes, alias, record: true, scope);
        }

        public void RehydrateBindings(PyModule scope)
        {
            if (scope == null)
            {
                throw new ArgumentNullException(nameof(scope));
            }

            lock (resetLock)
            {
                foreach (var assembly in assemblies.Values)
                {
                    ImportAssemblyIntoPython(assembly);
                }

                foreach (var module in modules.Keys)
                {
                    Session.WithGil(() => scope.Exec($"import {module}"));
                }

                foreach (var ns in namespaces.Keys)
                {
                    Session.WithGil(() => scope.Exec($"from {ns} import *"));
                }

                foreach (var variable in variables)
                {
                    Session.WithGil(() => scope.Set(variable.Key, variable.Value));
                }

                foreach (var entry in classes.Values)
                {
                    ImportClassInternal(entry.type, entry.alias, record: false, scope);
                }

                foreach (var method in staticMethods.Values)
                {
                    ImportStaticMethodInternal(method.type, method.method, null, method.alias, record: false, scope);
                }
            }
        }

        private void ImportClassInternal(Type type, string alias, bool record, PyModule? scope)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            TypeRegistry.RegisterType(type);
            ImportAssembly(type.Assembly);

            string key = type.FullName ?? type.Name;
            if (record && classes.ContainsKey(key))
            {
                return;
            }

            string effectiveAlias = string.IsNullOrEmpty(alias) ? type.Name : alias;
            Session.WithGil(() =>
            {
                var targetScope = EffectiveScope(scope);
                
                // 检查是否为嵌套类型
                if (type.IsNested)
                {
                    // 嵌套类型：直接使用 ToPython() 并通过反射访问
                    using var pyType = type.ToPython();
                    targetScope.Set(effectiveAlias, pyType);
                }
                else
                {
                    // 非嵌套类型：尝试使用 Python import 语句让 pythonnet 自然暴露类型
                    string? ns = type.Namespace;
                    string typeName = type.Name;
                    
                    if (!string.IsNullOrEmpty(ns))
                    {
                        try
                        {
                            // from Namespace import TypeName as Alias
                            targetScope.Exec($"from {ns} import {typeName} as {effectiveAlias}");
                        }
                        catch
                        {
                            // 如果 import 失败（比如私有类型），回退到 ToPython()
                            using var pyType = type.ToPython();
                            targetScope.Set(effectiveAlias, pyType);
                        }
                    }
                    else
                    {
                        // 无命名空间，直接使用 type.ToPython()
                        using var pyType = type.ToPython();
                        targetScope.Set(effectiveAlias, pyType);
                    }
                }
            });

            classes[key] = (type, effectiveAlias);
        }

        private void ImportStaticMethodInternal(Type type, string methodName, Type[]? parameterTypes, string alias, bool record, PyModule? scope)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            if (string.IsNullOrWhiteSpace(methodName))
            {
                throw new ArgumentNullException(nameof(methodName));
            }

            // 验证方法存在并解决重载歧义
            MethodInfo? methodInfo = null;
            
            if (parameterTypes != null)
            {
                // 使用指定的参数类型查找方法
                methodInfo = type.GetMethod(methodName, 
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy,
                    null, 
                    parameterTypes, 
                    null);
                
                if (methodInfo == null)
                {
                    throw new MissingMethodException(type.FullName, 
                        $"{methodName}({string.Join(", ", parameterTypes.Select(t => t.Name))})");
                }
            }
            else
            {
                // 自动查找：获取所有同名的静态方法
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                    .Where(m => m.Name == methodName)
                    .ToArray();
                
                if (methods.Length == 0)
                {
                    throw new MissingMethodException(type.FullName, methodName);
                }
                else if (methods.Length == 1)
                {
                    // 只有一个方法，直接使用
                    methodInfo = methods[0];
                }
                else
                {
                    // 有多个重载，抛出更友好的异常
                    var signatures = string.Join(", ", methods.Select(m => 
                        $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})"));
                    throw new AmbiguousMatchException(
                        $"方法 '{methodName}' 有 {methods.Length} 个重载。请使用带参数类型的重载版本指定具体方法签名。可用签名: {signatures}");
                }
            }

            // 确保类型已导入
            ImportClassInternal(type, string.Empty, record: false, scope);

            string key = type.FullName ?? type.Name;
            if (!classes.TryGetValue(key, out var info))
            {
                throw new InvalidOperationException($"无法找到类型 {type.FullName} 的别名记录。");
            }

            string pythonName = string.IsNullOrEmpty(alias) ? methodName : alias;
            string classAlias = info.alias;

            // 使用 pythonnet 的自然绑定机制获取静态方法
            Session.WithGil(() =>
            {
                var targetScope = EffectiveScope(scope);
                
                // 尝试通过已导入的类型对象获取静态方法
                using var classObj = targetScope.Get(classAlias);
                
                // 检查是否有对应的静态方法
                if (classObj.HasAttr(methodName))
                {
                    // 成功：pythonnet 已经正确暴露了静态方法
                    using var methodObj = classObj.GetAttr(methodName);
                    targetScope.Set(pythonName, methodObj);
                }
                else
                {
                    // 回退：创建一个 C# 包装委托，处理参数转换
                    // 这适用于嵌套类型或私有类型，pythonnet 无法直接暴露静态方法
                    var parameters = methodInfo.GetParameters();
                    int paramCount = parameters.Length;
                    
                    // 创建一个 C# 委托，在调用时进行参数转换
                    Func<PyObject[], object?> wrapper = (PyObject[] pyArgs) =>
                    {
                        if (pyArgs.Length != paramCount)
                        {
                            throw new ArgumentException($"Expected {paramCount} arguments, got {pyArgs.Length}");
                        }
                        
                        var convertedArgs = new object?[paramCount];
                        for (int i = 0; i < paramCount; i++)
                        {
                            convertedArgs[i] = pyArgs[i].AsManagedObject(parameters[i].ParameterType);
                        }

                        return methodInfo.Invoke(null, convertedArgs);
                    };
                    
                    // 将包装器注入到 Python scope
                    string wrapperVarName = $"__cs_wrapper_{pythonName}_{Guid.NewGuid():N}";
                    using var pyWrapper = wrapper.ToPython();
                    targetScope.Set(wrapperVarName, pyWrapper);
                    
                    // 生成 Python 包装函数
                    var code = new StringBuilder();
                    code.Append("def ").Append(pythonName).Append("(");
                    for (int i = 0; i < paramCount; i++)
                    {
                        if (i > 0) code.Append(", ");
                        code.Append("arg").Append(i);
                    }
                    code.AppendLine("):");
                    code.Append("    args = [");
                    for (int i = 0; i < paramCount; i++)
                    {
                        if (i > 0) code.Append(", ");
                        code.Append("arg").Append(i);
                    }
                    code.AppendLine("]");
                    code.Append("    return ").Append(wrapperVarName).AppendLine("(args)");
                    
                    targetScope.Exec(code.ToString());
                }
            });

            if (record)
            {
                staticMethods[key + "::" + methodName] = (type, methodName, alias);
            }
        }

        private void ImportAssemblyIntoPython(Assembly assembly)
        {
            Session.WithGil(() =>
            {
                dynamic clr = Py.Import("clr");
                string? location = assembly.Location;

                bool loaded = false;

                if (!string.IsNullOrEmpty(location) && File.Exists(location))
                {
                    // pythonnet 2.x 提供 AddReferenceToFileAndPath，但在 3.x 已被移除
                    if (clr.HasAttr("AddReferenceToFileAndPath"))
                    {
                        try
                        {
                            clr.AddReferenceToFileAndPath(location);
                            loaded = true;
                        }
                        catch
                        {
                            // fall through to AddReference
                        }
                    }
                }

                if (!loaded)
                {
                    clr.AddReference(assembly.GetName().Name);
                }
            });
        }

        private PyModule EffectiveScope(PyModule? scope) => scope ?? Session.Scope;
    }
}
