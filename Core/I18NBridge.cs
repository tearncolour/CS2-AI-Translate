using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AITranslate.Core
{
    /// <summary>
    /// 通过反射与 I18N Everywhere 交互（不做编译期依赖，未安装时自动降级）。
    /// 对应 I18NEverywhere.I18NEverywhere 的静态成员：CachedLanguagePacks / CachedMods /
    /// LoadLocales / LoadEmbedLocales / LoadCentralizedLocales / CurrentLocaleDictionary。
    /// </summary>
    internal sealed class I18NBridge
    {
        private const BindingFlags kStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags kInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly Type m_MainType;
        private readonly Type m_ModInfoType;

        public Assembly Assembly { get; }

        public bool Available => m_MainType != null;

        public string Version => Assembly?.GetName().Version?.ToString() ?? "-";

        private I18NBridge(Assembly assembly)
        {
            Assembly = assembly;
            m_MainType = assembly?.GetType("I18NEverywhere.I18NEverywhere");
            m_ModInfoType = assembly?.GetType("I18NEverywhere.Models.ModInfo");
        }

        public static I18NBridge Locate()
        {
            Assembly asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "I18NEverywhere", StringComparison.OrdinalIgnoreCase)
                    && a.GetType("I18NEverywhere.I18NEverywhere") != null);
            return new I18NBridge(asm);
        }

        /// <summary>I18N 是否已在主菜单完成首次加载。</summary>
        public bool IsInitialized
        {
            get
            {
                try
                {
                    object instance = m_MainType?.GetProperty("Instance", kStatic)?.GetValue(null);
                    return instance != null && (bool)(instance.GetType().GetProperty("GameLoaded", kInstance)?.GetValue(instance) ?? false);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>I18N 设置里「加载语言包」是否可用（需同时开启新版 mod 检测）。</summary>
        public bool CanLoadLanguagePacks
        {
            get
            {
                try
                {
                    object setting = m_MainType?.GetProperty("Setting", kStatic)?.GetValue(null);
                    object value = setting?.GetType().GetProperty("CanLoadLanguagePacks", kInstance)?.GetValue(setting);
                    return value is bool b ? b : true;
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        private PropertyInfo m_CurrentDictProp;

        /// <summary>I18N 运行时查询用的当前语言词典；每次 I18N 重载都会替换成新对象。</summary>
        public Dictionary<string, string> GetCurrentDictionary()
        {
            if (m_MainType == null)
            {
                return null;
            }
            try
            {
                m_CurrentDictProp = m_CurrentDictProp ?? m_MainType.GetProperty("CurrentLocaleDictionary", kStatic);
                return m_CurrentDictProp?.GetValue(null) as Dictionary<string, string>;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>I18N 的「Enable overwrite」。关闭时游戏词典已有的条目（英语游戏下的 mod 原文）不会被替换。</summary>
        public bool Overwrite
        {
            get
            {
                try
                {
                    object setting = m_MainType?.GetProperty("Setting", kStatic)?.GetValue(null);
                    object value = setting?.GetType().GetProperty("Overwrite", kInstance)?.GetValue(setting);
                    return value is bool b ? b : true;
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        private IList GetStaticList(string name)
        {
            return m_MainType?.GetProperty(name, kStatic)?.GetValue(null) as IList;
        }

        private static string Prop(object o, string name)
        {
            return o?.GetType().GetProperty(name, kInstance)?.GetValue(o) as string;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// I18N 只在自身 OnLoad 时扫描 ModsData 里的语言包；本 mod 首次运行时语言包目录可能还不存在，
        /// 这里把它补登记进 CachedLanguagePacks，之后 I18N 的每次重载都会读取它。
        /// </summary>
        public bool EnsureLanguagePackRegistered(string packName, string localizationDir)
        {
            try
            {
                IList packs = GetStaticList("CachedLanguagePacks");
                if (packs == null || m_ModInfoType == null)
                {
                    return false;
                }
                foreach (object p in packs)
                {
                    if (SamePath(Prop(p, "Path"), localizationDir))
                    {
                        return true;
                    }
                }
                object info = Activator.CreateInstance(m_ModInfoType);
                m_ModInfoType.GetProperty("Name", kInstance)?.SetValue(info, packName);
                m_ModInfoType.GetProperty("Path", kInstance)?.SetValue(info, localizationDir);
                m_ModInfoType.GetProperty("IsLanguagePack", kInstance)?.SetValue(info, true);
                packs.Add(info);
                Mod.Log.Info("Registered language pack into I18N Everywhere: " + localizationDir);
                return true;
            }
            catch (Exception ex)
            {
                Mod.Log.Warn(ex, "Failed to register language pack: " + ex.Message);
                return false;
            }
        }

        /// <summary>直接让 I18N 重新加载全部本地化（备用；正常情况下通过触发 onActiveDictionaryChanged 完成）。</summary>
        public bool Reload(string localeId, string fallbackLocaleId)
        {
            try
            {
                MethodInfo m = m_MainType?.GetMethod("LoadLocales", kStatic);
                if (m == null)
                {
                    return false;
                }
                m.Invoke(null, new object[] { localeId, fallbackLocaleId, true });
                return true;
            }
            catch (Exception ex)
            {
                Mod.Log.Warn(ex, "I18N reload failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>I18N 记录的各 mod（Name, mod/lang 目录），用于发现只靠 lang/en-US.json 本地化的 mod。</summary>
        public List<KeyValuePair<string, string>> GetCachedMods()
        {
            var result = new List<KeyValuePair<string, string>>();
            try
            {
                IList mods = GetStaticList("CachedMods");
                if (mods == null)
                {
                    return result;
                }
                foreach (object m in mods)
                {
                    string path = Prop(m, "Path");
                    if (!string.IsNullOrEmpty(path))
                    {
                        result.Add(new KeyValuePair<string, string>(Prop(m, "Name") ?? Path.GetFileName(Path.GetDirectoryName(path)), path));
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn(ex, "Read CachedMods failed: " + ex.Message);
            }
            return result;
        }

        /// <summary>
        /// 收集「除本 mod 语言包之外」I18N 能提供的目标语言译文：
        /// mod 自带的 lang/*.json、I18N 内置的集中翻译库、其他语言包。
        /// 若内部方法签名变化导致反射失败，则退回读取 CurrentLocaleDictionary 并剔除本 mod 的译文。
        /// </summary>
        private static readonly Dictionary<string, Dictionary<string, string>> s_OtherCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        /// <summary>玩家手动重新加载译文时清空。</summary>
        public static void ClearCache()
        {
            lock (s_OtherCache)
            {
                s_OtherCache.Clear();
            }
        }

        public Dictionary<string, string> CollectOtherTranslations(string localeId, string fallbackLocaleId, string ourLocalizationDir, IReadOnlyDictionary<string, string> ourTranslations)
        {
            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Available)
            {
                return current;
            }
            // 这些文本来自各 mod 的 lang 文件和 I18N 的翻译库，游戏运行期间不会变，读一次就缓存（每次都读磁盘会卡顿）。
            string cacheKey = localeId + "|" + (GetStaticList("CachedMods")?.Count ?? 0) + "|" + (GetStaticList("CachedLanguagePacks")?.Count ?? 0);
            Dictionary<string, string> cachedResult;
            lock (s_OtherCache)
            {
                s_OtherCache.TryGetValue(cacheKey, out cachedResult);
            }
            if (cachedResult != null)
            {
                return cachedResult;
            }
            try
            {
                var fallback = new Dictionary<string, string>(StringComparer.Ordinal);
                MethodInfo embed = m_MainType.GetMethod("LoadEmbedLocales", kStatic);
                MethodInfo central = m_MainType.GetMethod("LoadCentralizedLocales", kStatic);
                if (embed == null || central == null || embed.GetParameters().Length != 5 || central.GetParameters().Length != 6)
                {
                    throw new MissingMethodException("I18N internal API changed");
                }
                embed.Invoke(null, new object[] { current, fallback, localeId, fallbackLocaleId, false });
                central.Invoke(null, new object[] { current, fallback, localeId, fallbackLocaleId, false, null });

                IList packs = GetStaticList("CachedLanguagePacks");
                if (packs != null)
                {
                    foreach (object p in packs)
                    {
                        string path = Prop(p, "Path");
                        if (string.IsNullOrEmpty(path) || SamePath(path, ourLocalizationDir))
                        {
                            continue;
                        }
                        central.Invoke(null, new object[] { current, fallback, localeId, fallbackLocaleId, false, path });
                    }
                }
                lock (s_OtherCache)
                {
                    s_OtherCache[cacheKey] = current;
                }
                return current;
            }
            catch (Exception ex)
            {
                Mod.Log.Warn("Precise I18N scan unavailable, falling back to CurrentLocaleDictionary: " + (ex.InnerException ?? ex).Message);
            }

            try
            {
                if (m_MainType.GetProperty("CurrentLocaleDictionary", kStatic)?.GetValue(null) is Dictionary<string, string> dict)
                {
                    foreach (var kv in dict)
                    {
                        if (ourTranslations != null && ourTranslations.TryGetValue(kv.Key, out var ours) && ours == kv.Value)
                        {
                            continue;
                        }
                        current[kv.Key] = kv.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn(ex, "Read CurrentLocaleDictionary failed: " + ex.Message);
            }
            return current;
        }
    }
}
