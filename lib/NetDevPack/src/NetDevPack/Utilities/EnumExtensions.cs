using System;
using System.ComponentModel;
using System.Reflection;

namespace NetDevPack.Utilities
{
    public static class EnumExtensions
    {
        /// <summary>
        /// 获取枚举项的 <see cref="DescriptionAttribute"/> 描述文本（若不存在则返回枚举名称）
        /// 用法示例：myEnumValue.GetDescription()
        /// </summary>
        public static string GetDescription(this Enum value)
        {
            if (value == null) return string.Empty;

            var type = value.GetType();
            var name = Enum.GetName(type, value);
            if (name == null) return value.ToString();

            var field = type.GetField(name);
            if (field == null) return name;

            var attr = field.GetCustomAttribute<DescriptionAttribute>(inherit: false);
            return attr?.Description ?? name;
        }

        /// <summary>
        /// 泛型版本的 GetDescription（便于在泛型上下文中使用）
        /// </summary>
        public static string GetDescription<TEnum>(this TEnum value) where TEnum : Enum
            => GetDescription((Enum)(object)value);
    }
}
