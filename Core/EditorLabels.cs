using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.Prefabs;
using Game.UI.Widgets;
using UnityEngine;

namespace AITranslate.Core
{
    /// <summary>
    /// 资产编辑器检查器里的文字。编辑器用反射把组件的字段拼成界面，标签有两种来源：
    ///   字段名：词典键 Editor.FIELD[类型全名.成员名]，没有译文时显示“把成员名拆开的英文”（官方只写了 57 个字段的译文）；
    ///   组件标题、“添加组件”列表里的类型名：游戏代码里直接写死的英文，不查词典。
    /// 这里按编辑器同样的规则枚举所有会显示的字段和组件类型，生成可翻译的词典键：
    ///   Editor.FIELD[类型全名.成员名] → 英文标签（和游戏的键一致，译文直接生效）；
    ///   Editor.TYPE[类型名] → 英文标题（本 mod 自己的键，由 <see cref="EditorPatches"/> 在显示标题时查询）。
    /// 只读类型元数据，可以在任何线程运行。
    /// </summary>
    internal static class EditorLabels
    {
        public const string kFieldPrefix = "Editor.FIELD[";
        public const string kTypePrefix = "Editor.TYPE[";

        private static readonly Regex s_Words = new Regex(@"\B([A-Z][a-z])", RegexOptions.Compiled);
        private static readonly Regex s_Camel = new Regex(@"([^A-Z\s])([A-Z])", RegexOptions.Compiled);
        private static readonly Regex s_Digits = new Regex(@"(?<![\d\s]|\dx|\d-)(\d)", RegexOptions.Compiled);

        /// <summary>和游戏的 WidgetReflectionUtils.NicifyVariableName 完全一致：英文标签必须与游戏没有译文时显示的一样。</summary>
        public static string Nicify(string name)
        {
            if (name == null)
            {
                return string.Empty;
            }
            if (name.StartsWith("m_", StringComparison.Ordinal))
            {
                name = name.Substring(2);
            }
            else if (name.StartsWith("Get", StringComparison.Ordinal))
            {
                name = name.Substring(3);
            }
            name = s_Words.Replace(name, " $1");
            name = s_Camel.Replace(name, "$1 $2");
            name = s_Digits.Replace(name, " $1");
            return Regex.Replace(name, "^([a-z])", m => m.Value.ToUpperInvariant());
        }

        // 当前已知的组件类型名（最近一次检测的结果）。标题补丁每次生成标签都会调用，先用它快速排除绝大多数调用。
        private static volatile HashSet<string> s_KnownTypeNames = new HashSet<string>(StringComparer.Ordinal);

        public static void SetKnownTypeNames(HashSet<string> names) => s_KnownTypeNames = names ?? new HashSet<string>(StringComparer.Ordinal);

        /// <summary>标题补丁调用：raw 是类型名；有译文就把 text 换成译文。任何异常都吞掉，不能影响编辑器界面。</summary>
        public static void TranslateTitle(string raw, ref string text)
        {
            try
            {
                if (string.IsNullOrEmpty(raw) || !s_KnownTypeNames.Contains(raw))
                {
                    return;
                }
                if (Mod.Instance?.Service != null && Mod.Instance.Service.TryGetLiveText(TypeKey(raw), out string translated)
                    && !string.IsNullOrWhiteSpace(translated))
                {
                    text = translated;
                }
            }
            catch (Exception)
            {
                // 保持原文。
            }
        }

        public static string FieldKey(Type declaring, string member) => kFieldPrefix + TypeName(declaring) + "." + member + "]";

        public static string TypeKey(string typeName) => kTypePrefix + typeName + "]";

        /// <summary>Colossal 的 TypeName(includeNamespace: true)：嵌套类型写成 外层.内层。</summary>
        private static string TypeName(Type type)
        {
            if (type.IsNested)
            {
                return TypeName(type.DeclaringType) + "." + type.Name;
            }
            return string.IsNullOrEmpty(type.Namespace) ? type.Name : type.Namespace + "." + type.Name;
        }

        /// <summary>结果：词典键 → 英文标签；还有所有组件类型名（供标题补丁快速判断）。</summary>
        internal sealed class Result
        {
            public readonly Dictionary<string, string> Entries = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly HashSet<string> TypeNames = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>元数据读不出来、被跳过的类型数（诊断用）。</summary>
            public int Skipped;
        }

        /// <summary>
        /// 枚举给定程序集里所有组件和预制件类型的字段，以及字段里嵌套的可序列化结构。
        /// 过滤条件与游戏 EditorGenerator 的成员过滤一致。
        /// </summary>
        private static readonly object s_CacheLock = new object();
        private static string s_CacheKey;
        private static Result s_Cache;

