using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PythonScript.Analysis
{
    /// <summary>
    /// 默认 .NET 类型注册中心实现，支持线程安全缓存。
    /// </summary>
    public sealed class DotnetTypeRegistry : IDotnetTypeRegistry
    {
        private readonly ConcurrentDictionary<string, Type> typeCache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Assembly> assemblyCache = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<Assembly> Assemblies => assemblyCache.Values;

        public void RegisterAssembly(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            string cacheKey = assembly.FullName ?? assembly.GetName().Name ?? string.Empty;
            assemblyCache.TryAdd(cacheKey, assembly);

            foreach (var type in assembly.GetExportedTypes())
            {
                RegisterType(type);
            }
        }

        public void RegisterType(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            typeCache[type.Name] = type;
            if (!string.IsNullOrEmpty(type.FullName))
            {
                typeCache[type.FullName] = type;
            }
        }

        public bool TryResolve(string typeName, out Type? type)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                type = null;
                return false;
            }

            if (typeCache.TryGetValue(typeName, out type))
            {
                return true;
            }

            foreach (var assembly in assemblyCache.Values)
            {
                type = assembly.GetType(typeName);
                if (type != null)
                {
                    RegisterType(type);
                    return true;
                }
            }

            type = null;
            return false;
        }
    }
}
