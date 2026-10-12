using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Colossal;
using Colossal.IO.AssetDatabase;
using Colossal.Localization;
using Colossal.PSI.Environment;
using Game.Modding;
using Game.Prefabs;
using Game.SceneFlow;
using Newtonsoft.Json;

namespace AITranslate.Core
{
    internal enum ModState
    {
        NoText,
        NotNeeded,
        Translated,
        NeedsTranslation,
    }

    /// <summary>一个 mod（或资产包）在当前语言下的翻译状况。</summary>
    internal sealed class ModTextInfo
    {
        public string Key;
        public string DisplayName;

        /// <summary>key → 英文原文（全部条目）。</summary>
        public readonly Dictionary<string, string> Source = new Dictionary<string, string>(StringComparer.Ordinal);

        public int Translatable;
        public int Native;
        public int Done;
        public int Outdated;
        public readonly List<string> Pending = new List<string>();

        /// <summary>可由本 mod 翻译（不含自带/其他来源已有译文）的 key。</summary>
        public readonly List<string> OwnKeys = new List<string>();

        /// <summary>之前机翻过、但现在已有自带/官方译文的 key，应从译文文件中移除。</summary>
        public readonly List<string> Stale = new List<string>();

        /// <summary>出现在建造菜单里、但没有任何描述的资产（待生成）。</summary>
        public readonly List<AssetContext> MissingDescriptions = new List<AssetContext>();

        /// <summary>已由本 mod 生成描述的资产数。</summary>
        public int GeneratedDescriptions;

        /// <summary>只有随机编号作名字、需要起名的资产数。</summary>
        public int MissingNames;

        /// <summary>mod 类型（建筑资产、车辆资产、玩法/工具 MOD 等）。</summary>
        public Setting.ModCategory Category = Setting.ModCategory.Other;

        /// <summary>该 mod 含有代码（程序集）。</summary>
        public bool HasCode;

        /// <summary>各类预制件数量。</summary>
        public readonly Dictionary<Setting.ModCategory, int> PrefabCounts = new Dictionary<Setting.ModCategory, int>();

        /// <summary>之前生成过描述、但资产现在自带描述了，应删除生成的描述。</summary>
        public readonly List<string> StaleDescriptions = new List<string>();

        /// <summary>官方或 mod 自带、但中英混杂的译文（key, 英文原文, 现有译文），待 AI 判断是否修正。</summary>
        public readonly List<(string key, string source, string current)> MixedNative = new List<(string, string, string)>();

        /// <summary>修正过的官方/自带译文后来变了（作者或官方更新了译文）：之前的修正作废，应删除。</summary>
        public readonly List<string> StaleFixes = new List<string>();

        /// <summary>没有减少照搬英文的改写：撤回为原译文，但保留“已检查”记录，不再重复请求。</summary>
        public readonly List<string> RevertFixes = new List<string>();

        /// <summary>“不用改”的记录对应的原译文变了：改记到现在的译文上（key → 现在的译文），仍算已检查。</summary>
        public readonly Dictionary<string, string> RemarkFixes = new Dictionary<string, string>(StringComparer.Ordinal);

        public ModState State
        {
            get
            {
                if (Translatable == 0)
                {
                    return ModState.NoText;
                }
                if (Pending.Count > 0)
                {
                    return ModState.NeedsTranslation;
                }
                return Done > 0 ? ModState.Translated : ModState.NotNeeded;
            }
        }
    }

    internal sealed class ScanResult
    {
        public string Locale;
        public string FallbackLocale;
        public DateTime Time;
        public readonly Dictionary<string, ModTextInfo> Mods = new Dictionary<string, ModTextInfo>(StringComparer.Ordinal);

        /// <summary>译文分组 → 重复的“拼出来的名字”（同一资产已有真正名字的译文）。后台算好，主线程只负责删除。</summary>
        public Dictionary<string, List<string>> DuplicateGeneratedNames = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        /// <summary>本次扫描补充的无名称资产数量。</summary>
        public int UnnamedAssets;

        /// <summary>缺少描述的资产总数。</summary>
        public int MissingDescriptions => Mods.Values.Sum(m => m.MissingDescriptions.Count);

        // 术语表原始数据（只保留短条目）。
        public readonly Dictionary<string, string> GlossaryEnglish = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> GlossaryTarget = new Dictionary<string, string>(StringComparer.Ordinal);

        // 游戏原版区域建筑的官方译名：完整类型短语（"EU Residential Low" → "欧洲低层住宅"）、类型（"Residential Low" → "低层住宅"）、地区（"EU" → "欧洲"）。
        public readonly Dictionary<string, string> OfficialBuildingPhrases = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> OfficialBuildingTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> OfficialRegions = new Dictionary<string, string>(StringComparer.Ordinal);

        // 官方分区名（"CCP5 Residential Medium" → 英文 + 当前语言），用来说明创作者包代码代表哪个包。
        public readonly Dictionary<string, (string english, string translated)> ZoneNames = new Dictionary<string, (string, string)>(StringComparer.Ordinal);

        /// <summary>已安装 mod 的名称，翻译时必须原样保留。</summary>
        public ProtectedNames ProtectedNames = ProtectedNames.Empty;
    }

    /// <summary>
    /// 分析一次检测的快照（ScanSnapshot）：按所属 mod 分组本地化文本，与当前语言已有的译文对比，
    /// 得出每个 mod 是否需要翻译。不访问游戏对象，在后台线程运行。
    /// </summary>
    internal sealed class ModScanner
    {
        private const BindingFlags kAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>本 mod 自身界面文本的分组键。</summary>
        internal const string kSelfKey = "self_AITranslate";

        /// <summary>Road Builder 生成道路的分组键。</summary>
        private const string kRoadBuilderKey = "mod_RoadBuilder";
        private const string kRoadBuilderDisplayName = "Road Builder";

        /// <summary>DLC/创作者包分组键的前缀（后接 ContentPrefab 名）。</summary>
        private const string kContentPackPrefix = "dlc_";

        internal static bool IsContentPack(string key) => key != null && key.StartsWith(kContentPackPrefix, StringComparison.Ordinal);

        private readonly Dictionary<string, Owner> m_ContentPackOwners = new Dictionary<string, Owner>(StringComparer.Ordinal);

        /// <summary>DLC/创作者包的分组，名称用游戏里显示的包名（如 "Creator Pack: Mediterranean Heritage"），没有时由内部名拆出。</summary>
        private Owner ContentPackOwner(string pack)
        {
            if (!m_ContentPackOwners.TryGetValue(pack, out var owner))
            {
                string nameKey = "Assets.NAME[" + pack + "]";
                string name = TryGetDisplay(nameKey, out var shown) && !string.IsNullOrWhiteSpace(shown) ? shown.Trim() : HumanizePrefabName(pack);
                m_ContentPackOwners[pack] = owner = new Owner { Key = kContentPackPrefix + pack, Name = name };
            }
            return owner;
        }

        // Options.SECTION[Asm.Ns.Mod] 这类标准 key 中的程序集名，或 "ParkingLotTool.xxx" 这类以程序集名开头的私有 key。
        private static readonly Regex s_KeyOwner = new Regex(@"\[([A-Za-z_][A-Za-z0-9_]*)\.|^([A-Za-z_][A-Za-z0-9_]*)\.", RegexOptions.Compiled);

        private sealed class Owner
        {
            public string Key;
            public string Name;
            public string Root;
        }

