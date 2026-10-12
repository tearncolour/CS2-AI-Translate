using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Colossal;
using Colossal.IO.AssetDatabase;
using Colossal.Localization;
using Colossal.PSI.Environment;
using Game.Modding;
using Game.Prefabs;
using Game.SceneFlow;
using Newtonsoft.Json.Linq;

namespace AITranslate.Core
{
    /// <summary>本地化词典的副本：key → (值, 是否为回退条目)。可在任意线程读取。</summary>
    internal sealed class DictSnapshot
    {
        private readonly Dictionary<string, (string value, bool fallback)> m_Entries;

        public static readonly DictSnapshot Empty = new DictSnapshot(new Dictionary<string, (string, bool)>(StringComparer.Ordinal));

        public DictSnapshot(Dictionary<string, (string value, bool fallback)> entries)
        {
            m_Entries = entries;
        }

        public int Count => m_Entries.Count;

        public IEnumerable<KeyValuePair<string, string>> Entries => m_Entries.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.value));

        /// <summary>原始条目（不经过 I18N Everywhere 的拦截）。</summary>
        public bool TryGetRaw(string key, out string value, out bool fallback)
        {
            if (key != null && m_Entries.TryGetValue(key, out var entry))
            {
                value = entry.value;
                fallback = entry.fallback;
                return value != null;
            }
            value = null;
            fallback = false;
            return false;
        }

        public bool TryGetValue(string key, out string value) => TryGetRaw(key, out value, out _);

        public bool ContainsID(string key, bool ignoreFallbackEntries = false) =>
            key != null && m_Entries.TryGetValue(key, out var entry) && (!ignoreFallbackEntries || !entry.fallback);
    }

    /// <summary>一个本地化源的副本。</summary>
    internal sealed class SourceSnapshot
    {
        public bool IsLocaleAsset;
        public string AssetPath;
        public string AssetName;
        public Assembly Assembly;
        public string TypeName;
        public Dictionary<string, string> Entries;
    }

    /// <summary>一个已加载 mod 程序集的副本。</summary>
    internal sealed class ExecutableSnapshot
    {
        public Assembly Assembly;
        public string Name;
        public string Path;
    }

    /// <summary>一个预制件的副本（Unity 对象只能在主线程读取，这里只保留后台分析要用的字段）。</summary>
    internal sealed class PrefabSnapshot
    {
        public string Name;
        public bool IsBuiltin;
        public string AssetPath;
        public Setting.ModCategory? Category;

        /// <summary>可放置的资产（对象、道路、区域或出现在建造菜单），且不是服务/菜单分组。</summary>
        public bool IsCandidate;
        public bool IsUpgrade;
        public bool HasUIObject;
        public bool IsSpawnableBuilding;
        public bool IsBrand;

        /// <summary>所属的 DLC/创作者包（ContentPrefab 名）。只在开启「包含 DLC 和创作者包」时采集，否则为 null。</summary>
        public string ContentPack;

        /// <summary>写描述要用的资产信息（只给 mod 的建造菜单资产准备）。</summary>
        public AssetContext Context;
    }

    /// <summary>一次检测需要的全部游戏数据。由 <see cref="ScanCapture"/> 在主线程分帧采集，之后可在后台线程分析。</summary>
    internal sealed class ScanSnapshot
    {
        public string Locale;
        public string FallbackLocale;
        public List<Colossal.PSI.Common.Mod> PlaysetMods = new List<Colossal.PSI.Common.Mod>();
        public readonly List<ExecutableSnapshot> Executables = new List<ExecutableSnapshot>();
        public readonly List<SourceSnapshot> Sources = new List<SourceSnapshot>();
        public readonly List<PrefabSnapshot> Prefabs = new List<PrefabSnapshot>();
        public List<KeyValuePair<string, string>> I18NCachedMods = new List<KeyValuePair<string, string>>();
        public DictSnapshot Active = DictSnapshot.Empty;
        public Dictionary<string, string> Fallback = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>I18N Everywhere 当前词典（含本 mod 注入的译文）。没有 I18N 时为 null。</summary>
        public Dictionary<string, string> I18NCurrent;

        /// <summary>运行环境已稳定（I18N 已加载完，或已确定没有 I18N）。</summary>
        public bool Settled;

        /// <summary>Road Builder 生成的道路/小路：prefab 名（GUID-SteamID）→ 用户在 Road Builder 里起的名字。</summary>
        public Dictionary<string, string> RoadBuilderNames = new Dictionary<string, string>(StringComparer.Ordinal);

        public long CaptureMs;
        public int CaptureFrames;

        /// <summary>采集时单帧最长耗时（毫秒），用来发现超出每帧预算、会造成卡顿的步骤。</summary>
        public double CaptureMaxFrameMs;
    }

    /// <summary>
    /// 在主线程分帧采集 <see cref="ScanSnapshot"/>：每帧只工作一小段时间（默认 4 毫秒），游戏界面不会卡住。
    /// 用法：每帧调用 <see cref="Step"/>，返回 true 后读取 <see cref="Result"/>。
    /// </summary>
    internal sealed class ScanCapture
    {
        private const BindingFlags kAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly ScanSnapshot m_Snapshot = new ScanSnapshot();
        private readonly I18NBridge m_I18N;
        private readonly IEnumerator<bool> m_Steps;
        private readonly Stopwatch m_Total = new Stopwatch();
        private Stopwatch m_Frame;
        private double m_BudgetMs;

        public ScanSnapshot Result => m_Snapshot;

        public ScanCapture(IEnumerable<Colossal.PSI.Common.Mod> playsetMods, I18NBridge i18n)
        {
            m_I18N = i18n;
            if (playsetMods != null)
            {
                m_Snapshot.PlaysetMods = playsetMods.ToList();
            }
            m_Steps = Run().GetEnumerator();
        }

        /// <summary>执行一帧的工作量，全部完成时返回 true。</summary>
        public bool Step(double budgetMs)
        {
            m_BudgetMs = budgetMs;
            m_Frame = Stopwatch.StartNew();
            m_Total.Start();
            m_Snapshot.CaptureFrames++;
            bool more;
            try
            {
                more = m_Steps.MoveNext();
            }
            finally
            {
                m_Total.Stop();
                m_Snapshot.CaptureMaxFrameMs = Math.Max(m_Snapshot.CaptureMaxFrameMs, m_Frame.Elapsed.TotalMilliseconds);
            }
            if (!more)
            {
                m_Snapshot.CaptureMs = m_Total.ElapsedMilliseconds;
            }
            return !more;
        }

        private bool OverBudget => m_Frame.Elapsed.TotalMilliseconds >= m_BudgetMs;

        private IEnumerable<bool> Run()
        {
            var lm = GameManager.instance.localizationManager;
            m_Snapshot.Locale = lm.activeLocaleId;
            m_Snapshot.FallbackLocale = lm.fallbackLocaleId;

            // 已加载的 mod 程序集
            foreach (ModManager.ModInfo info in GameManager.instance.modManager)
            {
                var asset = info?.asset;
                if (asset?.assembly == null)
                {
                    continue;
                }
                string path = null;
                try
                {
                    path = asset.path;
                }
                catch (Exception)
                {
                    // 没有路径时按程序集名归类。
                }
                m_Snapshot.Executables.Add(new ExecutableSnapshot { Assembly = asset.assembly, Name = asset.name, Path = path });
            }
            m_Snapshot.I18NCachedMods = m_I18N.GetCachedMods();
            yield return true;

            // 本地化源（每个源读一次，超时就留到下一帧）
            foreach (IDictionarySource source in GetSources(lm, m_Snapshot.FallbackLocale))
            {
                if (source is GatedMemorySource)
                {
                    continue; // 本 mod 注入译文用的源
                }
                var snap = new SourceSnapshot { TypeName = source.GetType().Name, Assembly = source.GetType().Assembly };
                if (source is LocaleAsset localeAsset)
                {
                    if (localeAsset.isBuiltin)
                    {
                        continue; // 原版文本，官方已有翻译
                    }
                    snap.IsLocaleAsset = true;
                    try
                    {
                        snap.AssetPath = localeAsset.path;
                    }
                    catch (Exception)
                    {
                        // 部分资产没有元数据路径。
                    }
                    snap.AssetName = localeAsset.name;
                }
                snap.Entries = ReadSource(source);
                if (snap.Entries.Count > 0)
                {
                    m_Snapshot.Sources.Add(snap);
                }
                if (OverBudget)
                {
                    yield return true;
                }
            }
            yield return true;

            // 预制件
            List<PrefabBase> prefabs;
            try
            {
                var prefabSystem = Unity.Entities.World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<PrefabSystem>();
                prefabs = (typeof(PrefabSystem).GetProperty("prefabs", kAll)?.GetValue(prefabSystem) as IEnumerable<PrefabBase>)?.ToList()
                    ?? new List<PrefabBase>();
            }
            catch (Exception ex)
            {
                Mod.Log.Warn("Cannot enumerate prefabs: " + ex.Message);
                prefabs = new List<PrefabBase>();
            }
            yield return true;
            bool includeDlc = Mod.Setting?.IncludeDlcContent == true;
            for (int i = 0; i < prefabs.Count; i++)
            {
                var snap = CapturePrefab(prefabs[i], includeDlc);
                if (snap != null)
                {
                    m_Snapshot.Prefabs.Add(snap);
                }
                if ((i & 31) == 31 && OverBudget)
                {
                    yield return true;
                }
            }
            yield return true;

            // 词典副本（活动词典可能有 2–3 万条，分批复制避免单帧卡顿）
            {
                var dictCopy = new Dictionary<string, (string, bool)>(lm.activeDictionary.entryCount, StringComparer.Ordinal);
                int di = 0;
                foreach (var kv in lm.activeDictionary.entries)
                {
                    dictCopy[kv.Key] = (kv.Value, !lm.activeDictionary.ContainsID(kv.Key, ignoreFallbackEntries: true));
                    if ((++di & 255) == 0 && OverBudget)
                    {
                        yield return true;
                    }
                }
                m_Snapshot.Active = new DictSnapshot(dictCopy);
            }
            yield return true;
            if (typeof(LocalizationManager).GetField("m_FallbackDictionary", kAll)?.GetValue(lm) is LocalizationDictionary fallback)
            {
                var copy = new Dictionary<string, string>(fallback.entryCount, StringComparer.Ordinal);
                int fi = 0;
                foreach (var kv in fallback.entries)
                {
                    copy[kv.Key] = kv.Value;
                    if ((++fi & 255) == 0 && OverBudget)
                    {
                        yield return true;
                    }
                }
                m_Snapshot.Fallback = copy;
            }
            yield return true;
            var i18nDict = m_I18N.GetCurrentDictionary();
            if (i18nDict != null)
            {
                var i18nCopy = new Dictionary<string, string>(i18nDict.Count, StringComparer.Ordinal);
                int ii = 0;
                foreach (var kv in i18nDict)
                {
                    i18nCopy[kv.Key] = kv.Value;
                    if ((++ii & 255) == 0 && OverBudget)
                    {
                        yield return true;
                    }
                }
                m_Snapshot.I18NCurrent = i18nCopy;
            }
            // Road Builder：读取用户生成的道路名称（prefab 名是 GUID-SteamID，真正的名字在 JSON 里）。
            if (Mod.Setting?.IncludeRoadBuilderRoads == true)
            {
                LoadRoadBuilderNames(m_Snapshot.RoadBuilderNames);
            }
        }

        private static void LoadRoadBuilderNames(Dictionary<string, string> names)
        {
            try
            {
                string dir = Path.Combine(EnvPath.kUserDataPath, "ModsData", "RoadBuilder");
                if (!Directory.Exists(dir))
                {
                    return;
                }
                foreach (string file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        string id = Path.GetFileNameWithoutExtension(file);
                        string json = File.ReadAllText(file, System.Text.Encoding.UTF8);
                        var obj = JObject.Parse(json);
                        string roadName = (string)obj["Name"];
                        if (!string.IsNullOrWhiteSpace(roadName) && LanguageDetector.HasLetters(roadName))
                        {
                            names[id] = roadName.Trim();
                        }
                    }
                    catch (Exception)
                    {
                        // 单个文件损坏不影响整体。
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn("LoadRoadBuilderNames failed: " + ex.Message);
            }
        }

        private PrefabSnapshot CapturePrefab(PrefabBase prefab, bool includeDlc)
        {
            try
            {
                if (prefab == null)
                {
                    return null;
                }
                string name = prefab.name;
                if (string.IsNullOrEmpty(name))
                {
                    return null;
                }
                var snap = new PrefabSnapshot { Name = name, IsBuiltin = prefab.isBuiltin, IsBrand = prefab is BrandPrefab };
                if (snap.IsBuiltin)
                {
                    // 原版/DLC 只需要知道是不是可成长建筑（用来给没有名字的创作者包建筑补名）。
                    snap.IsSpawnableBuilding = prefab is BuildingPrefab && prefab.Has<SpawnableBuilding>();
                    // 开启「包含 DLC 和创作者包」时，DLC/创作者包的资产和 mod 资产一样采集完整信息。
                    snap.ContentPack = includeDlc ? DlcPackOf(prefab) : null;
                    if (snap.ContentPack == null)
                    {
                        return snap;
                    }
                }
                else
                {
                    try
                    {
                        snap.AssetPath = prefab.asset?.path;
                    }
                    catch (Exception)
                    {
                        // 没有资产文件的预制件。
                    }
                }
                snap.Category = ModScanner.CategoryOf(prefab);
                snap.HasUIObject = prefab.Has<UIObject>();
                snap.IsCandidate = (prefab is ObjectPrefab || prefab is NetPrefab || prefab is AreaPrefab || snap.HasUIObject)
                    && !(prefab is ServicePrefab || prefab is UIAssetMenuPrefab || prefab is UIAssetCategoryPrefab);
                snap.IsUpgrade = prefab.Has<ServiceUpgrade>();
                if (snap.IsCandidate && snap.HasUIObject)
                {
                    string titleKey = (snap.IsUpgrade ? "Assets.UPGRADE_NAME[" : "Assets.NAME[") + name + "]";
                    string descKey = (snap.IsUpgrade ? "Assets.UPGRADE_DESCRIPTION[" : "Assets.DESCRIPTION[") + name + "]";
                    snap.Context = AssetContext.From(prefab, descKey, titleKey, null, null);
                }
                return snap;
            }
            catch (Exception)
            {
                return null; // 个别预制件异常不影响整体检测
            }
        }

        // ContentPrefab → 是否是已拥有的 DLC/创作者包。
        private readonly Dictionary<ContentPrefab, bool> m_OwnedPacks = new Dictionary<ContentPrefab, bool>();

        /// <summary>
        /// 资产所属的 DLC/创作者包：ContentPrerequisite 指向的、需要 DLC 的 ContentPrefab 名（如 "MediterraneanHeritage"）。
        /// 原版资产和没有拥有的 DLC 返回 null（没买的包不花 API）。
        /// </summary>
        private string DlcPackOf(PrefabBase prefab)
        {
            if (!prefab.TryGet<ContentPrerequisite>(out var prerequisite) || prerequisite.m_ContentPrerequisite == null)
            {
                return null;
            }
            var content = prerequisite.m_ContentPrerequisite;
            if (!m_OwnedPacks.TryGetValue(content, out bool owned))
            {
                try
                {
                    owned = content.Has<DlcRequirement>() && content.IsAvailable();
                }
                catch (Exception)
                {
                    owned = false;
                }
                m_OwnedPacks[content] = owned;
            }
            return owned ? content.name : null;
        }

        /// <summary>复制游戏词典的原始条目（entries 直接读内部字典，不经过 I18N 的拦截）。内部字典泛型类型参数里含有私有 struct，用反射拿到后转成只读 IEnumerable 来用。</summary>
        /// <summary>复制游戏词典的原始条目（entries 直接读内部字典，不经过 I18N 的拦截）。</summary>
        private static DictSnapshot CopyDictionary(LocalizationDictionary dict)
        {
            var copy = new Dictionary<string, (string, bool)>(dict.entryCount, StringComparer.Ordinal);
            foreach (var kv in dict.entries)
            {
                copy[kv.Key] = (kv.Value, !dict.ContainsID(kv.Key, ignoreFallbackEntries: true));
            }
            return new DictSnapshot(copy);
        }

        /// <summary>某语言下的本地化源数量（不含本 mod 的注入源），用于检测是否有 mod 新增了文本。</summary>
        internal static int CountSources(LocalizationManager lm, string localeId)
        {
            return GetSources(lm, localeId).Count(s => !(s is GatedMemorySource));
        }

        private static IEnumerable<IDictionarySource> GetSources(LocalizationManager lm, string localeId)
        {
            var infos = typeof(LocalizationManager).GetField("m_LocaleInfos", kAll)?.GetValue(lm) as IDictionary;
            if (infos == null || !infos.Contains(localeId))
            {
                return Enumerable.Empty<IDictionarySource>();
            }
            object info = infos[localeId];
            var sources = info?.GetType().GetField("m_Sources", kAll)?.GetValue(info) as IEnumerable;
            return sources?.OfType<IDictionarySource>().ToList() ?? new List<IDictionarySource>();
        }

        private static Dictionary<string, string> ReadSource(IDictionarySource source)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var kv in source.ReadEntries(new List<IDictionaryEntryError>(), new Dictionary<string, int>()))
                {
                    if (kv.Key != null && kv.Value != null)
                    {
                        dict[kv.Key] = kv.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn("Read localization source " + source.GetType().FullName + " failed: " + ex.Message);
            }
            return dict;
        }
    }
}