        /// <summary>同一批程序集只枚举一次（类型元数据在游戏运行期间不变；装了新 mod、程序集变了才重新枚举）。</summary>
        public static Result CollectCached(IEnumerable<Assembly> assemblies)
        {
            var list = assemblies.Where(a => a != null).Distinct().ToList();
            string key = string.Join("|", list.Select(a => a.FullName).OrderBy(n => n, StringComparer.Ordinal));
            lock (s_CacheLock)
            {
                if (s_Cache != null && s_CacheKey == key)
                {
                    return s_Cache;
                }
            }
            Result result = Collect(list);
            lock (s_CacheLock)
            {
                s_CacheKey = key;
                s_Cache = result;
            }
            return result;
        }

        public static Result Collect(IEnumerable<Assembly> assemblies)
        {
            var result = new Result();
            var gameAssemblies = new HashSet<Assembly>(assemblies.Where(a => a != null));
            var visited = new HashSet<Type>();
            foreach (Assembly assembly in gameAssemblies)
            {
                foreach (Type type in SafeTypes(assembly))
                {
                    // 个别类型的元数据读不出来（TypeLoadException 等）：跳过它，不能让整个枚举中断。
                    try
                    {
                        if (!IsRoot(type))
                        {
                            continue;
                        }
                        if (!type.IsAbstract && typeof(ComponentBase).IsAssignableFrom(type))
                        {
                            // 组件标题和“添加组件”列表里显示的是类型名。
                            result.TypeNames.Add(type.Name);
                            result.Entries[TypeKey(type.Name)] = Nicify(type.Name);
                        }
                        Walk(type, result, visited, gameAssemblies, depth: 0);
                    }
                    catch (Exception)
                    {
                        result.Skipped++;
                    }
                }
            }
            return result;
        }

        private static bool IsRoot(Type type) =>
            type.IsClass && !type.IsGenericTypeDefinition
            && (typeof(ComponentBase).IsAssignableFrom(type) || typeof(PrefabBase).IsAssignableFrom(type));

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null);
            }
            catch (Exception)
            {
                return Enumerable.Empty<Type>();
            }
        }

        // 编辑器会逐层展开嵌套结构（最多 5 层）。
        private const int kMaxDepth = 5;

        private static void Walk(Type type, Result result, HashSet<Type> visited, HashSet<Assembly> gameAssemblies, int depth)
        {
            if (depth > kMaxDepth || !visited.Add(type))
            {
                return;
            }
            // 成员属于声明它的类型（键里用的是声明类型），所以沿继承链逐层处理。
            for (Type t = type; t != null && t != typeof(object) && t != typeof(UnityEngine.Object) && t != typeof(ScriptableObject) && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                if (t.IsGenericType)
                {
                    continue; // 键里会带泛型参数，且这类类型很少直接显示字段
                }
                FieldInfo[] fields;
                try
                {
                    fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (FieldInfo field in fields)
                {
                    try
                    {
                        if (!IsShown(field))
                        {
                            continue;
                        }
                        string label = Nicify(field.Name);
                        if (label.Length >= 2)
                        {
                            result.Entries[FieldKey(t, field.Name)] = label;
                        }
                        Type inner = ElementOf(field.FieldType);
                        if (inner != null && IsGroup(inner, gameAssemblies))
                        {
                            Walk(inner, result, visited, gameAssemblies, depth + 1);
                        }
                    }
                    catch (Exception)
                    {
                        result.Skipped++;
                    }
                }
            }
        }

        /// <summary>游戏 EditorGenerator.kMemberFilter 的复刻：公开字段（或标了 ShowInEditor），去掉不序列化/隐藏的。</summary>
        private static bool IsShown(FieldInfo field)
        {
            try
            {
                return (field.IsPublic || field.IsDefined(typeof(ShowInEditorAttribute), false))
                    && !field.IsStatic
                    && !field.IsDefined(typeof(NonSerializedAttribute), true)
                    && !field.IsDefined(typeof(HideInInspector), false)
                    && !field.IsDefined(typeof(HideInEditorAttribute), false)
                    && field.Name != "active" && field.Name != "dirty" && field.Name != "m_Dirty"
                    && (!typeof(PrefabBase).IsAssignableFrom(field.DeclaringType) || field.Name != "components");
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Type ElementOf(Type type)
        {
            if (type.IsArray)
            {
                return type.GetElementType();
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                return type.GetGenericArguments()[0];
            }
            return type;
        }

        /// <summary>会被编辑器展开成一组子字段的类型：游戏或 mod 自己的可序列化类/结构（Unity、System 的类型有专门的输入控件，不展开）。</summary>
        private static bool IsGroup(Type type, HashSet<Assembly> gameAssemblies)
        {
            return (type.IsClass || (type.IsValueType && !type.IsPrimitive && !type.IsEnum))
                && type.IsSerializable
                && !typeof(ComponentBase).IsAssignableFrom(type)
                && !typeof(UnityEngine.Object).IsAssignableFrom(type)
                && gameAssemblies.Contains(type.Assembly);
        }
    }
}
