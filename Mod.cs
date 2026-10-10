using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AITranslate.Core;
using AITranslate.Locale;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Colossal.PSI.Environment;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Newtonsoft.Json.Linq;

namespace AITranslate
{
    public class Mod : IMod
    {
        public const string Name = "AITranslate";

        public const string DisplayName = "AI Translate!";

        // 改名前（I18N Auto Translate）的设置名。
        private const string kLegacyName = "I18NAutoTranslate";

        public static ILog Log { get; } = LogManager.GetLogger($"{Name}.{nameof(Mod)}").SetShowsErrorsInUI(false);

        public static Mod Instance { get; private set; }

        public static Setting Setting { get; private set; }

        internal TranslationService Service { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Instance = this;
            Log.Info($"Loading {DisplayName}...");

            Setting = new Setting(this);
            Setting.RegisterInOptionsUI();
            AssetDatabase.global.LoadSettings(Name, Setting, new Setting(this));
            MigrateLegacySettings(Setting);
            if (Setting.ModelName == Setting.kLegacyDefaultModel)
            {
                // 还是旧的默认模型（玩家没自己改过）：换成新的默认值。
                Setting.ModelName = Setting.kDefaultModel;
                Setting.ApplyAndSave();
            }
            Log.Info($"Settings loaded: api={Setting.ApiUrl}, model={Setting.ModelName}, key={(string.IsNullOrEmpty(Setting.ApiKey) ? "<empty>" : "set")}");

            // 设置页预置游戏自带的全部语言；其他语言由本 mod 在填好 API Key 后自己翻译。
            var localization = GameManager.instance.localizationManager;
            foreach (string locale in LocaleTable.BundledLocales)
            {
                localization.AddSource(locale, new LocaleSource(Setting, locale));
            }

            Service = new TranslationService(Setting);
            Service.PrepareStorage();
            // 不在这里扫描：订阅的 mod 与 I18N Everywhere 往往比本地 mod 晚几十秒加载，由服务监视环境稳定后再扫描。
            Service.Start();
        }

        /// <summary>
        /// 沿用改名前（I18N Auto Translate）保存的设置。
        /// 游戏在 mod 加载前就索引好了所有 .coc 文件，LoadSettings 只从索引读取，所以不能靠复制文件迁移：
        /// 这里在新设置加载后，从旧文件读出数值写入新设置，再清空内存中的旧设置片段，
        /// 让游戏下次保存设置时自行删除旧文件（否则它会一直把旧文件写回来）。
        /// </summary>
        private static void MigrateLegacySettings(Setting setting)
        {
            try
            {
                var legacyAssets = AssetDatabase.global.GetAssets<SettingAsset>().Where(a => a.name == kLegacyName).ToList();
                string legacyFile = Path.Combine(EnvPath.kUserDataPath, "ModsSettings", kLegacyName, kLegacyName + ".coc");

                // 只有新设置尚未配置时才导入，避免用旧值覆盖玩家之后填写的新值。
                if (string.IsNullOrWhiteSpace(setting.ApiKey) && File.Exists(legacyFile))
                {
                    int imported = ImportSettingsFile(setting, legacyFile);
                    if (imported > 0)
                    {
                        setting.ApplyAndSave();
                        Log.Info($"Imported {imported} legacy settings from {legacyFile}");
                    }
                }

                foreach (SettingAsset asset in legacyAssets)
                {
                    foreach (SettingAsset.Fragment fragment in asset)
                    {
                        fragment.variant = null;
                        fragment.source = null;
                    }
                    Log.Info("Legacy settings asset cleared; the game removes the old file on its next settings save.");
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "Settings migration failed: " + ex.Message);
            }
        }

        /// <summary>读取 .coc 文件（第一行为设置名，其余为 JSON），把同名属性写入设置对象。返回导入的属性数。</summary>
        private static int ImportSettingsFile(Setting setting, string path)
        {
            string text = File.ReadAllText(path);
            int brace = text.IndexOf('{');
            if (brace < 0)
            {
                return 0;
            }
            JObject json = JObject.Parse(text.Substring(brace));
            int count = 0;
            foreach (var property in json.Properties())
            {
                PropertyInfo target = typeof(Setting).GetProperty(property.Name, BindingFlags.Instance | BindingFlags.Public);
                if (target == null || !target.CanWrite || !target.CanRead)
                {
                    continue;
                }
                try
                {
                    target.SetValue(setting, property.Value.ToObject(target.PropertyType));
                    count++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Skip legacy setting {property.Name}: {ex.Message}");
                }
            }
            return count;
        }

        public void OnDispose()
        {
            Service?.Dispose();
            Service = null;
            if (Setting != null)
            {
                Setting.UnregisterInOptionsUI();
                Setting = null;
            }
            Instance = null;
        }
    }
}