        private readonly ScanSnapshot m_Snapshot;
        private readonly List<Owner> m_Roots = new List<Owner>();
        private readonly Dictionary<Assembly, ExecutableSnapshot> m_Assemblies = new Dictionary<Assembly, ExecutableSnapshot>();
        private readonly Dictionary<string, ExecutableSnapshot> m_AssembliesByName = new Dictionary<string, ExecutableSnapshot>(StringComparer.OrdinalIgnoreCase);

        /// <summary>只读取快照，不访问任何游戏对象，可在后台线程运行。</summary>
        public ModScanner(ScanSnapshot snapshot)
        {
            m_Snapshot = snapshot;
            foreach (var m in snapshot.PlaysetMods)
            {
                if (string.IsNullOrEmpty(m.path))
                {
                    continue;
                }
                m_Roots.Add(new Owner { Key = "pdx_" + m.id, Name = string.IsNullOrWhiteSpace(m.displayName) ? m.id : m.displayName.Trim(), Root = NormalizeDir(m.path) });
                if (m.tags != null && m.tags.Any(t => string.Equals(t, "Code Mod", StringComparison.OrdinalIgnoreCase)))
                {
                    m_CodeOwners.Add("pdx_" + m.id);
                }
            }
            string localMods = Path.Combine(EnvPath.kUserDataPath, "Mods");
            if (Directory.Exists(localMods))
            {
                foreach (var dir in new DirectoryInfo(localMods).GetDirectories())
                {
                    if (!dir.Name.StartsWith(".") && !dir.Name.StartsWith("~"))
                    {
                        m_Roots.Add(new Owner { Key = "local_" + dir.Name, Name = dir.Name, Root = NormalizeDir(dir.FullName) });
                    }
                }
            }
            // 长路径优先，避免父目录抢先匹配。
            m_Roots.Sort((a, b) => b.Root.Length.CompareTo(a.Root.Length));

            foreach (var asset in snapshot.Executables)
            {
                m_Assemblies[asset.Assembly] = asset;
                m_AssembliesByName[asset.Name] = asset;
                if (!string.Equals(asset.Name, "0Harmony", StringComparison.OrdinalIgnoreCase))
                {
                    m_CodeOwners.Add(OwnerByAsset(asset).Key);
                }
            }
        }

        private static string NormalizeDir(string path)
        {
            try
            {
                return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/') + "/";
            }
            catch (Exception)
            {
                return (path ?? string.Empty).Replace('\\', '/').TrimEnd('/') + "/";
            }
        }

        // 目录 → 所属 mod 的缓存。同一目录下的上万个资产文件只需查一次（路径规范化 + 逐个比对 mod 根目录很慢）。
        private readonly Dictionary<string, Owner> m_DirOwners = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);

