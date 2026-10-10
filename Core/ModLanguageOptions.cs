using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Modding;

namespace AITranslate.Core
{
    /// <summary>
    /// 有些 mod 自带语言选择（例如 Parking Lot Tool 的「语言：Automatic / English / Deutsch」），
    /// 选成固定语言时完全不读游戏词典，本 mod 的译文就不会显示。
    /// 这里把这类选项切换为“自动/跟随游戏”。
    /// </summary>
    internal static class ModLanguageOptions
    {
        private static readonly string[] s_PropertyHints = { "language", "lang", "sprache", "locale", "idioma", "langue", "lingua", "jezyk", "язык" };

        private static readonly string[] s_AutoMembers =
        {
            "automatic", "auto", "game", "gamelanguage", "followgame", "fromgame", "system", "os", "automatisch", "automatique", "automatico",
        };

        internal sealed class Change
        {
            public string ModName;
            public System.Reflection.Assembly Assembly;
            public string Property;
            public string From;
            public string To;

            public override string ToString() => $"{ModName}.{Property}: {From} → {To}";
        }

        /// <param name="apply">false 时只检测不修改。</param>
        /// <param name="handled">已处理过的「设置id.属性」；处理一次后不再改动，尊重玩家之后的手动选择。</param>
        public static List<Change> FollowGameLanguage(bool apply, Type exclude, ISet<string> handled)
        {
            var changes = new List<Change>();
            IDictionary instances;
            try
            {
                instances = typeof(ModSetting).GetProperty("instances", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null) as IDictionary;
            }
            catch (Exception ex)
            {
                Mod.Log.Warn("Cannot read mod settings: " + ex.Message);
                return changes;
            }
            if (instances == null)
            {
                return changes;
            }

            foreach (object value in instances.Values)
            {
                if (!(value is ModSetting setting) || setting.GetType() == exclude)
                {
                    continue;
                }
                bool changed = false;
                foreach (PropertyInfo property in setting.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (!property.CanRead || !property.CanWrite || !property.PropertyType.IsEnum)
                    {
                        continue;
                    }
                    string name = property.Name.ToLowerInvariant();
                    if (!s_PropertyHints.Any(h => name.Contains(h)))
                    {
                        continue;
                    }
                    string auto = Enum.GetNames(property.PropertyType)
                        .FirstOrDefault(n => s_AutoMembers.Contains(n.ToLowerInvariant().Replace("_", string.Empty)));
                    if (auto == null)
                    {
                        continue;
                    }
                    string id = setting.id + "." + property.Name;
                    if (handled != null && !handled.Add(id))
                    {
                        continue;
                    }
                    object current = property.GetValue(setting);
                    if (current == null || current.ToString() == auto)
                    {
                        continue;
                    }
                    changes.Add(new Change { ModName = setting.id, Assembly = setting.GetType().Assembly, Property = property.Name, From = current.ToString(), To = auto });
                    if (apply)
                    {
                        try
                        {
                            property.SetValue(setting, Enum.Parse(property.PropertyType, auto));
                            changed = true;
                        }
                        catch (Exception ex)
                        {
                            Mod.Log.Warn($"Cannot set {setting.id}.{property.Name}: {ex.Message}");
                        }
                    }
                }
                if (changed)
                {
                    try
                    {
                        setting.ApplyAndSave();
                    }
                    catch (Exception ex)
                    {
                        Mod.Log.Warn($"ApplyAndSave failed for {setting.id}: {ex.Message}");
                    }
                }
            }
            return changes;
        }
    }
}
