using System.Reflection;
using Microsoft.CSharp.RuntimeBinder;
using Python.Runtime;

namespace PythonScript.Utils
{
    /// <summary>
    /// 属性名称辅助方法。
    /// </summary>
    public static class PropertyHelper
    {
        public static string NameOf(object? propertyObject)
        {
            if (propertyObject == null)
            {
                return string.Empty;
            }

            string? name = GetDynamicName(propertyObject);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            PropertyInfo? property = propertyObject.GetType().GetProperty("Name");
            if (property != null)
            {
                object? value = property.GetValue(propertyObject, null);
                name = value?.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }

            string fallback = propertyObject.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(fallback))
            {
                return fallback;
            }

            return propertyObject.GetType().Name;
        }

        public static string GetElementName(Array array, int index)
        {
            return $"{NameOf(array)}[{index}]";
        }

        private static string? GetDynamicName(object propertyObject)
        {
            if (propertyObject is PyObject pyObject)
            {
                return GetPythonObjectName(pyObject);
            }

            try
            {
                dynamic dynObj = propertyObject;
                string? name = dynObj.Name as string;
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
            catch (RuntimeBinderException)
            {
                // ignore dynamic binding errors
            }

            return null;
        }

        private static string GetPythonObjectName(PyObject pyObject)
        {
            if (pyObject == null)
                return "";

            try
            {
                using (Py.GIL())
                {
                    string? moduleName = null;
                    if (pyObject.HasAttr("__module__"))
                    {
                        var moduleObj = pyObject.GetAttr("__module__");
                        try
                        {
                            moduleName = moduleObj.As<string>();
                        }
                        finally
                        {
                            moduleObj.Dispose();
                        }
                    }

                    if (pyObject.HasAttr("__qualname__"))
                    {
                        var qualNameObj = pyObject.GetAttr("__qualname__");
                        try
                        {
                            string? qualName = qualNameObj.As<string>();
                            if (!string.IsNullOrEmpty(qualName))
                            {
                                return string.IsNullOrEmpty(moduleName)
                                    ? qualName
                                    : string.Concat(moduleName, ".", qualName);
                            }
                        }
                        finally
                        {
                            qualNameObj.Dispose();
                        }
                    }

                    if (pyObject.HasAttr("__name__"))
                    {
                        var nameObj = pyObject.GetAttr("__name__");
                        try
                        {
                            string? simpleName = nameObj.As<string>();
                            if (!string.IsNullOrEmpty(simpleName))
                            {
                                return string.IsNullOrEmpty(moduleName)
                                    ? simpleName
                                    : string.Concat(moduleName, ".", simpleName);
                            }
                        }
                        finally
                        {
                            nameObj.Dispose();
                        }
                    }

                    if (pyObject.HasAttr("__class__"))
                    {
                        var clsObj = pyObject.GetAttr("__class__");
                        try
                        {
                            if (clsObj.HasAttr("__name__"))
                            {
                                var clsNameObj = clsObj.GetAttr("__name__");
                                try
                                {
                                    string? className = clsNameObj.As<string>();
                                    if (!string.IsNullOrEmpty(className))
                                    {
                                        return className;
                                    }
                                }
                                finally
                                {
                                    clsNameObj.Dispose();
                                }
                            }
                        }
                        finally
                        {
                            clsObj.Dispose();
                        }
                    }

                    string? repr = pyObject.ToString();
                    if (!string.IsNullOrEmpty(repr))
                    {
                        return repr;
                    }

                    using var typeObj = pyObject.GetPythonType();
                    string? typeName = typeObj?.ToString();
                    return string.IsNullOrEmpty(typeName) ? "PythonObject" : typeName;
                }
            }
            catch (PythonException)
            {
                return pyObject.ToString()!;
            }
        }
    }
}