        private Owner OwnerByPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            string dir;
            try
            {
                dir = Path.GetDirectoryName(path);
            }
            catch (Exception)
            {
                dir = null;
            }
            if (dir != null && m_DirOwners.TryGetValue(dir, out var cached))
            {
                return cached;
            }
            string p = NormalizeDir(path);
            Owner owner = m_Roots.FirstOrDefault(o => p.StartsWith(o.Root, StringComparison.OrdinalIgnoreCase));
            // 只有当所在目录本身也在该 mod 根目录下时才能按目录缓存（path 恰好就是根目录时不行）。
            if (dir != null)
            {
                string d = NormalizeDir(dir);
                if (owner == null ? !m_Roots.Any(o => o.Root.StartsWith(d, StringComparison.OrdinalIgnoreCase))
                                  : d.StartsWith(owner.Root, StringComparison.OrdinalIgnoreCase))
                {
                    m_DirOwners[dir] = owner;
                }
            }
            return owner;
        }

        private Owner OwnerByAsset(ExecutableSnapshot asset)
        {
            string dir = null;
            try
            {
                dir = string.IsNullOrEmpty(asset.Path) ? null : Path.GetDirectoryName(asset.Path);
            }
            catch (Exception)
            {
                // path 不可用时按程序集名归类。
            }
            return OwnerByPath(dir) ?? new Owner { Key = "asm_" + asset.Name, Name = asset.Name };
        }

        // ---------------------------------------------------------------- 预制件归属

        // 预制件名 → 所属 mod（按资产文件所在目录判断）。用于把共用来源（如资产导入工具的 MemorySource）里的资产名分给真正的 mod。
        private Dictionary<string, Owner> m_PrefabOwners;

        private Dictionary<string, Owner> PrefabOwners()
        {
            if (m_PrefabOwners != null)
            {
                return m_PrefabOwners;
            }
            m_PrefabOwners = new Dictionary<string, Owner>(StringComparer.Ordinal);
            foreach (var prefab in m_Snapshot.Prefabs)
            {
                if (prefab.IsBuiltin)
                {
                    continue;
                }
                Owner owner = OwnerByPath(prefab.AssetPath);
                if (owner != null)
                {
                    m_PrefabOwners[prefab.Name] = owner;
                }
            }
            return m_PrefabOwners;
        }

        /// <summary>界面上显示的文字：I18N（含本 mod 的译文）优先，其次游戏词典。</summary>
        private bool TryGetDisplay(string key, out string value)
        {
            if (m_Snapshot.I18NCurrent != null && m_Snapshot.I18NCurrent.TryGetValue(key, out value))
            {
                return true;
            }
            return m_Snapshot.Active.TryGetValue(key, out value);
        }

        private static readonly Regex s_AssetKey = new Regex(@"^Assets\.[A-Z_]+\[(?<name>.+)\]$", RegexOptions.Compiled);
        private static readonly Regex s_ZoneKey = new Regex(@"^Assets\.NAME\[(?<name>(?:[A-Z]{2,4}|CCP\d+) [A-Za-z ]+)\]$", RegexOptions.Compiled);

        /// <summary>
        /// 共用来源里的条目按资产归属拆分：Assets.NAME[预制件] 等能找到所属 mod 的分给该 mod，其余按 key 推断。
        /// </summary>
        private void MergeShared(ScanResult result, Dictionary<string, string> entries, string fallbackName)
        {
            var owners = PrefabOwners();
            var byOwner = new Dictionary<string, (Owner owner, Dictionary<string, string> entries)>(StringComparer.Ordinal);
            var rest = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in entries)
            {
                Match m = s_AssetKey.Match(kv.Key);
                if (m.Success && owners.TryGetValue(m.Groups["name"].Value, out var owner))
                {
                    if (!byOwner.TryGetValue(owner.Key, out var group))
                    {
                        byOwner[owner.Key] = group = (owner, new Dictionary<string, string>(StringComparer.Ordinal));
                    }
                    group.entries[kv.Key] = kv.Value;
                }
                else
                {
                    rest[kv.Key] = kv.Value;
                }
            }
            foreach (var group in byOwner.Values)
            {
                Merge(result, group.owner, group.entries, overwrite: true);
            }
            if (rest.Count > 0)
            {
                Merge(result, OwnerByKeys(rest.Keys, fallbackName), rest, overwrite: true);
            }
        }

        /// <summary>MemorySource 等通用类型无法从类型推断归属，按 key 中最常见的程序集名前缀推断。</summary>
        private Owner OwnerByKeys(ICollection<string> keys, string fallbackName)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in keys.Take(200))
            {
                Match m = s_KeyOwner.Match(key);
                if (m.Success)
                {
                    // "Xxx.key" 形式只在 Xxx 确实是已加载的程序集名时才算（排除 Assets.、Options. 这类游戏通用前缀）。
                    string name = m.Groups[1].Success ? m.Groups[1].Value
                        : m_AssembliesByName.ContainsKey(m.Groups[2].Value) ? m.Groups[2].Value : null;
                    if (name == null)
                    {
                        continue;
                    }
                    counts.TryGetValue(name, out int n);
                    counts[name] = n + 1;
                }
            }
            foreach (var candidate in counts.OrderByDescending(p => p.Value))
            {
                if (m_AssembliesByName.TryGetValue(candidate.Key, out var asset))
                {
                    return OwnerByAsset(asset);
                }
            }
            string guess = counts.OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault() ?? fallbackName;
            return new Owner { Key = "src_" + guess, Name = guess };
        }

        private static bool IsGameAssembly(Assembly asm)
        {
            string name = asm.GetName().Name;
            return name == "Game" || name.StartsWith("Colossal.") || name.StartsWith("Unity") || name == "mscorlib" || name.StartsWith("System");
        }

        /// <summary>分析快照（后台线程）。只读取 <paramref name="store"/>，不写入。</summary>
        public ScanResult Scan(TranslationStore store, I18NBridge i18n)
        {
            var result = new ScanResult
            {
                Locale = m_Snapshot.Locale,
                FallbackLocale = m_Snapshot.FallbackLocale,
                Time = DateTime.Now,
            };
            Assembly self = typeof(ModScanner).Assembly;
            string i18nName = i18n.Assembly?.GetName().Name;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var phases = new List<string>();
            void Phase(string name)
            {
                phases.Add(name + " " + timer.ElapsedMilliseconds + "ms");
                timer.Restart();
            }

            // 1. 游戏本地化管理器里注册在英文下的源。
            foreach (var source in m_Snapshot.Sources)
            {
                Owner owner;
                if (source.IsLocaleAsset)
                {
                    owner = OwnerByPath(source.AssetPath) ?? new Owner { Key = "asset_" + source.AssetName, Name = source.AssetName };
                }
                else
                {
                    Assembly asm = source.Assembly;
                    if (i18nName != null && asm.GetName().Name == i18nName)
                    {
                        continue; // I18N 自身界面
                    }
                    if (asm == self)
                    {
                        owner = new Owner { Key = kSelfKey, Name = Mod.DisplayName };
                    }
                    else if (m_Assemblies.TryGetValue(asm, out var exe))
                    {
                        owner = OwnerByAsset(exe);
                    }
                    else if (IsGameAssembly(asm))
                    {
                        owner = null; // 稍后读完 key 再判断
                    }
                    else
                    {
                        string loc = null;
                        try
                        {
                            loc = Path.GetDirectoryName(asm.Location);
                        }
                        catch (Exception)
                        {
                            // 动态程序集没有路径。
                        }
                        owner = OwnerByPath(loc) ?? new Owner { Key = "asm_" + asm.GetName().Name, Name = asm.GetName().Name };
                    }
                }

                Dictionary<string, string> entries = source.Entries;
                if (owner == null)
                {
                    // 共用来源（MemorySource 等）：按资产归属拆分给真正的 mod。
                    MergeShared(result, entries, source.TypeName);
                    continue;
                }
                if (owner.Key == kSelfKey)
                {
                    // 本 mod 的名字 "AI Translate!" 是品牌名，不翻译；只有设置页标题和其他 mod 一样加译名。（复制一份，快照可能被分析多次。）
                    entries = entries.Where(e => e.Value != Mod.DisplayName || IsModTitleKey(e.Key)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
                }
                Merge(result, owner, entries, overwrite: true);
            }

            Phase("sources");
            // 2. 依靠 I18N Everywhere 的 mod/lang/en-US.json 提供文本的 mod。
            foreach (var kv in m_Snapshot.I18NCachedMods)
            {
                string file = Path.Combine(kv.Value, result.FallbackLocale + ".json");
                if (!File.Exists(file))
                {
                    continue;
                }
                try
                {
                    var entries = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file));
                    if (entries == null || entries.Count == 0)
                    {
                        continue;
                    }
                    Owner owner = OwnerByPath(Path.GetDirectoryName(kv.Value)) ?? new Owner { Key = "lang_" + kv.Key, Name = kv.Key };
                    if (IsPrivateLangFile(result, owner, Path.GetDirectoryName(kv.Value), entries))
                    {
                        // 不是 I18N 格式：mod 自己读取这个文件，并以 "程序集名.key" 注册到游戏里（已在上面扫描到）。
                        Mod.Log.Debug("Skip private language file " + file);
                        continue;
                    }
                    Merge(result, owner, entries, overwrite: false);
                }
                catch (Exception ex)
                {
                    Mod.Log.Warn("Read " + file + " failed: " + ex.Message);
                }
            }

            // 3. 根本没有名称条目的资产：游戏会直接显示内部预制件名，这里补上可翻译的名称。
            Phase("lang files");
            result.UnnamedAssets = CollectUnnamedAssets(result, store);
            Phase("assets");
            AssignCategories(result);
            result.ProtectedNames = BuildProtectedNames(result);   // 需要先知道哪些是代码 mod
            CollectGlossary(result);   // 先取得原版官方术语，分类时要用
            Phase("glossary");
            Classify(result, store, i18n);
            Phase("classify");
            CollectMixedNative(result, store, i18n);
            Phase("official");
            Mod.Log.Info("Scan phases: " + string.Join(", ", phases));
            return result;
        }

        /// <summary>
        /// 判断 lang/en-US.json 是否是 mod 的私有资源：其中大多数 key 加上该 mod 某个程序集名前缀后，
        /// 已经出现在游戏的本地化源里。
        /// </summary>
        private static bool IsPrivateLangFile(ScanResult result, Owner owner, string modDir, Dictionary<string, string> entries)
        {
            if (!result.Mods.TryGetValue(owner.Key, out var info) || info.Source.Count == 0 || string.IsNullOrEmpty(modDir))
            {
                return false;
            }
            List<string> prefixes;
            try
            {
                prefixes = Directory.GetFiles(modDir, "*.dll")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !n.StartsWith("0Harmony", StringComparison.OrdinalIgnoreCase))
                    .Select(n => n + ".")
                    .ToList();
            }
            catch (Exception)
            {
                return false;
            }
            var sample = entries.Keys.Take(200).ToList();
            foreach (string prefix in prefixes)
            {
                int hits = sample.Count(k => info.Source.ContainsKey(prefix + k));
                if (hits * 2 >= sample.Count)
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly Regex s_NameSplit = new Regex(
            @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=\d)|(?<=\d)(?=[A-Za-z])",
            RegexOptions.Compiled);

        /// <summary>把内部预制件名拆成可读文字，如 "CSBankTowerTall_01" → "CS Bank Tower Tall 01"。</summary>
        internal static string HumanizePrefabName(string name)
        {
            var template = MatchBuildingTemplate(name);
            if (template != null)
            {
                return template.Value.phrase + template.Value.suffix;
            }
            string s = (name ?? string.Empty).Replace('_', ' ').Replace('-', ' ');
            s = s_NameSplit.Replace(s, " ");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        /// <summary>游戏本体与 DLC 的文本归在这个分组下。</summary>
        internal const string kGameKey = "game_vanilla";

        /// <summary>
        /// 找出玩家实际看到的、官方或 mod 自带却中英混杂的译文（原版、DLC、mod 自带、I18N 官方库都算），
        /// 已经由 AI 判断过且译文没变的跳过。只处理中日韩目标语言。
        /// </summary>
        private void CollectMixedNative(ScanResult result, TranslationStore store, I18NBridge i18n)
        {
            if (string.Equals(result.Locale, result.FallbackLocale, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            string lang = LanguageDetector.LanguageOf(result.Locale);
            bool cjk = lang == "zh" || lang == "ja" || lang == "ko";
            var fallback = m_Snapshot.Fallback;
            var active = m_Snapshot.Active;
            var ours = store.GetOwnTranslations(result.Locale);
            var i18nDict = m_Snapshot.I18NCurrent;
            // mod 自带 lang 文件、I18N 翻译库、其他语言包的译文（不含本 mod 的语言包）。
            var others = i18n.CollectOtherTranslations(result.Locale, result.FallbackLocale, store.LocalizationDir, ours);

            // 某条文本现在的官方/自带译文。不读 I18N 的运行时词典：那里混着本 mod 注入的（可能是过时的）改写。
            // ownValue：本 mod 对这条的记录值，游戏词典里等于它说明是本 mod 写进去的（没有 I18N 时），不算原译文。
            string NativeText(string key, string ownValue)
            {
                if (others.TryGetValue(key, out var other) && other != ownValue)
                {
                    return other;
                }
                if (active.TryGetRaw(key, out var raw, out bool isFallback) && !isFallback && raw != ownValue)
                {
                    return raw;
                }
                return null;
            }
            var brands = BrandNames();
            var gameSaved = store.Get(result.Locale, kGameKey);

            // key → 所属 mod（不在任何 mod 文本里的就是游戏本体/DLC）。
            var keyOwner = new Dictionary<string, ModTextInfo>(StringComparer.Ordinal);
            foreach (var info in result.Mods.Values)
            {
                foreach (string key in info.Source.Keys)
                {
                    keyOwner[key] = info;
                }
            }
            ModTextInfo Game()
            {
                if (!result.Mods.TryGetValue(kGameKey, out var game))
                {
                    game = new ModTextInfo { Key = kGameKey, DisplayName = "Cities: Skylines II", Category = Setting.ModCategory.Other };
                    result.Mods[kGameKey] = game;
                }
                return game;
            }

            foreach (var kv in fallback)
            {
                string key = kv.Key;
                string english = kv.Value;
                if (string.IsNullOrEmpty(english))
                {
                    continue;
                }
                bool modOwned = keyOwner.ContainsKey(key);

                // 1. 游戏本体/DLC 的官方译文直接照搬了英文（如 "EU Low Wavy Concrete Fence"）：当成未翻译。
                //    mod 自带的同类情况在 Classify 里处理。虚构品牌名（BrandPrefab）保持原样。
                //    只处理资产名称和描述：界面上的格式串（日期 "yyyy-MM-dd"、单位 "kB"）照搬英文是故意的，翻了会出错。
                if (!modOwned && key.StartsWith("Assets.", StringComparison.Ordinal) && english.Trim().IndexOf(' ') > 0
                    && active.TryGetRaw(key, out var official, out bool officialIsFallback) && !officialIsFallback
                    && official == english && TextRules.IsTranslatable(english, result.Locale) && !IsBrandKey(key, brands)
                    && !(i18nDict != null && i18nDict.TryGetValue(key, out var fromLibrary) && fromLibrary != english && !(ours.TryGetValue(key, out var o1) && o1 == fromLibrary)))
                {
                    var game = Game();
                    game.Source[key] = english;
                    game.Translatable++;
                    game.OwnKeys.Add(key);
                    if (gameSaved != null && gameSaved.Translations.ContainsKey(key) && gameSaved.Sources.TryGetValue(key, out var savedSource) && savedSource == english)
                    {
                        game.Done++;
                    }
                    else
                    {
                        game.Pending.Add(key);
                    }
                    continue;
                }

                // 游戏更新后官方补上了真正的译文：删掉之前替它翻的，让官方译文生效。
                if (!modOwned && gameSaved != null && gameSaved.Translations.ContainsKey(key)
                    && active.TryGetRaw(key, out var nowOfficial, out bool nowIsFallback) && !nowIsFallback && nowOfficial != english
                    && nowOfficial != gameSaved.Translations[key] && !(ours.TryGetValue(key, out var injected) && injected == nowOfficial))   // 不是本 mod 写进去的
                {
                    Game().Stale.Add(key);
                }

                // 2. 中日韩：官方/自带译文里夹着照搬的英文单词。
                //    本 mod 自己的界面文字是手写的（故意写 “mod” 等），不当作混杂。
                if (!cjk || !m_Snapshot.Settled || ours.ContainsKey(key) || IsModTitleKey(key) || (modOwned && keyOwner[key].Key == kSelfKey))
                {
                    continue;
                }
                // 检查过的（改写过或判断为不用改）都有记录，一律不再触发；原译文变了的记录由下面第 3 步处理。
                string ownerKey = modOwned ? keyOwner[key].Key : kGameKey;
                if (store.Get(result.Locale, TranslationStore.FixGroup(ownerKey))?.Sources.ContainsKey(key) == true)
                {
                    continue;
                }
                string current = NativeText(key, null);
                string ownerName = modOwned ? keyOwner[key].DisplayName : null;
                if (current == null || current == english || !QualityChecker.LooksMixed(english, current, result.Locale, result.ProtectedNames, ownerName))
                {
                    continue;
                }
                var owner = modOwned ? keyOwner[key] : Game();
                owner.MixedNative.Add((key, english, current));
            }

            if (!m_Snapshot.Settled)
            {
                return; // 启动早期 I18N 还没加载完，读到的“原译文”不可靠
            }

            // 3. 之前的修正已作废：被修正的那条官方/自带译文后来变了（作者或官方更新了译文），或者属于本 mod 自己的界面。
            //    不删的话，旧修正会一直盖住新的译文。
            foreach (var info in result.Mods.Values.ToList())
            {
                var fixes = store.Get(result.Locale, TranslationStore.FixGroup(info.Key));
                if (fixes == null)
                {
                    continue;
                }
                foreach (var kv in fixes.Sources)
                {
                    if (info.Key == kSelfKey)
                    {
                        info.StaleFixes.Add(kv.Key);
                        continue;
                    }
                    fixes.Translations.TryGetValue(kv.Key, out var fixedText);
                    string native = NativeText(kv.Key, fixedText);
                    if (native != null && native != kv.Value)
                    {
                        if (fixedText == kv.Value)
                        {
                            // “不用改”的记录：改记到现在的译文上，仍算已检查，不再触发。
                            info.RemarkFixes[kv.Key] = native;
                        }
                        else
                        {
                            // 真正的改写：原译文变了就作废，否则旧改写会一直盖住新译文。
                            info.StaleFixes.Add(kv.Key);
                        }
                        continue;
                    }
                    // 改写并没有减少照搬的英文（只是换了个说法，甚至改坏了专有名词）：撤回，恢复原译文。
                    if (fixedText != null && fixedText != kv.Value && fallback.TryGetValue(kv.Key, out var english)
                        && QualityChecker.LeftoverEnglish(english, fixedText, result.Locale, result.ProtectedNames, info.DisplayName)
                            >= QualityChecker.LeftoverEnglish(english, kv.Value, result.Locale, result.ProtectedNames, info.DisplayName))
                    {
                        info.RevertFixes.Add(kv.Key);
                    }
                }
            }
        }

        private HashSet<string> m_BrandNames;

        /// <summary>游戏里的品牌（BrandPrefab）名称：都是虚构品牌，官方中文也保留英文，不翻译。</summary>
        private HashSet<string> BrandNames()
        {
            if (m_BrandNames != null)
            {
                return m_BrandNames;
            }
            m_BrandNames = new HashSet<string>(m_Snapshot.Prefabs.Where(p => p.IsBrand).Select(p => p.Name), StringComparer.Ordinal);
            return m_BrandNames;
        }

        private static bool IsBrandKey(string key, HashSet<string> brands)
        {
            Match m = s_AssetKey.Match(key);
            return m.Success && brands.Contains(m.Groups["name"].Value);
        }

        /// <summary>收集所有已安装 mod 的名称（Paradox Mods 显示名、本地 mod 文件夹名、各 mod 设置页标题）。</summary>
        private ProtectedNames BuildProtectedNames(ScanResult result)
        {
            // 只保护代码/工具类 mod 的名字。资产包的名字常常就是资产本身的名字（如 "Union Station"），那些应该翻译。
            var names = new List<string>();
            foreach (var info in result.Mods.Values.Where(m => m.HasCode))
            {
                names.Add(info.DisplayName);
                foreach (var kv in info.Source)
                {
                    if (kv.Key.StartsWith("Options.SECTION[", StringComparison.Ordinal))
                    {
                        names.Add(kv.Value);
                    }
                }
            }
            return new ProtectedNames(names);
        }

        // ---------------------------------------------------------------- 内部名解析

        // 区域包/原版使用的地区代码。
        private static readonly HashSet<string> s_Regions = new HashSet<string>(StringComparer.Ordinal)
        {
            "EU", "NA", "CN", "JP", "EE", "UK", "FR", "DE", "NL", "USNE", "USSW",
        };

        // 内部标记：不是名字的一部分，翻译时去掉（后面紧跟的编号也一起去掉）。
        private static readonly HashSet<string> s_Markers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Bonus", "Extra", "RP", "Req", "ProcReq", "CitizenReq", "Proc", "Details", "Random",
        };

        // 地区代码，或官方创作者包代码（CCP5 = 地中海遗产）。
        private static readonly Regex s_InternalPrefix = new Regex(@"^(?<region>[A-Z]{2,4}|CCP\d+)_(?<rest>.+)$", RegexOptions.Compiled);
        private static readonly Regex s_AuthorTag = new Regex(@"\s*(?<tag>\([^()]*\))\s*$", RegexOptions.Compiled);
        // _L1_2x2 / L1x2x2（东欧包把等级和占地连在一起）
        private static readonly Regex s_LevelLot = new Regex(@"_?L(?<lvl>[1-5])(?:_|x)(?<w>\d+)x(?<d>\d+)", RegexOptions.Compiled);
        private static readonly Regex s_Lot = new Regex(@"(?<=_|^)(?<w>\d+)x(?<d>\d+)(?=_|$)", RegexOptions.Compiled);

        /// <summary>
        /// 解析区域包/原版风格的内部名，拆成「类型短语」+「编号/等级/占地/作者标签后缀」：
        ///   EU_ResidentialLow01_L1_2x2            → ("EU Residential Low", " 01 - L1 2x2")（与原版官方格式一致）
        ///   CN_BonusResidentialHigh01a_L5_3x2     → ("CN Bonus Residential High", " 01a - L5 3x2")
        ///   EE_ResidentialMedium01CornerL3x2x2Details → ("EE Residential Medium Corner", " 01 - L3 2x2")
        ///   CN_FinancialCenter Req01              → ("CN Financial Center", "")
        ///   JP_CommercialStorefront01_1x1 (Styx's) → ("JP Commercial Storefront", " 01 1x1 (Styx's)")
        /// 同一类型短语只翻译一次，再拼回后缀，保证同类资产叫法一致。不是这种格式时返回 null。
        /// </summary>
        internal static (string phrase, string suffix)? MatchBuildingTemplate(string internalName)
        {
            string s = (internalName ?? string.Empty).Trim();
            string tag = string.Empty;
            Match tagMatch = s_AuthorTag.Match(s);
            if (tagMatch.Success)
            {
                tag = " " + tagMatch.Groups["tag"].Value;
                s = s.Substring(0, tagMatch.Index);
            }
            Match prefix = s_InternalPrefix.Match(s);
            if (!prefix.Success || !(s_Regions.Contains(prefix.Groups["region"].Value) || prefix.Groups["region"].Value.StartsWith("CCP", StringComparison.Ordinal)))
            {
                return null;
            }
            string region = prefix.Groups["region"].Value;
            string rest = prefix.Groups["rest"].Value;

            string level = null, lot = null;
            Match ll = s_LevelLot.Match(rest);
            if (ll.Success)
            {
                level = ll.Groups["lvl"].Value;
                lot = ll.Groups["w"].Value + "x" + ll.Groups["d"].Value;
                rest = rest.Remove(ll.Index, ll.Length);
            }
            else
            {
                Match lotMatch = s_Lot.Match(rest);
                if (lotMatch.Success)
                {
                    lot = lotMatch.Groups["w"].Value + "x" + lotMatch.Groups["d"].Value;
                    rest = rest.Remove(lotMatch.Index, lotMatch.Length);
                }
            }

            // 拆成单词与数字：驼峰、下划线、空格、字母/数字边界。
            var tokens = Regex.Replace(s_NameSplit.Replace(rest.Replace('_', ' ').Replace('-', ' '), " "), @"\s+", " ")
                .Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var words = new List<string>();
            string number = null;
            string variant = null;
            for (int i = 0; i < tokens.Count; i++)
            {
                string t = tokens[i];
                if (s_Markers.Contains(t))
                {
                    // 标记后面紧跟的编号（Req01）属于标记本身。
                    if (i + 1 < tokens.Count && tokens[i + 1].All(char.IsDigit))
                    {
                        i++;
                    }
                    continue;
                }
                if (t.All(char.IsDigit))
                {
                    if (number == null)
                    {
                        number = t;
                        // 编号后的单个小写字母是变体（01a）。
                        if (i + 1 < tokens.Count && tokens[i + 1].Length == 1 && char.IsLower(tokens[i + 1][0]))
                        {
                            number += tokens[++i];
                        }
                    }
                    continue;
                }
                // 单个大写字母（Office High B01 的 B）是变体编号，放进后缀。
                if (t.Length == 1 && char.IsUpper(t[0]) && words.Count > 0)
                {
                    variant = t;
                    continue;
                }
                words.Add(t);
            }
            if (words.Count == 0)
            {
                return null;
            }

            string phrase = region + " " + string.Join(" ", words);
            if (variant != null)
            {
                number = variant + (number ?? string.Empty);
            }
            string suffix = (number != null ? " " + number : string.Empty)
                + (level != null ? " - L" + level + " " + lot : lot != null ? " " + lot : string.Empty)
                + tag;
            return (phrase, suffix);
        }

        /// <summary>
        /// 这条名称原文是否由内部名拼出（本 mod 生成的，或作者直接把内部名当成了显示名），
        /// 是的话返回解析结果，按“类型短语 + 后缀”统一翻译。
        /// </summary>
        internal static (string phrase, string suffix)? TemplateForEntry(string key, string source)
        {
            foreach (string prefix in new[] { "Assets.NAME[", "Assets.UPGRADE_NAME[" })
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal) || !key.EndsWith("]", StringComparison.Ordinal))
                {
                    continue;
                }
                string prefabName = key.Substring(prefix.Length, key.Length - prefix.Length - 1);
                var template = MatchBuildingTemplate(prefabName);
                if (template == null)
                {
                    return null;
                }
                string trimmed = (source ?? string.Empty).Trim();
                if (trimmed == template.Value.phrase + template.Value.suffix || trimmed == prefabName
                    || trimmed == HumanizeLegacy(prefabName))
                {
                    return template;
                }
                // 作者写的名字也是同一个内部名（只是空格/下划线不同），同样按模板处理。
                var fromSource = MatchBuildingTemplate(trimmed.Replace(' ', '_'));
                return fromSource != null && fromSource.Value.phrase == template.Value.phrase ? template : null;
            }
            return null;
        }

        /// <summary>这条资产名原文是不是本 mod 用内部名拼出来的（而不是作者或导入工具提供的）。</summary>
        internal static bool IsGeneratedName(string prefabName, string source) =>
            source == HumanizePrefabName(prefabName) || source == HumanizeLegacy(prefabName);

        /// <summary>早期版本的拆分方式（"CN Residential Low 03 L 2 2 x 4"），用于识别旧原文。</summary>
        private static string HumanizeLegacy(string name)
        {
            string s = (name ?? string.Empty).Replace('_', ' ').Replace('-', ' ');
            s = s_NameSplit.Replace(s, " ");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        /// <summary>
        /// 从游戏原版的英文/当前语言词典中提取区域建筑的官方类型译名，例如
        /// "Residential Low" → "低层住宅"、"EU" → "欧洲"（比较同类型的 EU 与 NA 两个版本得出地区词和类型词）。
        /// </summary>
        private void CollectOfficialBuildingTerms(ScanResult result)
        {
            var active = m_Snapshot.Active;
            ExtractOfficialBuildingTerms(
                m_Snapshot.Fallback,
                key => active.TryGetRaw(key, out var value, out bool isFallback) && !isFallback ? value : null,
                result.OfficialBuildingPhrases, result.OfficialBuildingTypes, result.OfficialRegions);
        }

        /// <param name="translated">key → 当前语言的官方译文（没有时返回 null）。</param>
        internal static void ExtractOfficialBuildingTerms(
            IEnumerable<KeyValuePair<string, string>> english,
            Func<string, string> translated,
            Dictionary<string, string> phrases,
            Dictionary<string, string> types,
            Dictionary<string, string> regions)
        {
            // 类型 → 地区 → 当前语言的“地区+类型”短语
            var byType = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var kv in english)
            {
                if (!kv.Key.StartsWith("Assets.NAME[", StringComparison.Ordinal) || !kv.Key.EndsWith("]", StringComparison.Ordinal))
                {
                    continue;
                }
                string prefabName = kv.Key.Substring("Assets.NAME[".Length, kv.Key.Length - "Assets.NAME[".Length - 1);
                var template = MatchBuildingTemplate(prefabName);
                // 只用带等级的分区建筑名（"… 01 - L1 2x2"）推导官方术语，交通灯、栅栏等其他原版内部名会推出错误的词。
                string target = template == null || template.Value.suffix.IndexOf(" - L", StringComparison.Ordinal) < 0 ? null : translated(kv.Key);
                if (target == null)
                {
                    continue;
                }
                string suffix = template.Value.suffix.TrimStart();
                int at = target.LastIndexOf(suffix, StringComparison.Ordinal);
                if (at <= 0)
                {
                    continue; // 官方译名不是这个格式，跳过
                }
                string translatedPhrase = target.Substring(0, at).Trim();
                string phrase = template.Value.phrase;
                int space = phrase.IndexOf(' ');
                string region = phrase.Substring(0, space);
                string type = phrase.Substring(space + 1);
                if (!byType.TryGetValue(type, out var regionPhrases))
                {
                    byType[type] = regionPhrases = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                regionPhrases[region] = translatedPhrase;
                phrases[phrase] = translatedPhrase;
            }

            foreach (var type in byType)
            {
                if (type.Value.Count < 2)
                {
                    continue;
                }
                var values = type.Value.Values.ToList();
                string common = values[0];
                foreach (string v in values.Skip(1))
                {
                    int n = 0;
                    while (n < common.Length && n < v.Length && common[common.Length - 1 - n] == v[v.Length - 1 - n])
                    {
                        n++;
                    }
                    common = common.Substring(common.Length - n);
                }
                common = common.Trim();
                if (common.Length == 0)
                {
                    continue;
                }
                types[type.Key] = common;
                foreach (var region in type.Value)
                {
                    string regionWord = region.Value.Substring(0, region.Value.Length - common.Length).Trim();
                    if (regionWord.Length > 0)
                    {
                        regions[region.Key] = regionWord;
                    }
                }
            }
        }

        private int CollectUnnamedAssets(ScanResult result, TranslationStore store)
        {
            var active = m_Snapshot.Active;
            var fallback = m_Snapshot.Fallback;
            var i18nDict = m_Snapshot.I18NCurrent;
            var ours = store.GetAllTranslations(result.Locale);
            var groups = store.TranslationGroups(result.Locale);
            var rbNames = m_Snapshot.RoadBuilderNames;
            bool groupsHave(string key) => groups.TryGetValue(key, out var g) && g != TranslationStore.FileNameFor(kGameKey);

            // 某个 key 是否已有“别人提供的”文本（游戏/mod 自带、I18N 或其他语言包，不算本 mod 写入的）。
            bool HasForeign(string key)
            {
                bool mine(string value) => ours.TryGetValue(key, out var o) && o == value;
                return (active.TryGetRaw(key, out var raw, out _) && !mine(raw))
                    || (i18nDict != null && i18nDict.TryGetValue(key, out var other) && !mine(other));
            }

            // 官方文本只有英文（当前语言缺这条，游戏回退显示英文）。
            bool EnglishOnly(string key, out string english) =>
                fallback.TryGetValue(key, out english) && !string.IsNullOrWhiteSpace(english)
                && !(active.TryGetRaw(key, out _, out bool isFallback) && !isFallback);

            int count = 0;
            foreach (var prefab in m_Snapshot.Prefabs)
            {
                Owner owner;
                bool templatedBuilding = false;
                if (prefab.IsBuiltin)
                {
                    // 游戏本体/DLC：只给按区域包命名、却没有任何名称的可成长建筑补名字（如地中海遗产包的 CCP5_ResidentialMediumLeft01_L5_3x2）。
                    // 这类名字统一归在游戏分组，按官方建筑命名格式翻译。
                    string builtinTitle = "Assets.NAME[" + prefab.Name + "]";
                    templatedBuilding = prefab.IsSpawnableBuilding && MatchBuildingTemplate(prefab.Name) != null;
                    if (templatedBuilding && !HasForeign(builtinTitle) && !groupsHave(builtinTitle))
                    {
                        Merge(result, new Owner { Key = kGameKey, Name = "Cities: Skylines II" },
                            new Dictionary<string, string> { [builtinTitle] = HumanizePrefabName(prefab.Name) }, overwrite: false);
                        count++;
                    }
                    // 其余原版资产不处理；开启「包含 DLC 和创作者包」时，DLC/创作者包的资产按包分组，和 mod 资产一样补名称和描述。
                    if (prefab.ContentPack == null)
                    {
                        continue;
                    }
                    owner = ContentPackOwner(prefab.ContentPack);
                }
                else
                {
                    owner = OwnerByPath(prefab.AssetPath);
                }
                CountPrefab(prefab, owner);
                if (!prefab.IsCandidate)
                {
                    continue;
                }
                string titleKey = (prefab.IsUpgrade ? "Assets.UPGRADE_NAME[" : "Assets.NAME[") + prefab.Name + "]";
                string descKey = (prefab.IsUpgrade ? "Assets.UPGRADE_DESCRIPTION[" : "Assets.DESCRIPTION[") + prefab.Name + "]";
                owner = owner ?? new Owner { Key = "assets_unnamed", Name = "Unnamed assets" };

                // DLC/创作者包里官方只有英文的名称和描述：作为这个包的文本翻译（mod 的这类文本来自它自己的语言文件）。
                if (prefab.ContentPack != null)
                {
                    var englishOnly = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (string key in new[] { titleKey, descKey })
                    {
                        if (EnglishOnly(key, out var english))
                        {
                            englishOnly[key] = english;
                        }
                    }
                    Merge(result, owner, englishOnly, overwrite: false);
                }

                // Road Builder 生成的道路：prefab 名是 GUID-SteamID，IsMachineIdentifier=true。
                // 从 snapshot 里查用户起的名字，归到专属分组，避免落入 "assets_unnamed"。
                rbNames.TryGetValue(prefab.Name, out var rbDisplayName);
                bool isRbRoad = rbDisplayName != null;
                if (isRbRoad)
                {
                    owner = new Owner { Key = kRoadBuilderKey, Name = kRoadBuilderDisplayName };
                }

                // 名称：游戏词典（含回退英文）或其他来源都没有时，用内部名补一个可翻译的名称。
                // 内部 ID（GUID、哈希、Steam 用户号）不是名字，拆开也没有意义，跳过。
                // 已在别的分组里有译文的（例如上次由资产导入工具提供了名字），不再另起名字重复翻译。
                bool translatedElsewhere = groups.TryGetValue(titleKey, out var titleGroup) && titleGroup != TranslationStore.FileNameFor(owner.Key);
                if (!HasForeign(titleKey) && !translatedElsewhere && !templatedBuilding)
                {
                    if (isRbRoad)
                    {
                        // RB 道路：把用户起的名字（英文）注册为可翻译的名称。
                        Merge(result, owner, new Dictionary<string, string> { [titleKey] = rbDisplayName }, overwrite: false);
                        count++;
                    }
                    else if (!TextRules.IsMachineIdentifier(prefab.Name))
                    {
                        Merge(result, owner, new Dictionary<string, string> { [titleKey] = HumanizePrefabName(prefab.Name) }, overwrite: false);
                        count++;
                    }
                }

                // 描述：只处理出现在建造菜单里的资产（玩家会在提示框里看到描述）。
                if (!prefab.HasUIObject || prefab.Context == null)
                {
                    continue;
                }
                // 只有随机编号作名字、且还没起过名的资产：起名（没有描述时顺带写描述）。
                // Road Builder 道路也算：用户的名字是英文，需要翻译+写描述。
                bool needsName = !HasForeign(titleKey) && !ours.ContainsKey(titleKey)
                    && (isRbRoad || TextRules.IsMachineIdentifier(prefab.Name));
                bool hasOwnDescription = HasForeign(descKey);
                if (hasOwnDescription && !needsName)
                {
                    if (store.Get(result.Locale, TranslationStore.DescriptionGroup(owner.Key))?.Translations.ContainsKey(descKey) == true)
                    {
                        Merge(result, owner, new Dictionary<string, string>(), overwrite: false);
                        result.Mods[owner.Key].StaleDescriptions.Add(descKey);
                    }
                    continue;
                }
                Merge(result, owner, new Dictionary<string, string>(), overwrite: false);
                var info = result.Mods[owner.Key];
                if (ours.ContainsKey(descKey) && !needsName)
                {
                    info.GeneratedDescriptions++;
                    continue;
                }
                // 快照可能被分析多次，每次都把字段完整赋值一遍。
                var context = prefab.Context;
                context.Title = TryGetDisplay(titleKey, out var t) ? t : HumanizePrefabName(prefab.Name);
                context.EnglishTitle = fallback.TryGetValue(titleKey, out var en) ? en : null;
                context.NeedsName = needsName;
                context.OwnDescription = hasOwnDescription && TryGetDisplay(descKey, out var ownDescription) ? ownDescription : null;
                info.MissingDescriptions.Add(context);
                if (needsName)
                {
                    info.MissingNames++;
                }
            }
            return count;
        }

        // ---------------------------------------------------------------- 分类

        // 所属 mod 键 → 各类预制件数量。
        private readonly Dictionary<string, Dictionary<Setting.ModCategory, int>> m_PrefabCounts = new Dictionary<string, Dictionary<Setting.ModCategory, int>>(StringComparer.Ordinal);

        // 含代码的 mod 键。
        private readonly HashSet<string> m_CodeOwners = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>按预制件类型归类：建筑 / 车辆 / 道路轨道 / 植被 / 道具 / 地表区域。其他类型返回 null。必须在主线程调用。</summary>
        internal static Setting.ModCategory? CategoryOf(PrefabBase prefab)
        {
            switch (prefab)
            {
                case BuildingPrefab _:
                case BuildingExtensionPrefab _:
                    return Setting.ModCategory.Building;
                case VehiclePrefab _:
                    return Setting.ModCategory.Vehicle;
                case NetPrefab _:
                case NetLanePrefab _:
                    return Setting.ModCategory.Network;
                case AreaPrefab _:
                    return Setting.ModCategory.Surface;
                case StaticObjectPrefab _:
                    return prefab.Has<TreeObject>() || prefab.Has<PlantObject>() ? Setting.ModCategory.Vegetation : Setting.ModCategory.Prop;
                default:
                    return null;
            }
        }

        private void CountPrefab(PrefabSnapshot prefab, Owner owner)
        {
            if (prefab.Category == null || owner == null)
            {
                return;
            }
            if (!m_PrefabCounts.TryGetValue(owner.Key, out var counts))
            {
                m_PrefabCounts[owner.Key] = counts = new Dictionary<Setting.ModCategory, int>();
            }
            counts.TryGetValue(prefab.Category.Value, out int n);
            counts[prefab.Category.Value] = n + 1;
        }

        /// <summary>
        /// 定 mod 类型：含代码的是「玩法/工具 MOD」；否则按预制件数量最多的类型，
        /// 第二多的类型也占到 1/3 以上时算「综合资产包」；什么都没有的是「其他」。
        /// </summary>
        private void AssignCategories(ScanResult result)
        {
            foreach (var info in result.Mods.Values)
            {
                info.HasCode = info.Key == kSelfKey || m_CodeOwners.Contains(info.Key)
                    || info.Source.Keys.Any(k => k.StartsWith("Options.SECTION[", StringComparison.Ordinal));
                if (m_PrefabCounts.TryGetValue(info.Key, out var counts))
                {
                    foreach (var kv in counts)
                    {
                        info.PrefabCounts[kv.Key] = kv.Value;
                    }
                }
                if (info.HasCode)
                {
                    info.Category = Setting.ModCategory.Gameplay;
                    continue;
                }
                var ordered = info.PrefabCounts.OrderByDescending(p => p.Value).ToList();
                if (ordered.Count == 0)
                {
                    info.Category = Setting.ModCategory.Other;
                }
                else if (ordered.Count > 1 && ordered[1].Value * 3 >= ordered.Sum(p => p.Value))
                {
                    info.Category = Setting.ModCategory.Mixed;
                }
                else
                {
                    info.Category = ordered[0].Key;
                }
            }
        }

        private static void Merge(ScanResult result, Owner owner, Dictionary<string, string> entries, bool overwrite)
        {
            if (!result.Mods.TryGetValue(owner.Key, out var info))
            {
                info = new ModTextInfo { Key = owner.Key, DisplayName = owner.Name };
                result.Mods[owner.Key] = info;
            }
            foreach (var kv in entries)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Value == null)
                {
                    continue;
                }
                if (overwrite || !info.Source.ContainsKey(kv.Key))
                {
                    info.Source[kv.Key] = kv.Value;
                }
            }
        }

        private void Classify(ScanResult result, TranslationStore store, I18NBridge i18n)
        {
            string locale = result.Locale;
            // 游戏语言就是回退语言（英语）时，翻译的是非英文写成的 mod 文本（如中文作者的 mod）。
            bool fallbackTarget = string.Equals(locale, result.FallbackLocale, StringComparison.OrdinalIgnoreCase);
            var active = m_Snapshot.Active;
            // 只看本 mod 自己的译文；对官方译文的修正（fix_）不能让官方译文被当成“没有译文”。
            var ours = store.GetOwnTranslations(locale);
            var others = i18n.CollectOtherTranslations(locale, result.FallbackLocale, store.LocalizationDir, ours);
            var buildingCache = store.LoadBuildingPhrases(locale);

            foreach (var info in result.Mods.Values)
            {
                var saved = store.Get(locale, info.Key);
                foreach (var kv in info.Source)
                {
                    // 设置页标题（选项菜单左侧的 mod 列表）显示成“译名 (原名)”；mod 名在其他地方一律保留原名。
                    bool modTitle = IsModTitleKey(kv.Key);
                    if (modTitle ? !NeedsModTitle(kv.Value, locale, fallbackTarget) : !TextRules.IsTranslatable(kv.Value, locale))
                    {
                        if (modTitle && saved != null && saved.Translations.ContainsKey(kv.Key))
                        {
                            info.Stale.Add(kv.Key);
                        }
                        continue;
                    }
                    info.Translatable++;

                    // mod 自带或其他翻译来源（I18N 官方库、其他语言包）已有译文：始终优先使用它们。
                    bool hasNative;
                    if (fallbackTarget)
                    {
                        // 英语游戏：只有 I18N 另行提供了真正的英文版本才算已有译文。
                        hasNative = others.TryGetValue(kv.Key, out var other) && other != kv.Value
                            && LanguageDetector.IsInTargetLanguage(other, locale);
                    }
                    else
                    {
                        // 直接读游戏词典的原始条目（绕过 I18N 的拦截），并排除本 mod 自己注入的译文。
                        // 自带/官方译文与英文原文完全相同，说明其实没翻译（作者或官方照搬了英文），当成待翻译。
                        hasNative = (others.TryGetValue(kv.Key, out var other) && other != kv.Value)
                            || (active.TryGetRaw(kv.Key, out var raw, out bool isFallback) && !isFallback && raw != kv.Value
                                && !(ours.TryGetValue(kv.Key, out var mine) && mine == raw));
                    }
                    bool hasOurs = saved != null && saved.Translations.ContainsKey(kv.Key);
                    if (hasNative)
                    {
                        info.Native++;
                        if (hasOurs)
                        {
                            info.Stale.Add(kv.Key);
                        }
                        continue;
                    }

                    info.OwnKeys.Add(kv.Key);
                    if (!hasOurs)
                    {
                        info.Pending.Add(kv.Key);
                    }
                    else if (saved.Sources.TryGetValue(kv.Key, out var oldSource) && oldSource == kv.Value && FollowsBuildingFormat(kv.Key, kv.Value, saved.Translations[kv.Key], result.OfficialBuildingPhrases, buildingCache)
                        && (!modTitle || IsModTitleFormat(kv.Value, saved.Translations[kv.Key]))
                        && !result.ProtectedNames.Lost(kv.Value, saved.Translations[kv.Key], out _))
                    {
                        info.Done++;
                    }
                    else
                    {
                        // 原文在 mod 更新后变了，需要重翻。
                        info.Outdated++;
                        info.Pending.Add(kv.Key);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- 设置页标题

        private const string kModTitlePrefix = "Options.SECTION[";

        /// <summary>选项菜单里 mod 设置页的标题（左侧 mod 列表显示的名字）。</summary>
        internal static bool IsModTitleKey(string key) => key != null && key.StartsWith(kModTitlePrefix, StringComparison.Ordinal);

        /// <summary>
        /// 设置页标题是否需要加译名：原名已含目标语言文字（如 "CSLModernMap地图导出"）不需要；
        /// 英语游戏只给非英文的 mod 名加英文译名。
        /// </summary>
        private static bool NeedsModTitle(string name, string locale, bool fallbackTarget)
        {
            if (string.IsNullOrWhiteSpace(name) || !LanguageDetector.HasLetters(name) || TextRules.IsMachineIdentifier(name.Trim()))
            {
                return false;
            }
            if (fallbackTarget)
            {
                return !name.All(c => c < 128);
            }
            // 非拉丁文字的语言：已含该文字就是作者给过译名了。拉丁文字语言（德、法…）无法按文字区分，都加译名。
            bool latinTarget = LanguageDetector.ScriptMatches("a", locale);
            return latinTarget || !LanguageDetector.ScriptMatches(name, locale);
        }

        /// <summary>“译名 (原名)”。译名为空或与原名相同（无法意译的品牌名）时只用原名。</summary>
        internal static string ComposeModTitle(string localized, string original)
        {
            original = original.Trim();
            string name = (localized ?? string.Empty).Trim();
            // 模型有时自己带上原名：去掉，统一由这里拼接。
            if (name.IndexOf(original, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                name = Regex.Replace(name, Regex.Escape(original), string.Empty, RegexOptions.IgnoreCase);
                name = name.Replace("()", string.Empty).Replace("（）", string.Empty).Trim(' ', '-', '–', '—', ':', '：', '/', '|');
            }
            if (name.Length == 0 || string.Equals(name, original, StringComparison.OrdinalIgnoreCase))
            {
                return original;
            }
            return name + " (" + original + ")";
        }

        internal static bool IsModTitleFormat(string source, string translation)
        {
            string original = source.Trim();
            return translation == original || translation.EndsWith(" (" + original + ")", StringComparison.Ordinal);
        }

        /// <summary>格式固定的条目（拼出的建筑名、设置页标题）：不做质量检查、不送审校。</summary>
        internal static bool IsFixedFormat(string key, string source) => IsModTitleKey(key) || TemplateForEntry(key, source) != null;

        /// <summary>
        /// 由内部名拼出的资产名，译文必须等于“统一的类型短语译名 + 后缀”（与原版格式一致，同类叫法统一）；
        /// 早期版本逐条翻译的结果（如 "中国高层住宅01a 1级 6×6"、"中国奖励中层住宅"）视为需要更新。
        /// </summary>
        private static bool FollowsBuildingFormat(string key, string source, string translation, IReadOnlyDictionary<string, string> official, IReadOnlyDictionary<string, string> cache)
        {
            var template = TemplateForEntry(key, source);
            if (template == null)
            {
                return true;
            }
            string phrase = template.Value.phrase;
            string expected = official.TryGetValue(phrase, out var o) ? o : cache.TryGetValue(phrase, out var c) ? c : null;
            // 类型短语还没有统一译名（旧版本逐条翻译的）或与统一译名不一致：需要更新（只请求短语，花费很少）。
            return expected != null && translation == expected + template.Value.suffix;
        }

        private void CollectGlossary(ScanResult result)
        {
            if (string.Equals(result.Locale, result.FallbackLocale, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var fallback = m_Snapshot.Fallback;
            var active = m_Snapshot.Active;
            CollectOfficialBuildingTerms(result);
            foreach (var kv in fallback)
            {
                // 分区预制件的名称没有编号，如 "Assets.NAME[CCP5 Residential Medium]"、"Assets.NAME[EU Residential Low]"。
                Match zone = s_ZoneKey.Match(kv.Key);
                if (zone.Success && active.TryGetRaw(kv.Key, out var zoneTranslated, out bool zoneFallback) && !zoneFallback)
                {
                    result.ZoneNames[zone.Groups["name"].Value] = (kv.Value, zoneTranslated.Trim());
                }
            }
            foreach (var kv in fallback)
            {
                if (kv.Value == null || kv.Value.Length > 40 || kv.Value.Length < 2)
                {
                    continue;
                }
                if (!active.TryGetRaw(kv.Key, out var target, out bool isFallback) || isFallback)
                {
                    continue;
                }
                result.GlossaryEnglish[kv.Key] = kv.Value;
                result.GlossaryTarget[kv.Key] = target;
            }
        }
    }
}
