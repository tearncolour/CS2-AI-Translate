using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AITranslate.Locale;
using Colossal.Core;
using Colossal.Localization;
using Colossal.PSI.Common;
using Colossal.PSI.Environment;
using Colossal.PSI.PdxSdk;
using Game.PSI;
using Game.SceneFlow;
using Game.UI.Localization;
using Game.UI.Widgets;
using Newtonsoft.Json.Linq;

namespace AITranslate.Core
{
    /// <summary>
    /// 统筹扫描、翻译、保存与实时生效。翻译目标始终是游戏当前语言。
    /// 除标注为后台的部分外，均在主线程执行。
    /// </summary>
    internal sealed class TranslationService : IDisposable
    {
        private const string kNotificationId = "AITranslate.Progress";
        private const int kMaxBatchChars = 6000;
        private const int kLargeModThreshold = 1500;

        // 环境（mod 列表、本地化源、I18N、游戏语言）稳定多久后才扫描。
        // 本地 mod 比 Paradox 订阅的 mod 先加载，后者可能在主菜单出现几十秒后才加载完。
        private const double kSettleSeconds = 3;
        private const double kAutoTranslateSettleSeconds = 20;

        private static int s_UiVersion;

        public static int GetUiVersion() => s_UiVersion;

        private static void BumpUi() => Interlocked.Increment(ref s_UiVersion);

        private readonly Setting m_Setting;

        public TranslationStore Store { get; } = new TranslationStore();

        private I18NBridge m_I18N;

        private bool m_I18NConfigured;

        private ScanResult m_Scan;

        private Task<Glossary> m_GlossaryTask;

        private List<Colossal.PSI.Common.Mod> m_PlaysetMods;

        // 当前语言下全部译文，用于注入 I18N 词典 / 游戏词典。
        private Dictionary<string, string> m_Live = new Dictionary<string, string>(StringComparer.Ordinal);

        private string m_LiveLocale;

        private Dictionary<string, string> m_LastInjectedDict;

        // 未安装 I18N 时的降级方案。
        private GatedMemorySource m_Gated;

        private Guid? m_WatchUpdater;
        private int m_WatchFrames;
        private string m_Signature;
        private DateTime m_SignatureChangedAt = DateTime.UtcNow;
        private bool m_RescanPending = true;
        private bool m_AutoTranslatePending = true;
        private int m_LocateModCount = -1;
        private string m_SignatureLocale;

        private bool m_Disposed;

        private volatile bool m_Busy;

        private bool m_Scanning;

        private CancellationTokenSource m_Cts;

        private int m_JobTotal;
        private int m_JobDone;
        private int m_JobFailed;
        private int m_JobFixed;
        private List<int> m_JobScores = new List<int>();
        private JobKind m_JobKind;
        private int m_LastNotifiedPercent = -1;
        private string m_JobCurrent;
        private string m_JobLanguage;
        private Stopwatch m_JobWatch;

        // 留在界面上的状态文字都存成 LocText，显示时才格式化：切换游戏语言后跟着变。
        private LocText m_ApiStatus = LocText.Empty;
        private List<LocText> m_Overview = new List<LocText> { LocText.Of("Waiting") };
        private LocText m_Progress = LocText.Empty;
        private LocText m_Environment = LocText.Empty;

        public string ApiStatusText => m_ApiStatus.ToString();

        public string OverviewText => string.Join("\n", m_Overview.Select(t => t.ToString()));

        public string ProgressText => m_Progress.ToString();

        public string EnvironmentText => m_Environment.ToString();

        private LocText m_LastSummary;

        private string m_LastScanSummary;

        // 删除了已生效的译文：需要重新加载，让原文/官方译文重新显示。
        private bool m_NeedsReload;

        // 只有确定没有 I18N Everywhere 后才直接写游戏词典。
        private bool m_AllowDirectInjection;


        public bool IsBusy => m_Busy || m_Scanning;

        /// <summary>正在执行翻译/审校/描述/修正任务（不含后台检测）。</summary>
        public bool IsJobRunning => m_Busy;

        // 概况里「对全部 mod 的操作」按钮：有事可做时才显示。
        public bool HasPendingMods => m_Scan != null && m_Scan.Mods.Values.Any(m => m.State == ModState.NeedsTranslation);

        public bool HasSuspicious => m_Quality.Values.Any(q => q.Suspicious > 0);

        public bool HasMissingDescriptions => m_Scan != null && m_Scan.MissingDescriptions > 0;

        public bool HasMixed => m_Scan != null && m_Scan.Mods.Values.Any(m => m.MixedNative.Count > 0);

        /// <summary>所选 mod 有缺失或已变化的条目。</summary>
        public bool HasPending(string key)
        {
            return m_Scan != null && !string.IsNullOrEmpty(key) && m_Scan.Mods.TryGetValue(key, out var m) && m.Pending.Count > 0;
        }

        public TranslationService(Setting setting)
        {
            m_Setting = setting;
            LocText.Resolver = L;
            m_Overview = new List<LocText> { T("Waiting") };
        }

        private static LocalizationManager Localization => GameManager.instance?.localizationManager;

        /// <summary>延迟格式化的界面文字：显示时才按当前游戏语言取词，切换语言后跟着变（见 LocText）。</summary>
        private static LocText T(string name, params object[] args) => LocText.Of(name, args);

        /// <summary>
        /// 取界面文字。经由游戏词典查询，因此 I18N Everywhere（以及本 mod 自己翻译的界面译文）都会生效；
        /// 查不到时使用内置英文。
        /// </summary>
        private static string L(string name, params object[] args)
        {
            string format = null;
            try
            {
                if (Localization != null && Localization.activeDictionary.TryGetValue(LocaleTable.kUiPrefix + name, out var value))
                {
                    format = value;
                }
            }
            catch (Exception)
            {
                // 词典尚未就绪。
            }
            format = format ?? LocaleTable.English(name);
            if (args == null || args.Length == 0)
            {
                return format;
            }
            try
            {
                return string.Format(format, args);
            }
            catch (FormatException)
            {
                return string.Format(LocaleTable.English(name), args);
            }
        }

        private static string LanguageDisplayName(string locale)
        {
            try
            {
                string name = Localization?.GetLocalizedName(locale);
                return string.IsNullOrEmpty(name) || name == locale ? locale : $"{name} ({locale})";
            }
            catch (Exception)
            {
                return locale;
            }
        }

        // ================================================================ 启动与环境监视

        public void PrepareStorage()
        {
            try
            {
                Store.MigrateLegacy();
            }
            catch (Exception ex)
            {
                Mod.Log.Warn(ex, "Legacy migration failed: " + ex.Message);
            }
            try
            {
                Store.EnsureLayout();
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "Cannot create translation folder: " + ex.Message);
            }
        }

        public void Start()
        {
            m_WatchUpdater = MainThreadDispatcher.RegisterUpdater((Func<bool>)Watch);
            var lm = Localization;
            if (lm != null)
            {
                lm.onActiveDictionaryChanged += OnActiveDictionaryChanged;
            }
        }

        /// <summary>游戏语言变了（或词典重新加载，比如本 mod 的界面译文刚写进去）：已经显示的状态文字和日志按新语言重新格式化。</summary>
        private void OnActiveDictionaryChanged()
        {
            LocText.Invalidate();
            BumpUi();
        }

        /// <summary>
        /// 每半秒检查一次环境签名；签名稳定 <see cref="kSettleSeconds"/> 秒后重新应用译文并扫描。
        /// 这样既能等到较晚加载的订阅 mod 和 I18N Everywhere，也能在切换游戏语言后自动跟进。
        /// </summary>
        private bool Watch()
        {
            if (m_Disposed)
            {
                return true;
            }
            try
            {
                LiveUpdate();
                if (++m_WatchFrames % 30 != 0)
                {
                    return false;
                }
                var gm = GameManager.instance;
                if (gm == null || gm.modManager == null || !gm.modManager.isInitialized
                    || gm.state == GameManager.State.Loading || gm.state == GameManager.State.Booting)
                {
                    return false;
                }

                string signature = ComputeSignature();
                DateTime now = DateTime.UtcNow;
                if (signature != m_Signature)
                {
                    string locale = Localization.activeLocaleId;
                    if (m_SignatureLocale != null && locale != m_SignatureLocale)
                    {
                        // 切换了游戏语言：新语言也要按设置自动翻译一次。
                        m_AutoTranslatePending = true;
                        Mod.Log.Info($"Game language changed: {m_SignatureLocale} → {locale}");
                        ActivityLog.Info(T("LogLanguageChanged", LanguageDisplayName(locale)));
                    }
                    m_SignatureLocale = locale;
                    m_Signature = signature;
                    m_SignatureChangedAt = now;
                    m_RescanPending = true;
                    return false;
                }

                double stable = (now - m_SignatureChangedAt).TotalSeconds;
                if (m_RescanPending && stable >= kSettleSeconds && !IsBusy)
                {
                    m_RescanPending = false;
                    OnEnvironmentSettled();
                }
                else if (m_AutoTranslatePending && !m_RescanPending && stable >= kAutoTranslateSettleSeconds && m_Scan != null && !IsBusy)
                {
                    m_AutoTranslatePending = false;
                    // 所有 mod 都加载完了，才能判断哪些是新安装的。
                    InitKnownMods();
                    // 环境已稳定 20 秒仍没有 I18N Everywhere：改为直接写游戏词典。
                    if (!UseI18N && !m_AllowDirectInjection)
                    {
                        m_AllowDirectInjection = true;
                        ApplyToGame(forceReload: true);
                    }
                    if (m_Setting.AutoTranslateOnStartup)
                    {
                        m_AutoQueue.Clear();
                        TranslateAllNeeded(fromStartup: true);
                    }
                }
                else if (m_AutoQueue.Count > 0 && !m_RescanPending && stable >= kSettleSeconds && m_Scan != null && !IsBusy)
                {
                    RunAutoQueue();
                }
                else if (!m_RescanPending && stable >= kSettleSeconds && m_Scan != null && !IsBusy)
                {
                    TranslateOwnUiIfNeeded();
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn("Watcher error: " + ex.Message);
            }
            return false;
        }

        // 已为哪些语言自动翻译过本 mod 自己的界面（每种语言每次运行只试一次，失败不反复请求）。
        private readonly HashSet<string> m_OwnUiAttempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 本 mod 的设置页只内置英文和简体中文；其他语言（繁体中文、日语、德语…）在填好 API Key 后自动把设置页翻译成游戏语言，
        /// 切换语言后同样自动跟进。只有两百来条，花费很少，不受「自动翻译」开关限制。
        /// </summary>
        private void TranslateOwnUiIfNeeded()
        {
            string locale = m_Scan.Locale;
            if (string.IsNullOrWhiteSpace(m_Setting.ApiKey) || m_OwnUiAttempted.Contains(locale)
                || !m_Scan.Mods.TryGetValue(ModScanner.kSelfKey, out var self) || self.Pending.Count == 0)
            {
                return;
            }
            m_OwnUiAttempted.Add(locale);
            Mod.Log.Info($"Translating own settings page into {locale}: {self.Pending.Count} entries");
            TranslateMods(new[] { ModScanner.kSelfKey }, force: false);
        }

        private string ComputeSignature()
        {
            int mods = 0;
            foreach (var _ in GameManager.instance.modManager)
            {
                mods++;
            }
            // I18N 只会随 mod 一起加载，mod 数量变化时才需要重新查找。
            if (m_I18N == null || (!m_I18N.Available && mods != m_LocateModCount))
            {
                m_I18N = I18NBridge.Locate();
                m_LocateModCount = mods;
            }
            var lm = Localization;
            int sources = ScanCapture.CountSources(lm, lm.fallbackLocaleId);
            bool i18n = m_I18N.Available && m_I18N.IsInitialized;
            // 翻译范围的开关也算在内：切换后重新检测。
            return $"{mods}|{sources}|{i18n}|{lm.activeLocaleId}|{m_Setting.IncludeDlcContent}|{m_Setting.IncludeRoadBuilderRoads}";
        }

        /// <summary>I18N Everywhere 已加载完成但尚未接入时立即接入（任何检测之前都要先做，否则结果不准）。</summary>
        private void EnsureI18NConfigured()
        {
            if (m_I18N == null || !m_I18N.Available)
            {
                m_I18N = I18NBridge.Locate();
            }
            if (m_I18N.Available && m_I18N.IsInitialized && !m_I18NConfigured)
            {
                m_I18N.EnsureLanguagePackRegistered(TranslationStore.kPackFolderName, Store.LocalizationDir);
                m_I18NConfigured = true;
                Mod.Log.Info("I18N Everywhere detected, version " + m_I18N.Version);
                ActivityLog.Info(T("LogI18NDetected", m_I18N.Version));
                m_LiveLocale = null; // 从降级模式切换到 I18N 模式
            }
        }

        private void OnEnvironmentSettled()
        {
            EnsureI18NConfigured();
            if (m_LiveLocale != Localization.activeLocaleId)
            {
                ApplyToGame(forceReload: false);
            }
            UpdateEnvironmentText();
            FetchPlaysetThenScan();
        }

        private bool UseI18N => m_I18N != null && m_I18N.Available && m_I18NConfigured;

        private void FetchPlaysetThenScan()
        {
            m_Scanning = true;
            m_Progress = T("Scanning");
            BumpUi();

            Task<HashSet<Colossal.PSI.Common.Mod>> task = null;
            try
            {
                task = PlatformManager.instance.GetPSI<PdxSdkPlatform>("PdxSdk")?.GetModsInActivePlayset();
            }
            catch (Exception ex)
            {
                Mod.Log.Warn("Cannot query active playset: " + ex.Message);
            }

            int frames = 0;
            MainThreadDispatcher.RegisterUpdater(() =>
            {
                if (m_Disposed)
                {
                    return true;
                }
                frames++;
                if (task != null && !task.IsCompleted && frames < 900)
                {
                    return false;
                }
                if (task != null && task.Status == TaskStatus.RanToCompletion && task.Result != null)
                {
                    m_PlaysetMods = task.Result.ToList();
                }
                else if (task?.Exception != null)
                {
                    Mod.Log.Warn("Playset query failed: " + task.Exception.GetBaseException().Message);
                }
                m_PlaysetMods = m_PlaysetMods ?? new List<Colossal.PSI.Common.Mod>();
                Safe(DoScan, "scan");
                return true;
            });
        }

        // ================================================================ 扫描

        public void Rescan()
        {
            if (IsBusy)
            {
                return;
            }
            FetchPlaysetThenScan();
        }

        // 主线程每帧最多花在采集游戏数据上的时间。
        private const double kCaptureBudgetMs = 4;

        private bool m_ScanRunning;
        private bool m_ScanAgain;

        /// <summary>
        /// 开始一次检测：主线程分帧采集游戏数据（每帧几毫秒）→ 后台线程分析 → 主线程套用结果。
        /// 整个过程不会卡住游戏界面。正在检测时再次调用，会在本次结束后再检测一次。
        /// </summary>
        private void DoScan()
        {
            if (m_ScanRunning)
            {
                m_ScanAgain = true;
                return;
            }
            m_ScanRunning = true;
            m_Scanning = true;
            m_Progress = T("Scanning");
            BumpUi();
            try
            {
                EnsureI18NConfigured();
                if (m_LiveLocale != Localization.activeLocaleId)
                {
                    ApplyToGame(forceReload: false);
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn("Prepare scan failed: " + ex.Message);
            }

            var total = Stopwatch.StartNew();
            var bridge = m_I18N ?? I18NBridge.Locate();
            var capture = new ScanCapture(m_PlaysetMods, bridge);
            // I18N 已就绪，或已确定没有 I18N：这时才能可靠判断“原译文”，才允许维护修正混杂的检查记录。
            capture.Result.Settled = (bridge.Available && bridge.IsInitialized) || m_AllowDirectInjection;
            MainThreadDispatcher.RegisterUpdater(() =>
            {
                if (m_Disposed)
                {
                    return true;
                }
                try
                {
                    if (!capture.Step(kCaptureBudgetMs))
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Mod.Log.Error(ex, "Scan capture failed: " + ex.Message);
                    FinishScan(null, null, total);
                    return true;
                }
                AnalyzeInBackground(capture.Result, bridge, total, attempt: 0);
                return true;
            });
        }

        private void AnalyzeInBackground(ScanSnapshot snapshot, I18NBridge bridge, Stopwatch total, int attempt)
        {
            Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                ScanResult scan = null;
                try
                {
                    scan = new ModScanner(snapshot).Scan(Store, bridge);
                    scan.DuplicateGeneratedNames = FindDuplicateGeneratedNames(Store, scan.Locale);
                }
                catch (Exception ex)
                {
                    Mod.Log.Error(ex, "Scan analysis failed: " + ex.Message);
                }
                long analyzeMs = sw.ElapsedMilliseconds;
                MainThreadDispatcher.RunOnMainThread(() =>
                {
                    if (m_Disposed)
                    {
                        return;
                    }
                    // 译文换了归属（写入存储）只在主线程做；之后用同一份快照重新分析一次。
                    if (scan != null && attempt == 0 && Safe(() => AdoptMisfiled(scan)) > 0)
                    {
                        AnalyzeInBackground(snapshot, bridge, total, attempt + 1);
                        return;
                    }
                    if (scan != null)
                    {
                        Mod.Log.Info($"Scan timing: capture {snapshot.CaptureMs} ms over {snapshot.CaptureFrames} frames (main thread), analyze {analyzeMs} ms (background), total {total.ElapsedMilliseconds} ms");
                    }
                    FinishScan(scan, snapshot, total);
                });
            });
        }

        /// <summary>在主线程套用检测结果。</summary>
        private void FinishScan(ScanResult scan, ScanSnapshot snapshot, Stopwatch total)
        {
            try
            {
                if (scan != null)
                {
                    ApplyScan(scan);
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, "Apply scan failed: " + ex.Message);
            }
            finally
            {
                m_ScanRunning = false;
                m_Scanning = false;
                if (!m_Busy)
                {
                    m_Progress = m_LastSummary != null ? T("LastJob", m_LastSummary) : LocText.Empty;
                }
                BumpUi();
            }
            if (m_ScanAgain && !m_Disposed)
            {
                m_ScanAgain = false;
                DoScan();
            }
        }

        private void ApplyScan(ScanResult scan)
        {
            var applyWatch = Stopwatch.StartNew();
            PruneObsolete(scan);
            m_Scan = scan;
            if (m_NeedsReload)
            {
                m_NeedsReload = false;
                ReloadI18N();
                ApplyToGame(forceReload: true);
            }
            Mod.Log.Info($"Scan applied in {applyWatch.ElapsedMilliseconds} ms (main thread): locale={scan.Locale}, i18n={UseI18N}, mods={scan.Mods.Count}, unnamed assets={scan.UnnamedAssets}, needing translation={scan.Mods.Values.Count(m => m.State == ModState.NeedsTranslation)}");
            string scanSummary = L("LogScan", scan.Mods.Values.Count(m => m.State == ModState.NeedsTranslation), scan.Mods.Values.Where(m => m.State == ModState.NeedsTranslation).Sum(m => m.Pending.Count), scan.MissingDescriptions);
            if (scanSummary != m_LastScanSummary)
            {
                m_LastScanSummary = scanSummary;
                ActivityLog.Info(T("LogScan", scan.Mods.Values.Count(m => m.State == ModState.NeedsTranslation), scan.Mods.Values.Where(m => m.State == ModState.NeedsTranslation).Sum(m => m.Pending.Count), scan.MissingDescriptions));
            }

            var en = scan.GlossaryEnglish;
            var target = scan.GlossaryTarget;
            string locale = scan.Locale;
            // 官方区域建筑类型（低层住宅、中层联排住宅…）和地区（欧洲、北美）一定按游戏用语翻译。
            var officialTerms = scan.OfficialBuildingTypes.Concat(scan.OfficialRegions).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value);
            m_GlossaryTask = Task.Run(() =>
            {
                var g = Glossary.Build(en, target, locale).WithForced(officialTerms);
                Mod.Log.Info($"Glossary built for {locale}: {g.Count} terms");
                return g;
            });
            ComputeQualityInBackground(scan, m_GlossaryTask);
            FixModLanguageOptions(scan.Locale);
            DetectNewMods();

            EnsureSelection();
            UpdateOverview();
        }

        private static int Safe(Func<int> action)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, ex.Message);
                return 0;
            }
        }

        /// <summary>把之前存在别的分组下、原文一致的译文移到正确的 mod 名下。</summary>
        private int AdoptMisfiled(ScanResult scan)
        {
            // 已在别的分组（旧的归属规则、共用文本的同系列 mod）里翻好、原文一致的条目，直接复制过来，不再付费重翻。
            int total = 0;
            foreach (var info in scan.Mods.Values.Where(m => m.Pending.Count > 0))
            {
                int moved = Store.Adopt(scan.Locale, info.Key, info.DisplayName, info.Source, info.Pending);
                if (moved > 0)
                {
                    Mod.Log.Info($"Reused {moved} existing translations for {info.DisplayName}");
                    total += moved;
                }
            }
            return total;
        }

        /// <summary>把自带语言选择的 mod 切到“自动/跟随游戏”，否则它们不会读取游戏词典里的译文。</summary>
        private void FixModLanguageOptions(string locale)
        {
            var lm = Localization;
            if (!m_Setting.FollowGameLanguageOptions || string.Equals(locale, lm.fallbackLocaleId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var changes = ModLanguageOptions.FollowGameLanguage(apply: true, exclude: typeof(Setting), handled: m_LanguageOptionsHandled);
            if (changes.Count == 0)
            {
                return;
            }
            foreach (var c in changes)
            {
                Mod.Log.Info("Switched mod language option to follow the game: " + c);
                ActivityLog.Info(T("LogLanguageOption", ResolveModName(c.Assembly) ?? c.ModName));
            }
            RefreshGameUI();
        }

        /// <summary>程序集 → 玩家看到的 mod 名称（Paradox Mods 上的显示名）。</summary>
        private string ResolveModName(System.Reflection.Assembly assembly)
        {
            if (assembly == null)
            {
                return null;
            }
            try
            {
                foreach (Game.Modding.ModManager.ModInfo info in GameManager.instance.modManager)
                {
                    if (info?.asset?.assembly != assembly)
                    {
                        continue;
                    }
                    string dir = Path.GetFullPath(Path.GetDirectoryName(info.asset.path)).TrimEnd('\\', '/');
                    foreach (var mod in m_PlaysetMods ?? new List<Colossal.PSI.Common.Mod>())
                    {
                        if (!string.IsNullOrEmpty(mod.path) && string.Equals(Path.GetFullPath(mod.path).TrimEnd('\\', '/'), dir, StringComparison.OrdinalIgnoreCase))
                        {
                            return string.IsNullOrWhiteSpace(mod.displayName) ? info.asset.name : mod.displayName.Trim();
                        }
                    }
                    return info.asset.name;
                }
            }
            catch (Exception)
            {
                // 退回调用方提供的名称。
            }
            return assembly.GetName().Name;
        }

        // ---------------------------------------------------------------- 质量

        internal sealed class QualitySummary
        {
            public int Checked;
            public int Reviewed;
            public int LowScore;
            public double AverageScore;
            public readonly Dictionary<string, List<string>> Issues = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            public int Suspicious => Issues.Count;
        }

        private Dictionary<string, QualitySummary> m_Quality = new Dictionary<string, QualitySummary>(StringComparer.Ordinal);


        private readonly HashSet<string> m_LanguageOptionsHandled = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>术语表构建好后，在后台对所有已保存的译文做一次免费的启发式检查。</summary>
        private void ComputeQualityInBackground(ScanResult scan, Task<Glossary> glossaryTask)
        {
            string locale = scan.Locale;
            var snapshot = new List<(string mod, string key, string source, string translation, int score)>();
            foreach (var info in scan.Mods.Values)
            {
                var saved = Store.Get(locale, info.Key);
                if (saved == null)
                {
                    continue;
                }
                foreach (string key in info.OwnKeys)
                {
                    if (saved.Translations.TryGetValue(key, out var t) && info.Source.TryGetValue(key, out var s) && !ModScanner.IsFixedFormat(key, s))
                    {
                        saved.Scores.TryGetValue(key, out int score);
                        snapshot.Add((info.Key, key, s, t, score));
                    }
                }
            }
            bool useGlossary = m_Setting.UseGameGlossary;
            Task.Run(async () =>
            {
                Glossary glossary = useGlossary && glossaryTask != null ? await glossaryTask.ConfigureAwait(false) : Glossary.Empty;
                var result = new Dictionary<string, QualitySummary>(StringComparer.Ordinal);
                foreach (var group in snapshot.GroupBy(e => e.mod))
                {
                    var q = new QualitySummary();
                    var scores = new List<int>();
                    foreach (var e in group)
                    {
                        q.Checked++;
                        if (e.score > 0)
                        {
                            scores.Add(e.score);
                            if (e.score <= Translator.kFixThreshold)
                            {
                                q.LowScore++;
                            }
                        }
                        var issues = QualityChecker.Check(e.source, e.translation, locale, glossary);
                        // 已由 AI 审过的条目不再算“可优化”（否则提示永远消不掉，也会被反复审校）。
                        if (e.score == 0 && QualityChecker.IsSuspicious(issues))
                        {
                            q.Issues[e.key] = issues;
                        }
                    }
                    q.Reviewed = scores.Count;
                    q.AverageScore = scores.Count > 0 ? scores.Average() : 0;
                    result[group.Key] = q;
                }
                MainThreadDispatcher.RunOnMainThread(() =>
                {
                    if (m_Scan != null && m_Scan.Locale == locale)
                    {
                        m_Quality = result;
                        Mod.Log.Info($"Quality check: {result.Values.Sum(q => q.Checked)} entries, {result.Values.Sum(q => q.Suspicious)} suspicious, {result.Values.Sum(q => q.Reviewed)} AI-reviewed");
                        UpdateOverview();
                        BumpUi();
                    }
                });
            });
        }

        /// <summary>移除已有自带/官方译文或 mod 中已删除的条目（只处理本次扫描到的 mod，停用的 mod 不动）。</summary>
        /// <summary>
        /// 后台：找出同一个资产名既有“本 mod 拼出来的名字”的译文、又有“真正名字”的译文的条目（启动时序问题造成的重复）。
        /// 要对全部译文做正则判断，放在主线程会卡一秒多。
        /// </summary>
        private static Dictionary<string, List<string>> FindDuplicateGeneratedNames(TranslationStore store, string locale)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var localeGroups = store.GetLocale(locale)
                .Where(g => !g.Key.StartsWith("desc_", StringComparison.Ordinal) && !g.Key.StartsWith("fix_", StringComparison.Ordinal))
                .Select(g => (group: g.Key, sources: g.Value.Sources.ToList()))
                .ToList();
            var generated = new List<(string group, string key)>();
            var realNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (group, sources) in localeGroups)
            {
                foreach (var kv in sources)
                {
                    string prefab = PrefabNameFromKey(kv.Key);
                    if (prefab == null)
                    {
                        continue;
                    }
                    if (ModScanner.IsGeneratedName(prefab, kv.Value))
                    {
                        generated.Add((group, kv.Key));
                    }
                    else
                    {
                        realNames.Add(kv.Key);
                    }
                }
            }
            foreach (var (group, key) in generated.Where(g => realNames.Contains(g.key)))
            {
                if (!result.TryGetValue(group, out var list))
                {
                    result[group] = list = new List<string>();
                }
                list.Add(key);
            }
            return result;
        }

        private void PruneObsolete(ScanResult scan)
        {
            // 撤销作废的修正：
            // - 修正混杂时把 mod 名也翻掉了（如 "Bridge Builder 桥梁" → "“桥梁建造者”桥梁"），恢复官方译文；
            // - 被修正的那条译文后来变了（作者或官方更新），旧修正不能再盖住新译文。
            foreach (var info in scan.Mods.Values)
            {
                string group = TranslationStore.FixGroup(info.Key);
                var fixes = Store.Get(scan.Locale, group);
                if (fixes == null)
                {
                    continue;
                }
                var undo = fixes.Translations
                    .Where(kv => fixes.Sources.TryGetValue(kv.Key, out var original) && scan.ProtectedNames.Lost(original, kv.Value, out _))
                    .Select(kv => kv.Key)
                    .Concat(info.StaleFixes)
                    .Distinct().ToList();
                // 没有改进的改写：恢复原译文，但留下“已检查”记录（译文 = 原译文），以后不再请求。
                var revert = info.RevertFixes.Where(k => !undo.Contains(k) && fixes.Sources.ContainsKey(k)).ToList();
                // “不用改”的记录对应的原译文变了：改记到现在的译文上，仍算已检查。
                var remark = info.RemarkFixes.Where(p => !undo.Contains(p.Key) && fixes.Sources.ContainsKey(p.Key)).ToList();
                foreach (var p in remark)
                {
                    fixes.Sources[p.Key] = p.Value;
                    fixes.Translations[p.Key] = p.Value;
                    m_Live.Remove(p.Key);
                }
                if (remark.Count > 0)
                {
                    Mod.Log.Info($"Fixes of {info.DisplayName}: re-marked {remark.Count} checked entries to the current text");
                }
                if (undo.Count == 0 && revert.Count == 0)
                {
                    if (remark.Count > 0)
                    {
                        Safe(() => Store.Save(scan.Locale, group, info.DisplayName), "save");
                        m_NeedsReload = true;
                    }
                    continue;
                }
                foreach (string k in undo)
                {
                    fixes.Translations.Remove(k);
                    fixes.Sources.Remove(k);
                    m_Live.Remove(k);
                }
                foreach (string k in revert)
                {
                    fixes.Translations[k] = fixes.Sources[k];
                    fixes.Scores.Remove(k);
                    m_Live.Remove(k);
                }
                Safe(() => Store.Save(scan.Locale, group, info.DisplayName), "save");
                Mod.Log.Info($"Fixes of {info.DisplayName}: undid {undo.Count} outdated, reverted {revert.Count} that did not remove any English");
                m_NeedsReload = true;
            }

            // 同一个资产名既有“本 mod 拼出来的名字”的译文，又有“真正名字”的译文（启动时序问题造成的重复）：保留后者。
            // 哪些是重复的已在后台算好（FindDuplicateGeneratedNames），这里只删除。
            var groups = Store.GetLocale(scan.Locale);
            foreach (var pair in scan.DuplicateGeneratedNames)
            {
                if (!groups.TryGetValue(pair.Key, out var data))
                {
                    continue;
                }
                var duplicates = pair.Value.Where(k => data.Sources.ContainsKey(k)).ToList();
                if (duplicates.Count == 0)
                {
                    continue;
                }
                foreach (string k in duplicates)
                {
                    data.Translations.Remove(k);
                    data.Sources.Remove(k);
                    data.Scores.Remove(k);
                }
                string groupKey = pair.Key;
                Safe(() => Store.Save(scan.Locale, groupKey, null), "save");
                Mod.Log.Info($"Removed {duplicates.Count} duplicate generated names from {groupKey}");
                m_NeedsReload = true;
            }

            // 资产后来自带了描述：删掉之前生成的描述，让自带描述生效。
            foreach (var info in scan.Mods.Values.Where(m => m.StaleDescriptions.Count > 0))
            {
                string group = TranslationStore.DescriptionGroup(info.Key);
                var generated = Store.Get(scan.Locale, group);
                if (generated == null)
                {
                    continue;
                }
                foreach (string k in info.StaleDescriptions)
                {
                    generated.Translations.Remove(k);
                    generated.Sources.Remove(k);
                    m_Live.Remove(k);
                }
                Safe(() => Store.Save(scan.Locale, group, info.DisplayName), "save");
                Mod.Log.Info($"Removed {info.StaleDescriptions.Count} generated descriptions of {info.DisplayName} (asset now has its own)");
            }

            foreach (var info in scan.Mods.Values)
            {
                var saved = Store.Get(scan.Locale, info.Key);
                if (saved == null)
                {
                    continue;
                }
                // 移除：已有自带/官方译文；原文是内部 ID；按现在的规则不该翻译且译文与原文相同（翻了也没用）。
                // 注意：本次检测里“看不到原文”的条目绝不删除——启动时 mod 和资产是陆续加载的，看不到不代表已删除；
                // 真的被 mod 删掉的文本留在文件里也无害。
                var remove = saved.Translations.Keys
                    .Where(k => info.Source.TryGetValue(k, out var src)
                        && (TextRules.IsMachineIdentifier(src)
                            || (!TextRules.IsTranslatable(src, scan.Locale) && saved.Translations[k] == src)))
                    .Concat(info.Stale).Distinct().ToList();
                if (remove.Count == 0)
                {
                    continue;
                }
                foreach (string k in remove)
                {
                    saved.Translations.Remove(k);
                    saved.Sources.Remove(k);
                    m_Live.Remove(k);
                }
                Safe(() => Store.Save(scan.Locale, info.Key, info.DisplayName), "save");
                Mod.Log.Info($"Pruned {remove.Count} obsolete entries of {info.DisplayName}");
                m_NeedsReload = true;
            }
        }

        private void EnsureSelection()
        {
            string selected = m_Setting.SelectedMod;
            if (!string.IsNullOrEmpty(selected) && m_Scan.Mods.TryGetValue(selected, out var cur) && cur.Translatable > 0)
            {
                return;
            }
            var first = m_Scan.Mods.Values
                .Where(m => m.State == ModState.NeedsTranslation)
                .OrderByDescending(m => m.Key == ModScanner.kSelfKey)
                .ThenByDescending(m => m.Pending.Count)
                .FirstOrDefault();
            m_Setting.SelectedMod = first?.Key ?? Setting.kNoneMod;
        }

        private void UpdateOverview()
        {
            if (m_Scan == null)
            {
                return;
            }
            var withText = m_Scan.Mods.Values.Where(m => m.Translatable > 0).ToList();
            var need = withText.Where(m => m.State == ModState.NeedsTranslation).ToList();
            int pending = need.Sum(m => m.Pending.Count);

            // 每项一行，显示在整行多行文本里。
            var lines = new List<LocText> { T("StatusTarget", LanguageDisplayName(m_Scan.Locale)) };
            lines.Add(need.Count > 0
                ? T("StatusMods", need.Count, pending)
                : T("StatusModsDone", withText.Count));
            int suspicious = m_Quality.Values.Sum(q => q.Suspicious);
            if (suspicious > 0)
            {
                lines.Add(T("StatusQuality", suspicious));
            }
            int mixed = m_Scan.Mods.Values.Sum(m => m.MixedNative.Count);
            if (mixed > 0)
            {
                lines.Add(T("StatusMixed", mixed));
            }
            if (m_Scan.MissingDescriptions > 0)
            {
                int missingNames = m_Scan.Mods.Values.Sum(m => m.MissingNames);
                lines.Add(missingNames > 0 ? T("StatusDescriptionsNames", m_Scan.MissingDescriptions, missingNames) : T("StatusDescriptions", m_Scan.MissingDescriptions));
            }
            if (string.IsNullOrWhiteSpace(m_Setting.ApiKey))
            {
                lines.Insert(0, T("StatusNoKey"));   // 最要紧的放最前面
            }
            m_Overview = lines;
        }

        private void UpdateEnvironmentText()
        {
            LocText text;
            if (!UseI18N)
            {
                text = T("NoI18N");
            }
            else if (!m_I18N.CanLoadLanguagePacks)
            {
                text = T("PacksDisabled", m_I18N.Version);
            }
            else
            {
                text = T("PackRegistered", m_I18N.Version);
            }
            var lm = Localization;
            if (UseI18N && lm != null && string.Equals(lm.activeLocaleId, lm.fallbackLocaleId, StringComparison.OrdinalIgnoreCase) && !m_I18N.Overwrite)
            {
                text = LocText.Concat(text, T("OverwriteOff"));
            }
            text = LocText.Concat(text, T("Folder", Store.RootDir.Replace('/', '\\')));
            m_Environment = text;
            BumpUi();
        }

        // ================================================================ 下拉列表 / 描述

        public DropdownItem<string>[] GetDropdownItems()
        {
            var items = new List<DropdownItem<string>>();
            var matches = FilteredMods();
            items.Add(new DropdownItem<string>
            {
                value = Setting.kNoneMod,
                displayName = LocalizedString.Value(m_Setting.HasNoFilter() ? L("SelectMod") : L("SelectModFiltered", matches.Count)),
            });
            foreach (var m in matches)
            {
                items.Add(new DropdownItem<string> { value = m.Key, displayName = LocalizedString.Value(Label(m)) });
            }
            return items.ToArray();
        }

        // ---------------------------------------------------------------- 搜索与分类

        private List<ModTextInfo> m_FilteredCache;
        private int m_FilteredVersion = -1;

        /// <summary>出现在列表里的 mod：有文本或有资产描述相关内容。</summary>
        private static bool IsListed(ModTextInfo m) => m.Translatable > 0 || m.MissingDescriptions.Count > 0 || m.GeneratedDescriptions > 0 || m.MixedNative.Count > 0;

        private bool NeedsPolish(ModTextInfo m) => m_Quality.TryGetValue(m.Key, out var q) && q.Suspicious > 0;

        private bool MatchesStatus(ModTextInfo m, Setting.StatusFilterOption filter)
        {
            switch (filter)
            {
                case Setting.StatusFilterOption.Actionable:
                    return m.State == ModState.NeedsTranslation || m.MissingDescriptions.Count > 0 || NeedsPolish(m);
                case Setting.StatusFilterOption.Pending:
                    return m.State == ModState.NeedsTranslation;
                case Setting.StatusFilterOption.MissingDescriptions:
                    return m.MissingDescriptions.Count > 0;
                case Setting.StatusFilterOption.Polish:
                    return NeedsPolish(m);
                case Setting.StatusFilterOption.Done:
                    return m.State != ModState.NeedsTranslation && m.MissingDescriptions.Count == 0 && !NeedsPolish(m);
                default:
                    return true;
            }
        }

        /// <summary>用于搜索的文字：mod 名、本 mod 翻译出的 mod 名（可用中文搜英文 mod）、Paradox ID、类型名。</summary>
        private string[] SearchFields(ModTextInfo m)
        {
            string translatedName = null;
            foreach (string key in m.Source.Keys)
            {
                if (key.StartsWith("Options.SECTION[", StringComparison.Ordinal) && m_Live.TryGetValue(key, out var t))
                {
                    translatedName = t;
                    break;
                }
            }
            string id = m.Key.StartsWith("pdx_", StringComparison.Ordinal) ? m.Key.Substring(4) : m.Key;
            return new[] { m.DisplayName, translatedName, id, CategoryName(m.Category) };
        }

        /// <summary>按当前搜索词、类型、状态筛选并排序后的 mod 列表（结果按界面版本缓存）。</summary>
        private List<ModTextInfo> FilteredMods()
        {
            int version = s_UiVersion;
            if (m_FilteredCache != null && version == m_FilteredVersion)
            {
                return m_FilteredCache;
            }
            var result = new List<ModTextInfo>();
            if (m_Scan != null)
            {
                string query = m_Setting.SearchText?.Trim() ?? string.Empty;
                var category = m_Setting.CategoryFilter;
                var status = m_Setting.StatusFilter;
                var scored = new List<(ModTextInfo mod, int score)>();
                foreach (var m in m_Scan.Mods.Values)
                {
                    if (!IsListed(m) || (category != Setting.ModCategory.All && m.Category != category) || !MatchesStatus(m, status))
                    {
                        continue;
                    }
                    int score = FuzzyMatcher.Score(query, SearchFields(m));
                    if (score > 0)
                    {
                        scored.Add((m, score));
                    }
                }
                bool searching = query.Length > 0;
                result = scored
                    // 有搜索词时按匹配度排序，否则按“需要处理的在前”排序。
                    .OrderByDescending(p => searching ? p.score : 0)
                    .ThenBy(p => p.mod.State == ModState.NeedsTranslation ? 0 : p.mod.MissingDescriptions.Count > 0 ? 1 : NeedsPolish(p.mod) ? 2 : p.mod.State == ModState.Translated ? 3 : 4)
                    .ThenByDescending(p => p.mod.Key == ModScanner.kSelfKey)
                    .ThenBy(p => p.mod.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .Select(p => p.mod)
                    .ToList();
            }
            m_FilteredCache = result;
            m_FilteredVersion = version;
            return result;
        }

        /// <summary>搜索框下方的联想提示：匹配数量 + 前几个结果。</summary>
        public string SearchSummary
        {
            get
            {
                if (m_Scan == null)
                {
                    return L("NotScanned");
                }
                var matches = FilteredMods();
                int listed = m_Scan.Mods.Values.Count(IsListed);
                if (m_Setting.HasNoFilter())
                {
                    return L("SearchHint", listed);
                }
                if (matches.Count == 0)
                {
                    return L("SearchNone");
                }
                string top = string.Join("、", matches.Take(5).Select(m => m.DisplayName));
                return matches.Count > 5
                    ? L("SearchResultsMore", matches.Count, listed, top)
                    : L("SearchResults", matches.Count, listed, top);
            }
        }

        /// <summary>搜索词或筛选条件变了：刷新列表，并把选择切到最匹配的 mod（联想）。</summary>
        public void OnFilterChanged()
        {
            BumpUi();
            if (m_Scan == null)
            {
                return;
            }
            var matches = FilteredMods();
            bool selectedVisible = matches.Any(m => m.Key == m_Setting.SelectedMod);
            if (matches.Count > 0 && (!selectedVisible || !string.IsNullOrWhiteSpace(m_Setting.SearchText)))
            {
                m_Setting.SelectedMod = matches[0].Key;
            }
            else if (matches.Count == 0)
            {
                m_Setting.SelectedMod = Setting.kNoneMod;
            }
            BumpUi();
        }

        private static string CategoryName(Setting.ModCategory category) => L("Category" + category);

        private static string CategoryDetail(ModTextInfo m)
        {
            if (m.PrefabCounts.Count == 0)
            {
                return CategoryName(m.Category);
            }
            string counts = string.Join(" · ", m.PrefabCounts.OrderByDescending(p => p.Value).Select(p => L("CountOf" + p.Key, p.Value)));
            return L("CategoryWithCounts", CategoryName(m.Category), counts);
        }

        private string Label(ModTextInfo m)
        {
            if (m.State != ModState.NeedsTranslation && m.MissingDescriptions.Count > 0)
            {
                return L("TagMissingDescriptions", m.MissingDescriptions.Count) + m.DisplayName;
            }
            switch (m.State)
            {
                case ModState.NeedsTranslation:
                    return L(m.Outdated == m.Pending.Count ? "TagUpdate" : "TagPending", m.Pending.Count) + m.DisplayName;
                case ModState.Translated:
                    return m_Quality.TryGetValue(m.Key, out var q) && q.Suspicious > 0
                        ? L("TagNeedsPolish", q.Suspicious) + m.DisplayName
                        : L("TagTranslated") + m.DisplayName;
                default:
                    return L("TagNotNeeded") + m.DisplayName;
            }
        }

        public string DescribeMod(string key)
        {
            if (m_Scan == null)
            {
                return L("NotScanned");
            }
            if (string.IsNullOrEmpty(key) || !m_Scan.Mods.TryGetValue(key, out var m))
            {
                return L("NothingSelected");
            }
            var lines = new List<string> { L("ModCategoryLine", CategoryDetail(m)) };
            if (m.Translatable > 0)
            {
                lines.Add(L("ModText", m.Translatable, m.Native, m.Done, m.Pending.Count));
            }
            if (m.Outdated > 0)
            {
                lines.Add(L("ModOutdated", m.Outdated));
            }
            if (m_Quality.TryGetValue(m.Key, out var q) && q.Checked > 0)
            {
                lines.Add(q.Reviewed > 0
                    ? L("ModQualityReviewed", q.AverageScore.ToString("0.0"), q.Reviewed, q.Suspicious)
                    : L("ModQuality", q.Suspicious));
            }
            if (m.MixedNative.Count > 0)
            {
                lines.Add(L("ModMixed", m.MixedNative.Count));
            }
            if (m.MissingDescriptions.Count > 0 || m.GeneratedDescriptions > 0)
            {
                lines.Add(m.MissingNames > 0
                    ? L("ModDescriptionsNames", m.MissingDescriptions.Count, m.MissingNames, m.GeneratedDescriptions)
                    : L("ModDescriptions", m.MissingDescriptions.Count, m.GeneratedDescriptions));
            }
            return string.Join("\n", lines);
        }

        public bool CanReview(string key)
        {
            return m_Scan != null && !string.IsNullOrEmpty(key) && m_Scan.Mods.TryGetValue(key, out var m) && m.Done > 0;
        }

        public bool CanTranslate(string key)
        {
            return m_Scan != null && !string.IsNullOrEmpty(key) && m_Scan.Mods.TryGetValue(key, out var m) && m.OwnKeys.Count > 0;
        }

        public bool HasTranslation(string key)
        {
            if (m_Scan == null || string.IsNullOrEmpty(key))
            {
                return false;
            }
            var saved = Store.Get(m_Scan.Locale, key);
            var described = Store.Get(m_Scan.Locale, TranslationStore.DescriptionGroup(key));
            return (saved != null && saved.Translations.Count > 0) || (described != null && described.Translations.Count > 0);
        }

        // ================================================================ 翻译 / 审校任务

        private sealed class WorkItem
        {
            public string Key;
            public string Source;
            public string Translation;     // 审校任务：现有译文
            public List<string> Issues;    // 审校任务：启发式疑点
            public AssetContext Asset;     // 描述任务：资产信息
            public string Original;        // 修正混杂任务：英文原文（Source 存放被检查的那条译文）
            public string Resolved;        // 翻译任务：已由“类型短语 + 编号”拼好的译文，无需调用 API
            public bool Unresolved;        // 翻译任务：类型短语没翻译成功，本次跳过
        }

        private sealed class BatchOutcome
        {
            public readonly Dictionary<string, string> Translations = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> Scores = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly List<(string before, string after)> Rewrites = new List<(string, string)>();
            public int Fixed;
            public int Failed;
            public string Warning;
        }

        private enum JobKind
        {
            Translate,
            Review,
            Describe,
            FixMixed,
        }

        public void TranslateAllNeeded() => TranslateAllNeeded(fromStartup: false);

        private void TranslateAllNeeded(bool fromStartup)
        {
            if (m_Scan == null)
            {
                return;
            }
            var keys = m_Scan.Mods.Values
                .Where(m => m.State == ModState.NeedsTranslation)
                .Where(m => !fromStartup || !m_Setting.AutoTranslateSkipLarge || m.Pending.Count <= kLargeModThreshold)
                .OrderByDescending(m => m.Key == ModScanner.kSelfKey) // 先翻本 mod 自己的界面
                .ThenBy(m => m.Pending.Count)
                .Select(m => m.Key)
                .ToList();
            if (fromStartup && string.IsNullOrWhiteSpace(m_Setting.ApiKey))
            {
                return;
            }
            bool describe = m_Setting.AutoDescribe && m_Scan.MissingDescriptions > 0;
            if (keys.Count == 0)
            {
                if (describe)
                {
                    DescribeAll();
                }
                else if (!fromStartup)
                {
                    m_Progress = T("NothingToTranslate");
                    BumpUi();
                }
                return;
            }
            // 先翻译，完成后再为缺少描述的资产生成描述（此时资产名称已有译名，描述更准确）。
            m_ChainDescribeKeys = describe ? m_Scan.Mods.Keys.ToList() : null;
            TranslateMods(keys, force: false);
        }

        // 翻译完成后要接着生成资产描述的 mod；null 表示不生成。

        // ---------------------------------------------------------------- 新 mod 自动后台翻译

        // 已见过的 mod；null 表示还没初始化（要等所有 mod 加载完才能判断哪些是新的）。
        private HashSet<string> m_KnownMods;

        // 等待后台翻译的新 mod / 更新过的 mod。
        private readonly List<string> m_AutoQueue = new List<string>();

        // 本次运行中已自动处理过的 mod，避免失败后反复重试。
        private readonly HashSet<string> m_AutoAttempted = new HashSet<string>(StringComparer.Ordinal);

        private bool m_MissingKeyLogged;

        /// <summary>所有 mod 加载完成后调用一次：读取已知 mod 列表；首次运行时把现有 mod 全部记为已知，不自动翻译。</summary>
        private void InitKnownMods()
        {
            if (m_KnownMods != null || m_Scan == null)
            {
                return;
            }
            m_KnownMods = Store.LoadKnownMods();
            if (m_KnownMods == null)
            {
                m_KnownMods = new HashSet<string>(m_Scan.Mods.Keys, StringComparer.Ordinal);
                Safe(() => Store.SaveKnownMods(m_KnownMods), "save known mods");
                Mod.Log.Info($"Known mod list initialized with {m_KnownMods.Count} mods");
                return;
            }
            DetectNewMods();
        }

        /// <summary>对比已知列表，找出新安装（以及更新后有文本变化）的 mod，加入后台翻译队列。</summary>
        private void DetectNewMods()
        {
            if (m_KnownMods == null || m_Scan == null)
            {
                return;
            }
            var newMods = new List<ModTextInfo>();
            foreach (var info in m_Scan.Mods.Values)
            {
                if (m_KnownMods.Add(info.Key))
                {
                    newMods.Add(info);
                }
            }
            if (newMods.Count > 0)
            {
                Safe(() => Store.SaveKnownMods(m_KnownMods), "save known mods");
                var listed = newMods.Where(IsListed).ToList();
                if (listed.Count > 0)
                {
                    ActivityLog.Info(T("LogNewMods", listed.Count, string.Join("、", listed.Take(5).Select(m => m.DisplayName)) + (listed.Count > 5 ? " …" : string.Empty)));
                }
            }
            if (!m_Setting.AutoTranslateNewMods)
            {
                return;
            }

            var candidates = newMods
                .Concat(m_Scan.Mods.Values.Where(m => m.Outdated > 0)) // mod 更新后文本有变化
                .Where(m => !m_AutoAttempted.Contains(m.Key) && !m_AutoQueue.Contains(m.Key))
                .Where(m => m.Pending.Count > 0 || (m_Setting.AutoDescribe && m.MissingDescriptions.Count > 0))
                .Where(m => !m_Setting.AutoTranslateSkipLarge || m.Pending.Count <= kLargeModThreshold)
                .Select(m => m.Key)
                .Distinct()
                .ToList();
            if (candidates.Count > 0)
            {
                m_AutoQueue.AddRange(candidates);
                Mod.Log.Info("Queued for background translation: " + string.Join(", ", candidates));
            }
        }

        /// <summary>空闲时开始翻译队列里的 mod（翻译完接着为它们生成资产描述）。</summary>
        private void RunAutoQueue()
        {
            var keys = m_AutoQueue.Where(k => m_Scan.Mods.ContainsKey(k)).Distinct().ToList();
            m_AutoQueue.Clear();
            if (keys.Count == 0)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(m_Setting.ApiKey))
            {
                if (!m_MissingKeyLogged)
                {
                    m_MissingKeyLogged = true;
                    ActivityLog.Warning(T("LogAutoNeedsKey"));
                }
                return;
            }
            foreach (string k in keys)
            {
                m_AutoAttempted.Add(k);
            }
            var names = keys.Select(k => m_Scan.Mods[k].DisplayName).ToList();
            ActivityLog.Info(T("LogAutoStart", names.Count, string.Join("、", names.Take(5)) + (names.Count > 5 ? " …" : string.Empty)));
            Notify(L("NotifyAutoStart", names.Count), ProgressState.Progressing, 0, pop: false);

            var toTranslate = keys.Where(k => m_Scan.Mods[k].Pending.Count > 0).ToList();
            var describeKeys = m_Setting.AutoDescribe ? keys : null;
            if (toTranslate.Count > 0)
            {
                m_ChainDescribeKeys = describeKeys;
                TranslateMods(toTranslate, force: false);
            }
            else if (describeKeys != null)
            {
                DescribeMods(describeKeys);
            }
        }
        private List<string> m_ChainDescribeKeys;

        public void TranslateMods(IEnumerable<string> modKeys, bool force)
        {
            if (!CanStartJob())
            {
                return;
            }
            var work = new List<(ModTextInfo mod, List<WorkItem> items)>();
            foreach (string key in modKeys)
            {
                if (string.IsNullOrEmpty(key) || !m_Scan.Mods.TryGetValue(key, out var info))
                {
                    continue;
                }
                var keys = force ? info.OwnKeys : info.Pending;
                if (keys.Count > 0)
                {
                    work.Add((info, keys.Select(k => new WorkItem { Key = k, Source = info.Source[k] }).ToList()));
                }
            }
            if (work.Count == 0)
            {
                m_Progress = T("SelectionUpToDate");
                BumpUi();
                return;
            }
            StartJob(JobKind.Translate, work);
        }

        public void ReviewSelected(string modKey) => ReviewMods(new[] { modKey });

        public void ReviewAll() => ReviewMods(m_Scan?.Mods.Keys.ToList() ?? new List<string>());

        /// <summary>对已保存的译文做 AI 审校，按设置的范围挑选条目，低分条目自动改进。</summary>
        public void ReviewMods(IEnumerable<string> modKeys)
        {
            if (!CanStartJob())
            {
                return;
            }
            var scope = m_Setting.ReviewScope;
            var work = new List<(ModTextInfo mod, List<WorkItem> items)>();
            foreach (string key in modKeys)
            {
                if (string.IsNullOrEmpty(key) || !m_Scan.Mods.TryGetValue(key, out var info))
                {
                    continue;
                }
                var saved = Store.Get(m_Scan.Locale, key);
                if (saved == null)
                {
                    continue;
                }
                m_Quality.TryGetValue(key, out var quality);
                var items = new List<WorkItem>();
                foreach (string k in info.OwnKeys)
                {
                    // 由“统一类型短语 + 后缀”拼出的建筑名格式固定，不送审校（改写后又会被检测为格式不对而还原，白白花钱）。
                    if (!saved.Translations.TryGetValue(k, out var t) || info.Pending.Contains(k) || ModScanner.IsFixedFormat(k, info.Source[k]))
                    {
                        continue;
                    }
                    List<string> issues = null;
                    quality?.Issues.TryGetValue(k, out issues);
                    bool reviewed = saved.Scores.ContainsKey(k);
                    // AI 审过（有评分）且译文没变的条目已经定稿：只有选「全部条目」才会再审，避免每次都把同一批反复改写。
                    bool take = scope == Setting.ReviewScopeOption.All
                        || (scope == Setting.ReviewScopeOption.Unreviewed && !reviewed)
                        || (scope == Setting.ReviewScopeOption.Suspicious && issues != null && !reviewed);
                    if (take)
                    {
                        items.Add(new WorkItem { Key = k, Source = info.Source[k], Translation = t, Issues = issues });
                    }
                }
                if (items.Count > 0)
                {
                    work.Add((info, items));
                }
            }
            if (work.Count == 0)
            {
                m_Progress = T("NothingToReview");
                BumpUi();
                return;
            }
            StartJob(JobKind.Review, work);
        }

        private bool CanStartJob()
        {
            if (IsBusy || m_Scan == null)
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(m_Setting.ApiKey))
            {
                m_Progress = T("NeedApiKey");
                BumpUi();
                return false;
            }
            return true;
        }

        private void StartJob(JobKind kind, List<(ModTextInfo mod, List<WorkItem> items)> work)
        {
            var client = new ChatClient(m_Setting.ApiUrl, m_Setting.ApiKey, m_Setting.ModelName, m_Setting.Temperature);
            string locale = m_Scan.Locale;
            int batchSize = Math.Max(1, m_Setting.BatchSize);
            int concurrency = Math.Max(1, m_Setting.Concurrency);
            bool useGlossary = m_Setting.UseGameGlossary;
            bool reviewAfter = kind == JobKind.Translate && m_Setting.AutoReview;
            string extra = m_Setting.ExtraPrompt;
            Task<Glossary> glossaryTask = m_GlossaryTask;
            IReadOnlyDictionary<string, string> summaries = kind == JobKind.Describe ? GetModSummaries() : new Dictionary<string, string>();
            IReadOnlyDictionary<string, string> officialPhrases = m_Scan.OfficialBuildingPhrases;
            ProtectedNames protectedNames = m_Scan.ProtectedNames;
            IReadOnlyDictionary<string, (string english, string translated)> zoneNames = m_Scan.ZoneNames;

            m_Cts = new CancellationTokenSource();
            CancellationToken ct = m_Cts.Token;
            m_Busy = true;
            m_JobKind = kind;
            m_JobTotal = work.Sum(w => w.items.Count);
            m_JobDone = 0;
            m_JobFailed = 0;
            m_JobFixed = 0;
            m_JobScores = new List<int>();
            m_LastNotifiedPercent = -1;
            m_JobCurrent = work[0].mod.DisplayName;
            m_JobLanguage = LanguageDisplayName(locale);
            m_JobWatch = Stopwatch.StartNew();
            UpdateProgressText();
            Mod.Log.Info($"Start {kind} job: {work.Count} mod(s), {m_JobTotal} entries → {locale} via {client.Endpoint} ({client.Model}), review={reviewAfter || kind == JobKind.Review}");
            ActivityLog.Info(T(kind == JobKind.FixMixed ? "LogStartFix" : kind == JobKind.Review ? "LogStartReview" : kind == JobKind.Describe ? "LogStartDescribe" : "LogStartTranslate", work.Count, m_JobTotal, m_JobLanguage, client.Model));

            Task.Run(async () =>
            {
                string error = null;
                try
                {
                    Glossary glossary = Glossary.Empty;
                    if (useGlossary && glossaryTask != null)
                    {
                        glossary = await glossaryTask.ConfigureAwait(false);
                    }
                    var translator = new Translator(client, locale, glossary, extra, protectedNames);
                    if (kind == JobKind.Translate)
                    {
                        await ResolveBuildingNamesAsync(translator, work, officialPhrases, Store, locale, ct, zoneNames).ConfigureAwait(false);
                        await ResolveModTitlesAsync(translator, work, ct).ConfigureAwait(false);
                    }
                    // 审校请求比翻译请求长（带原文和译文），批次减半。
                    int size = kind == JobKind.Review ? Math.Max(5, batchSize / 2) : kind == JobKind.Describe ? Math.Max(5, Math.Min(20, batchSize)) : batchSize;
                    var batches = work.SelectMany(w => Chunk(w.items, size).Select(b => (w.mod, b))).ToList();

                    using (var gate = new SemaphoreSlim(concurrency))
                    {
                        var tasks = batches.Select(async batch =>
                        {
                            await gate.WaitAsync(ct).ConfigureAwait(false);
                            try
                            {
                                m_JobCurrent = batch.mod.DisplayName;
                                BatchOutcome outcome = kind == JobKind.Translate
                                    ? await TranslateBatchAsync(translator, glossary, locale, batch.mod.DisplayName, batch.b, reviewAfter, ct).ConfigureAwait(false)
                                    : kind == JobKind.Review
                                        ? await ReviewBatchAsync(translator, batch.mod.DisplayName, batch.b, ct).ConfigureAwait(false)
                                        : kind == JobKind.FixMixed
                                            ? await FixMixedBatchAsync(translator, batch.mod.DisplayName, batch.b, locale, protectedNames, ct).ConfigureAwait(false)
                                            : await DescribeBatchAsync(translator, batch.mod.DisplayName, summaries, batch.mod.Key, batch.b, ct).ConfigureAwait(false);
                                Interlocked.Add(ref m_JobDone, batch.b.Count);
                                Interlocked.Add(ref m_JobFailed, outcome.Failed);
                                Interlocked.Add(ref m_JobFixed, outcome.Fixed);
                                lock (m_JobScores)
                                {
                                    m_JobScores.AddRange(outcome.Scores.Values);
                                }
                                MainThreadDispatcher.RunOnMainThread(() => Safe(() => ApplyBatch(locale, batch.mod, batch.b, outcome), "apply"));
                            }
                            catch (ApiException ex) when (ex.Fatal)
                            {
                                error = ex.Message;
                                ActivityLog.Error(ex.Message);
                                m_Cts.Cancel();
                            }
                            catch (ApiException ex)
                            {
                                // 重试多次仍失败的批次：跳过，其余批次继续。
                                Mod.Log.Warn("Batch failed: " + ex.Message);
                                ActivityLog.Error(batch.mod.DisplayName + "：" + ex.Message);
                                error = error ?? ex.Message;
                                Interlocked.Add(ref m_JobDone, batch.b.Count);
                                Interlocked.Add(ref m_JobFailed, batch.b.Count);
                            }
                            finally
                            {
                                gate.Release();
                            }
                        }).ToList();

                        try
                        {
                            await Task.WhenAll(tasks).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // 取消或致命错误，下方统一处理。
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = error ?? ex.Message;
                    Mod.Log.Error(ex, "Job failed: " + ex.Message);
                }
                bool cancelled = ct.IsCancellationRequested;
                MainThreadDispatcher.RunOnMainThread(() => Safe(() => FinishJob(error, cancelled), "finish"));
            });
        }

        /// <summary>后台：翻译一批，可选紧接着审校并改进低分译文。</summary>
        private static async Task<BatchOutcome> TranslateBatchAsync(Translator translator, Glossary glossary, string locale, string modName, List<WorkItem> batch, bool review, CancellationToken ct)
        {
            var outcome = new BatchOutcome();
            // 已拼好的区域建筑名直接使用，不再请求（格式与官方一致，也不送审校）。
            foreach (var w in batch.Where(w => w.Resolved != null))
            {
                outcome.Translations[w.Key] = w.Resolved;
            }
            var items = batch.Where(w => w.Resolved == null && !w.Unresolved).Select(w => new TranslateItem { Key = w.Key, Source = w.Source }).ToList();
            var translated = items.Count > 0
                ? await translator.TranslateAsync(modName, items, ct).ConfigureAwait(false)
                : new Dictionary<string, string>();
            foreach (var kv in translated)
            {
                outcome.Translations[kv.Key] = kv.Value;
            }
            outcome.Failed = items.Count - translated.Count + batch.Count(w => w.Unresolved);

            if (review && translated.Count > 0)
            {
                var reviewItems = batch
                    .Where(w => w.Resolved == null && translated.ContainsKey(w.Key))
                    .Select(w => new Translator.ReviewItem
                    {
                        Key = w.Key,
                        Source = w.Source,
                        Translation = translated[w.Key],
                        Issues = QualityChecker.Check(w.Source, translated[w.Key], locale, glossary),
                    })
                    .ToList();
                try
                {
                    MergeReview(outcome, await translator.ReviewAsync(modName, reviewItems, ct).ConfigureAwait(false), translated);
                }
                catch (ApiException ex) when (!ex.Fatal)
                {
                    // 审校失败不影响已经得到的译文。
                    Mod.Log.Warn("Review after translation failed: " + ex.Message);
                    outcome.Warning = ex.Message;
                }
            }
            return outcome;
        }

        /// <summary>
        /// 后台：区域建筑名（如 "CN Residential Low 03 - L2 2x4"）按“类型短语”分组，每个短语只翻译一次，
        /// 再拼回编号/等级/占地后缀，保证同类建筑叫法统一并与游戏官方格式一致（"欧洲低层住宅 01 - L1 2x2"）。
        /// </summary>
        private static async Task ResolveBuildingNamesAsync(Translator translator, List<(ModTextInfo mod, List<WorkItem> items)> work,
            IReadOnlyDictionary<string, string> officialPhrases, TranslationStore store, string locale, CancellationToken ct,
            IReadOnlyDictionary<string, (string english, string translated)> zones = null)
        {
            var templated = new List<(WorkItem item, string phrase, string suffix)>();
            foreach (var (_, items) in work)
            {
                foreach (var item in items)
                {
                    var template = ModScanner.TemplateForEntry(item.Key, item.Source);
                    if (template != null)
                    {
                        templated.Add((item, template.Value.phrase, template.Value.suffix));
                    }
                }
            }
            if (templated.Count == 0)
            {
                return;
            }

            // 译名来源优先级：原版官方 > 已缓存（含玩家手动修改） > 新请求。
            var cache = store.LoadBuildingPhrases(locale);
            var phraseTranslations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string phrase in templated.Select(t => t.phrase).Distinct())
            {
                if (officialPhrases.TryGetValue(phrase, out var official))
                {
                    phraseTranslations[phrase] = official;
                }
                else if (cache.TryGetValue(phrase, out var cached))
                {
                    phraseTranslations[phrase] = cached;
                }
            }
            var unknown = templated.Select(t => t.phrase).Distinct().Where(p => !phraseTranslations.ContainsKey(p)).ToList();
            if (unknown.Count > 0)
            {
                var result = await translator.TranslateBuildingTypesAsync(unknown, officialPhrases, ct, zones).ConfigureAwait(false);
                foreach (var kv in result)
                {
                    phraseTranslations[kv.Key] = kv.Value;
                    cache[kv.Key] = kv.Value;
                }
                if (result.Count > 0)
                {
                    MainThreadDispatcher.RunOnMainThread(() => store.SaveBuildingPhrases(locale, cache));
                    ActivityLog.Info(string.Join("、", result.Take(8).Select(p => p.Key + " → " + p.Value)) + (result.Count > 8 ? " …" : string.Empty));
                }
            }

            foreach (var (item, phrase, suffix) in templated)
            {
                if (phraseTranslations.TryGetValue(phrase, out var translatedPhrase))
                {
                    item.Resolved = translatedPhrase + suffix;
                }
                else
                {
                    // 短语没翻译成功：这次跳过，不逐条单独翻译（逐条翻译格式不统一，下次检测还会被判为需更新）。
                    item.Unresolved = true;
                }
            }
        }

        /// <summary>
        /// 后台：设置页标题（mod 名）单独请求一个简短译名，拼成“译名 (原名)”。
        /// 不走普通翻译（普通翻译要求 mod 名保留原样），也不送审校。
        /// </summary>
        private static async Task ResolveModTitlesAsync(Translator translator, List<(ModTextInfo mod, List<WorkItem> items)> work, CancellationToken ct)
        {
            var titles = work.SelectMany(w => w.items).Where(i => i.Resolved == null && ModScanner.IsModTitleKey(i.Key)).ToList();
            if (titles.Count == 0)
            {
                return;
            }
            var names = titles.Select(t => t.Source.Trim()).Distinct().ToList();
            var localized = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                for (int i = 0; i < names.Count; i += 40)
                {
                    foreach (var kv in await translator.TranslateModNamesAsync(names.Skip(i).Take(40).ToList(), ct).ConfigureAwait(false))
                    {
                        localized[kv.Key] = kv.Value;
                    }
                }
            }
            catch (ApiException ex) when (!ex.Fatal)
            {
                // 译名失败只影响标题，这次跳过，下次再试；其他条目照常翻译。
                Mod.Log.Warn("Mod title request failed: " + ex.Message);
            }
            foreach (var item in titles)
            {
                if (localized.TryGetValue(item.Source.Trim(), out var name))
                {
                    item.Resolved = ModScanner.ComposeModTitle(name, item.Source);
                }
                else
                {
                    item.Unresolved = true;
                }
            }
            if (localized.Count > 0)
            {
                ActivityLog.Info(string.Join("、", titles.Where(t => t.Resolved != null).Take(8).Select(t => t.Resolved)) + (titles.Count > 8 ? " …" : string.Empty));
            }
        }

        private static string PrefabNameFromKey(string key)
        {
            foreach (string prefix in new[] { "Assets.NAME[", "Assets.UPGRADE_NAME[" })
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal) && key.EndsWith("]", StringComparison.Ordinal))
                {
                    return key.Substring(prefix.Length, key.Length - prefix.Length - 1);
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- 修正官方/自带译文的中英混杂

        public void FixMixedSelected(string modKey) => FixMixedMods(new[] { modKey });

        public void FixMixedAll() => FixMixedMods(m_Scan?.Mods.Keys.ToList() ?? new List<string>());

        public bool CanFixMixed(string key)
        {
            return m_Scan != null && !string.IsNullOrEmpty(key) && m_Scan.Mods.TryGetValue(key, out var m) && m.MixedNative.Count > 0;
        }

        /// <summary>让 AI 逐条判断官方/自带译文里照搬的英文该不该翻译；该翻的改写，品牌、人名、技术名词保留。</summary>
        public void FixMixedMods(IEnumerable<string> modKeys)
        {
            if (!CanStartJob())
            {
                return;
            }
            var work = new List<(ModTextInfo mod, List<WorkItem> items)>();
            foreach (string key in modKeys)
            {
                if (string.IsNullOrEmpty(key) || !m_Scan.Mods.TryGetValue(key, out var info) || info.MixedNative.Count == 0)
                {
                    continue;
                }
                work.Add((info, info.MixedNative
                    .Select(x => new WorkItem { Key = x.key, Source = x.current, Original = x.source, Translation = x.current, Issues = new List<string> { QualityChecker.kMixed } })
                    .ToList()));
            }
            if (work.Count == 0)
            {
                m_Progress = T("NothingToFix");
                BumpUi();
                return;
            }
            StartJob(JobKind.FixMixed, work);
        }

        /// <summary>后台：审校一批中英混杂的官方译文。判断为不用改的也记下来（存原样），以后不再重复检查。</summary>
        private static async Task<BatchOutcome> FixMixedBatchAsync(Translator translator, string modName, List<WorkItem> batch, string locale, ProtectedNames names, CancellationToken ct)
        {
            var outcome = new BatchOutcome();
            var reviewItems = batch.Select(w => new Translator.ReviewItem { Key = w.Key, Source = w.Original, Translation = w.Translation, Issues = w.Issues }).ToList();
            var reviews = await translator.ReviewAsync(modName, reviewItems, ct).ConfigureAwait(false);
            foreach (var w in batch)
            {
                if (!reviews.TryGetValue(w.Key, out var review))
                {
                    outcome.Failed++;
                    continue;
                }
                // 只采用真正减少了照搬英文的改写；只是换个说法的不算修正（还可能把官方用词改坏）。
                bool improves = review.Fix != null
                    && QualityChecker.LeftoverEnglish(w.Original, review.Fix, locale, names, modName)
                        < QualityChecker.LeftoverEnglish(w.Original, w.Translation, locale, names, modName);
                if (improves)
                {
                    outcome.Translations[w.Key] = review.Fix;
                    outcome.Rewrites.Add((w.Translation, review.Fix));
                    outcome.Fixed++;
                }
                else
                {
                    outcome.Translations[w.Key] = w.Translation; // 保持原样，仅作已检查标记
                }
                outcome.Scores[w.Key] = improves ? 4 : review.Score;
            }
            return outcome;
        }

        /// <summary>后台：为一批资产生成描述。</summary>
        private static async Task<BatchOutcome> DescribeBatchAsync(Translator translator, string modName, IReadOnlyDictionary<string, string> summaries, string modKey, List<WorkItem> batch, CancellationToken ct)
        {
            var outcome = new BatchOutcome();
            if (!summaries.TryGetValue(modKey, out var summary) && ModScanner.IsContentPack(modKey))
            {
                summary = "Official DLC / creator pack for Cities: Skylines II.";
            }
            var generated = await translator.DescribeAsync(modName, summary, batch.Select(w => w.Asset).ToList(), ct).ConfigureAwait(false);
            foreach (var kv in generated)
            {
                outcome.Translations[kv.Key] = kv.Value;
            }
            outcome.Failed = batch.Count - batch.Count(w => generated.ContainsKey(w.Key));
            return outcome;
        }

        // ---------------------------------------------------------------- 资产描述

        public void DescribeSelected(string modKey) => DescribeMods(new[] { modKey });

        public void DescribeAll() => DescribeMods(m_Scan?.Mods.Keys.ToList() ?? new List<string>());

        public bool CanDescribe(string key)
        {
            return m_Scan != null && !string.IsNullOrEmpty(key) && m_Scan.Mods.TryGetValue(key, out var m) && m.MissingDescriptions.Count > 0;
        }

        /// <summary>为缺少描述的资产生成描述（目标语言为游戏当前语言）。</summary>
        public void DescribeMods(IEnumerable<string> modKeys)
        {
            if (!CanStartJob())
            {
                return;
            }
            var work = new List<(ModTextInfo mod, List<WorkItem> items)>();
            foreach (string key in modKeys)
            {
                if (string.IsNullOrEmpty(key) || !m_Scan.Mods.TryGetValue(key, out var info) || info.MissingDescriptions.Count == 0)
                {
                    continue;
                }
                work.Add((info, info.MissingDescriptions
                    .Select(a => new WorkItem { Key = a.DescriptionKey, Source = a.ToString(), Asset = a })
                    .ToList()));
            }
            if (work.Count == 0)
            {
                m_Progress = T("NothingToDescribe");
                BumpUi();
                return;
            }
            StartJob(JobKind.Describe, work);
        }

        private Dictionary<string, string> m_ModSummaries;

        /// <summary>从 Paradox Mods 本地缓存读取各 mod 的简介（"pdx_&lt;id&gt;" → 简介），帮助模型理解资产用途。</summary>
        private Dictionary<string, string> GetModSummaries()
        {
            if (m_ModSummaries != null)
            {
                return m_ModSummaries;
            }
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                string file = Path.Combine(EnvPath.kUserDataPath, ".cache", "Mods", "pdx_mods_cache.json");
                if (File.Exists(file))
                {
                    var items = JObject.Parse(File.ReadAllText(file))["items"] as JArray;
                    foreach (JToken item in items ?? new JArray())
                    {
                        string id = item["id"]?.ToString();
                        string text = item["shortDescription"]?.ToString();
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            text = item["longDescription"]?.ToString();
                        }
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrWhiteSpace(text))
                        {
                            result["pdx_" + id] = text.Length > 600 ? text.Substring(0, 600) : text;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Mod.Log.Warn("Cannot read mod summaries: " + ex.Message);
            }
            m_ModSummaries = result;
            return result;
        }

        /// <summary>后台：审校一批已有译文。</summary>
        private static async Task<BatchOutcome> ReviewBatchAsync(Translator translator, string modName, List<WorkItem> batch, CancellationToken ct)
        {
            var outcome = new BatchOutcome();
            var reviewItems = batch.Select(w => new Translator.ReviewItem { Key = w.Key, Source = w.Source, Translation = w.Translation, Issues = w.Issues }).ToList();
            var reviews = await translator.ReviewAsync(modName, reviewItems, ct).ConfigureAwait(false);
            MergeReview(outcome, reviews, batch.ToDictionary(w => w.Key, w => w.Translation, StringComparer.Ordinal));
            outcome.Failed = batch.Count - reviews.Count;
            return outcome;
        }

        private static void MergeReview(BatchOutcome outcome, Dictionary<string, Translator.ReviewResult> reviews, IReadOnlyDictionary<string, string> before)
        {
            foreach (var kv in reviews)
            {
                if (kv.Value.Fix != null)
                {
                    if (before.TryGetValue(kv.Key, out var old))
                    {
                        outcome.Rewrites.Add((old, kv.Value.Fix));
                    }
                    outcome.Translations[kv.Key] = kv.Value.Fix;
                    // 已按审校意见改进，记为合格；下次审校“仅可疑”时若仍被检出会再次处理。
                    outcome.Scores[kv.Key] = 4;
                    outcome.Fixed++;
                }
                else
                {
                    outcome.Scores[kv.Key] = kv.Value.Score;
                }
            }
        }

        private static IEnumerable<List<WorkItem>> Chunk(List<WorkItem> items, int size)
        {
            var batch = new List<WorkItem>();
            int chars = 0;
            foreach (var item in items)
            {
                int len = item.Source.Length + item.Key.Length + (item.Translation?.Length ?? 0);
                if (batch.Count > 0 && (batch.Count >= size || chars + len > kMaxBatchChars))
                {
                    yield return batch;
                    batch = new List<WorkItem>();
                    chars = 0;
                }
                batch.Add(item);
                chars += len;
            }
            if (batch.Count > 0)
            {
                yield return batch;
            }
        }

        private void ApplyBatch(string locale, ModTextInfo mod, List<WorkItem> batch, BatchOutcome outcome)
        {
            if (outcome.Translations.Count > 0 || outcome.Scores.Count > 0)
            {
                string storeKey = m_JobKind == JobKind.Describe ? TranslationStore.DescriptionGroup(mod.Key)
                    : m_JobKind == JobKind.FixMixed ? TranslationStore.FixGroup(mod.Key)
                    : mod.Key;
                var data = Store.GetOrCreate(locale, storeKey);
                foreach (var item in batch)
                {
                    bool keepOwnDescription = item.Asset != null && !string.IsNullOrEmpty(item.Asset.OwnDescription);
                    if (!keepOwnDescription && outcome.Translations.TryGetValue(item.Key, out var t))
                    {
                        data.Translations[item.Key] = t;
                        data.Sources[item.Key] = item.Source;
                    }
                    // 描述任务顺带为随机编号资产起的名字，与描述一起存放。
                    if (item.Asset != null && item.Asset.NeedsName && outcome.Translations.TryGetValue(item.Asset.TitleKey, out var generatedTitle))
                    {
                        data.Translations[item.Asset.TitleKey] = generatedTitle;
                        data.Sources[item.Asset.TitleKey] = item.Asset.PrefabName;
                    }
                    if (outcome.Scores.TryGetValue(item.Key, out int score))
                    {
                        data.Scores[item.Key] = score;
                    }
                    else if (outcome.Translations.ContainsKey(item.Key))
                    {
                        data.Scores.Remove(item.Key); // 新译文尚未审校
                    }
                }
                Store.Save(locale, storeKey, mod.DisplayName);

                // 翻译途中切换了游戏语言时只保存，不注入；资产自带的描述不被生成的描述替换。
                var live = outcome.Translations
                    .Where(kv => !batch.Any(w => w.Key == kv.Key && w.Asset != null && !string.IsNullOrEmpty(w.Asset.OwnDescription)))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                if (live.Count > 0 && string.Equals(locale, m_LiveLocale, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var kv in live)
                    {
                        m_Live[kv.Key] = kv.Value;
                    }
                    InjectEntries(live);
                }
            }
            UpdateStateAfterBatch(mod, batch, outcome);
            LogBatch(mod, batch, outcome);
            UpdateProgressText();
        }

        /// <summary>把一批结果直接更新到当前检测状态（待翻译 → 已翻译、缺描述 → 已生成），代替任务结束后的整体重新检测。</summary>
        private void UpdateStateAfterBatch(ModTextInfo mod, List<WorkItem> batch, BatchOutcome outcome)
        {
            switch (m_JobKind)
            {
                case JobKind.Translate:
                    foreach (var item in batch)
                    {
                        if (outcome.Translations.ContainsKey(item.Key) && mod.Pending.Remove(item.Key))
                        {
                            mod.Done++;
                            if (!mod.OwnKeys.Contains(item.Key))
                            {
                                mod.OwnKeys.Add(item.Key);
                            }
                        }
                    }
                    mod.Outdated = Math.Min(mod.Outdated, mod.Pending.Count);
                    break;
                case JobKind.Describe:
                    foreach (var item in batch)
                    {
                        if (!outcome.Translations.ContainsKey(item.Key))
                        {
                            continue;
                        }
                        int removed = mod.MissingDescriptions.RemoveAll(a => a.DescriptionKey == item.Key);
                        if (removed > 0)
                        {
                            mod.GeneratedDescriptions++;
                            if (item.Asset != null && item.Asset.NeedsName && outcome.Translations.ContainsKey(item.Asset.TitleKey))
                            {
                                mod.MissingNames = Math.Max(0, mod.MissingNames - 1);
                            }
                        }
                    }
                    break;
                case JobKind.FixMixed:
                    mod.MixedNative.RemoveAll(x => outcome.Translations.ContainsKey(x.key));
                    break;
            }
        }

        /// <summary>把一批结果写进「日志」标签页：一行摘要 + 少量样例明细。</summary>
        private void LogBatch(ModTextInfo mod, List<WorkItem> batch, BatchOutcome outcome)
        {
            const int kSamples = 2;
            const int kSampleLength = 36;
            // 列表倒序显示，先写明细，再写摘要，摘要就会出现在明细上方。
            var details = new List<LocText>();
            switch (m_JobKind)
            {
                case JobKind.Translate:
                    foreach (var item in batch.Where(w => outcome.Translations.TryGetValue(w.Key, out var t) && t != w.Source).Take(kSamples))
                    {
                        details.Add(T("LogSample", ActivityLog.Shorten(item.Source, kSampleLength), ActivityLog.Shorten(outcome.Translations[item.Key], kSampleLength)));
                    }
                    break;
                case JobKind.Describe:
                    foreach (var item in batch.Where(w => outcome.Translations.ContainsKey(w.Key)).Take(kSamples))
                    {
                        string shownName = item.Asset != null && outcome.Translations.TryGetValue(item.Asset.TitleKey, out var generatedName) ? generatedName : item.Asset?.Title;
                        details.Add(T("LogDescribeSample", ActivityLog.Shorten(shownName, 20), ActivityLog.Shorten(outcome.Translations[item.Key], 60)));
                    }
                    break;
            }
            foreach (var (before, after) in outcome.Rewrites.Take(kSamples))
            {
                details.Add(T("LogRewrite", ActivityLog.Shorten(before, kSampleLength), ActivityLog.Shorten(after, kSampleLength)));
            }
            details.Reverse();
            foreach (LocText d in details)
            {
                ActivityLog.Detail(d);
            }

            int ok = batch.Count - outcome.Failed;
            LocText line;
            switch (m_JobKind)
            {
                case JobKind.Review:
                    string average = outcome.Scores.Count > 0 ? outcome.Scores.Values.Average().ToString("0.0") : "-";
                    line = T("LogReviewBatch", mod.DisplayName, ok, outcome.Fixed, average);
                    break;
                case JobKind.FixMixed:
                    line = T("LogFixBatch", mod.DisplayName, ok, outcome.Fixed);
                    break;
                case JobKind.Describe:
                    line = T("LogDescribeBatch", mod.DisplayName, ok);
                    break;
                default:
                    line = outcome.Fixed > 0 ? T("LogTranslateBatchReviewed", mod.DisplayName, ok, outcome.Fixed) : T("LogTranslateBatch", mod.DisplayName, ok);
                    break;
            }
            if (outcome.Failed > 0)
            {
                line = LocText.Concat(line, T("LogFailedSuffix", outcome.Failed));
            }
            ActivityLog.Add(outcome.Failed > 0 ? ActivityLog.Level.Warning : ActivityLog.Level.Info, line);
            if (outcome.Warning != null)
            {
                ActivityLog.Warning(T("LogReviewFailed", mod.DisplayName, outcome.Warning));
            }
        }

        /// <summary>「日志」标签页的显示文字（每帧调用，内容不变时走缓存）。</summary>
        public string GetLogText()
        {
            return ActivityLog.Render(m_Setting.LogShowSamples, L("LogEmpty"), level =>
            {
                switch (level)
                {
                    case ActivityLog.Level.Success: return L("LogTagSuccess");
                    case ActivityLog.Level.Warning: return L("LogTagWarning");
                    case ActivityLog.Level.Error: return L("LogTagError");
                    default: return string.Empty;
                }
            });
        }

        public void ClearLog() => ActivityLog.Clear();

        private void FinishJob(string error, bool cancelled)
        {
            m_Busy = false;
            m_JobWatch?.Stop();
            string elapsed = m_JobWatch == null ? string.Empty : m_JobWatch.Elapsed.ToString(@"m\:ss");
            int ok = m_JobDone - m_JobFailed;
            string average = m_JobScores.Count > 0 ? m_JobScores.Average().ToString("0.0") : "-";

            ProgressState state;
            LocText summary;
            if (error != null && cancelled)
            {
                summary = T("Stopped", error, ok);
                state = ProgressState.Failed;
            }
            else if (cancelled)
            {
                summary = T("Cancelled", ok);
                state = ProgressState.Cancelled;
            }
            else
            {
                summary = m_JobKind == JobKind.FixMixed
                    ? T("DoneFix", ok, m_JobFixed, elapsed)
                    : m_JobKind == JobKind.Review
                    ? T("DoneReview", ok, m_JobFixed, average, elapsed)
                    : m_JobKind == JobKind.Describe
                        ? T("DoneDescribe", ok, m_JobFailed, elapsed)
                        : (m_JobScores.Count > 0
                            ? T("DoneWithReview", ok, m_JobFailed, m_JobFixed, average, elapsed)
                            : T("Done", ok, m_JobFailed, elapsed));
                if (error != null)
                {
                    summary = LocText.Concat(summary, T("SomeBatchesFailed", error));
                }
                state = m_JobFailed > 0 ? ProgressState.Warning : ProgressState.Complete;
            }
            Mod.Log.Info("Job finished: " + summary);
            ActivityLog.Add(state == ProgressState.Failed ? ActivityLog.Level.Error : state == ProgressState.Warning || state == ProgressState.Cancelled ? ActivityLog.Level.Warning : ActivityLog.Level.Success, summary);

            Notify(summary.ToString(), state, 100, pop: true);
            RefreshGameUI();
            // 不再整体重新检测（会卡住游戏几秒），批次结果已在 ApplyBatch 里更新到现有状态。
            UpdateOverview();
            ComputeQualityInBackground(m_Scan, m_GlossaryTask);
            m_LastSummary = summary;
            m_Progress = T("LastJob", summary);

            var chainKeys = m_ChainDescribeKeys;
            m_ChainDescribeKeys = null;
            if (chainKeys != null && m_JobKind == JobKind.Translate && !cancelled && m_Scan != null
                && chainKeys.Any(k => m_Scan.Mods.TryGetValue(k, out var m) && m.MissingDescriptions.Count > 0))
            {
                DescribeMods(chainKeys);
                return;
            }
            BumpUi();
        }

        private void UpdateProgressText()
        {
            if (!m_Busy)
            {
                return;
            }
            int done = m_JobDone;
            int total = Math.Max(1, m_JobTotal);
            int percent = done * 100 / total;
            m_Progress = m_JobKind == JobKind.FixMixed
                ? T("Fixing", done, m_JobTotal, percent, m_JobFixed, m_JobCurrent)
                : m_JobKind == JobKind.Review
                ? T("Reviewing", done, m_JobTotal, percent, m_JobFixed, m_JobCurrent)
                : m_JobKind == JobKind.Describe
                    ? T("Describing", done, m_JobTotal, percent, m_JobFailed, m_JobCurrent)
                    : T("Translating", done, m_JobTotal, percent, m_JobFailed, m_JobCurrent);
            if (percent != m_LastNotifiedPercent)
            {
                m_LastNotifiedPercent = percent;
                Notify(L(m_JobKind == JobKind.FixMixed ? "NotifyFix" : m_JobKind == JobKind.Review ? "NotifyReview" : m_JobKind == JobKind.Describe ? "NotifyDescribe" : "NotifyProgress", m_JobLanguage, done, m_JobTotal), ProgressState.Progressing, percent, pop: false);
            }
            BumpUi();
        }

        private static void Notify(string text, ProgressState state, int progress, bool pop)
        {
            try
            {
                LocalizedString title = LocalizedString.Value(Mod.DisplayName);
                LocalizedString body = LocalizedString.Value(text);
                if (pop)
                {
                    NotificationSystem.Pop(kNotificationId, 6f, title, body, null, null, null, state, progress);
                }
                else
                {
                    NotificationSystem.Push(kNotificationId, title, body, null, null, null, state, progress);
                }
            }
            catch (Exception)
            {
                // 通知 UI 尚未绑定时忽略。
            }
        }

        public void Cancel()
        {
            if (m_Busy)
            {
                m_Cts?.Cancel();
                m_Progress = T("Cancelling");
                BumpUi();
            }
        }

        public void DeleteTranslation(string key)
        {
            if (IsBusy || m_Scan == null || string.IsNullOrEmpty(key))
            {
                return;
            }
            // 译文和生成的资产描述一起删除。
            bool deletedTranslations = Store.Delete(m_Scan.Locale, key);
            bool deletedDescriptions = Store.Delete(m_Scan.Locale, TranslationStore.DescriptionGroup(key));
            if (deletedTranslations || deletedDescriptions)
            {
                Mod.Log.Info("Deleted translations and generated descriptions of " + key);
                ReloadI18N();
                ApplyToGame(forceReload: true);
                m_Progress = T("Deleted");
            }
            DoScan();
        }

        /// <summary>重新从磁盘读取译文并应用（玩家手动编辑译文文件后使用）。</summary>
        public void ReloadFromDisk()
        {
            if (IsBusy)
            {
                return;
            }
            Store.ClearCache();
            I18NBridge.ClearCache();
            ReloadI18N();
            ApplyToGame(forceReload: true);
            UpdateEnvironmentText();
            if (m_PlaysetMods != null)
            {
                DoScan();
            }
            m_Progress = T("Reloaded");
            BumpUi();
        }

        private void ReloadI18N()
        {
            if (UseI18N)
            {
                m_I18N.Reload(Localization.activeLocaleId, Localization.fallbackLocaleId);
                m_LastInjectedDict = null;
            }
        }

        // ================================================================ 生效到游戏

        /// <summary>按当前游戏语言重建译文集合并写入 I18N / 游戏词典。</summary>
        private void ApplyToGame(bool forceReload)
        {
            var lm = Localization;
            string locale = lm.activeLocaleId;
            Store.FallbackLocale = lm.fallbackLocaleId;
            m_LiveLocale = locale;
            m_Live = Store.GetAllTranslations(locale);

            if (UseI18N)
            {
                RetireGatedSource();
                m_LastInjectedDict = null;
                LiveUpdate();
            }
            else if (!m_AllowDirectInjection)
            {
                // 还不确定 I18N Everywhere 会不会加载：先不写游戏词典。
                // 过早写入会让其他 mod 以为名字已存在而不注册自己的文本（如 Extra Assets Importer），导致重复翻译。
            }
            else if (!forceReload && m_Gated != null && m_Gated.Locale == locale && !m_Gated.Retired)
            {
                // 同一语言：直接补写，无需重载整个词典。
                foreach (var kv in m_Live)
                {
                    m_Gated.Entries[kv.Key] = kv.Value;
                    lm.activeDictionary.Add(kv.Key, kv.Value);
                }
            }
            else
            {
                // RemoveSource 会自动重建受影响的词典，无需整体重载。
                RetireGatedSource();
                if (m_Live.Count > 0)
                {
                    m_Gated = new GatedMemorySource(locale, new Dictionary<string, string>(m_Live, StringComparer.Ordinal));
                    lm.AddSource(locale, m_Gated);
                }
            }
            RefreshGameUI();
        }

        /// <summary>停用旧的降级注入源，并把它从本地化管理器中移除（移除回退语言的源会清理回退词典）。</summary>
        private bool RetireGatedSource()
        {
            if (m_Gated == null)
            {
                return false;
            }
            m_Gated.Retired = true;
            try
            {
                Localization.RemoveSource(m_Gated.Locale, m_Gated);
            }
            catch (Exception ex)
            {
                Mod.Log.Warn("RemoveSource failed: " + ex.Message);
            }
            m_Gated = null;
            return true;
        }

        private void InjectEntries(Dictionary<string, string> entries)
        {
            var lm = Localization;
            if (lm.activeLocaleId != m_LiveLocale)
            {
                return;
            }
            if (!UseI18N && !m_AllowDirectInjection)
            {
                return;
            }
            if (UseI18N)
            {
                var dict = m_I18N.GetCurrentDictionary();
                if (dict == null)
                {
                    return;
                }
                foreach (var kv in entries)
                {
                    dict[kv.Key] = kv.Value;
                }
                return;
            }
            if (m_Gated == null || m_Gated.Retired)
            {
                m_Gated = new GatedMemorySource(lm.activeLocaleId, new Dictionary<string, string>(StringComparer.Ordinal));
                lm.AddSource(lm.activeLocaleId, m_Gated);
            }
            foreach (var kv in entries)
            {
                m_Gated.Entries[kv.Key] = kv.Value;
                lm.activeDictionary.Add(kv.Key, kv.Value);
            }
        }

        /// <summary>
        /// I18N 每次重载都会换一个新的 CurrentLocaleDictionary；检测到换了就把译文补进去。
        /// 英语（回退语言）译文不在 I18N 语言包里，完全依靠这里注入。
        /// </summary>
        private void LiveUpdate()
        {
            try
            {
                if (!UseI18N || m_Live.Count == 0 || Localization?.activeLocaleId != m_LiveLocale)
                {
                    return;
                }
                var dict = m_I18N.GetCurrentDictionary();
                if (dict == null || ReferenceEquals(dict, m_LastInjectedDict))
                {
                    return;
                }
                foreach (var kv in m_Live)
                {
                    dict[kv.Key] = kv.Value;
                }
                m_LastInjectedDict = dict;
            }
            catch (Exception ex)
            {
                Mod.Log.Warn("Live inject failed: " + ex.Message);
            }
        }

        /// <summary>触发 onActiveDictionaryChanged，让界面重新取词（I18N 会随之重载并读到新保存的语言包）。</summary>
        private static void RefreshGameUI()
        {
            try
            {
                var field = typeof(LocalizationManager).GetField("onActiveDictionaryChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                (field?.GetValue(Localization) as Action)?.Invoke();
            }
            catch (Exception ex)
            {
                Mod.Log.Warn("UI refresh failed: " + ex.Message);
            }
        }

        // ================================================================ 其他按钮

        /// <summary>测试连接与翻译任务互不影响，只有测试本身进行中才禁用按钮。</summary>
        public bool IsTesting { get; private set; }

        public void TestConnection()
        {
            if (IsTesting)
            {
                return;
            }
            var client = new ChatClient(m_Setting.ApiUrl, m_Setting.ApiKey, m_Setting.ModelName, m_Setting.Temperature);
            string locale = Localization.activeLocaleId;
            m_ApiStatus = T("Connecting", client.Endpoint);
            IsTesting = true;
            BumpUi();

            Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                string reply = null;
                Exception error = null;
                try
                {
                    reply = await client.CompleteAsync(
                        "You are a game translator. Reply with json only.",
                        $"Translate {{\"1\":\"Welcome, Mayor! Zoning tools are ready.\"}} into {Translator.LanguageName(locale)}. Reply with json like {{\"1\":\"...\"}}.",
                        jsonMode: true,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    error = ex.InnerException ?? ex;
                }
                long ms = sw.ElapsedMilliseconds;
                MainThreadDispatcher.RunOnMainThread(() =>
                {
                    m_ApiStatus = error == null
                        ? T("ConnectOk", client.Model, (ms / 1000.0).ToString("0.0"), SampleFromReply(reply))
                        : T("ConnectFailed", error.Message);
                    IsTesting = false;
                    BumpUi();
                });
            });
        }

        /// <summary>从测试回复 {"1":"..."} 中取出译文，取不到时显示截断的原文。</summary>
        private static string SampleFromReply(string reply)
        {
            try
            {
                int start = reply.IndexOf('{');
                int end = reply.LastIndexOf('}');
                if (start >= 0 && end > start)
                {
                    string text = JObject.Parse(reply.Substring(start, end - start + 1))["1"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Trim();
                    }
                }
            }
            catch (Exception)
            {
                // 不是 JSON，显示原文。
            }
            return ChatClient.Truncate((reply ?? string.Empty).Replace("\n", " "), 80);
        }

        public void OpenFolder()
        {
            try
            {
                Store.EnsureLayout();
                Process.Start(new ProcessStartInfo { FileName = Store.RootDir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Mod.Log.Warn("Open folder failed: " + ex.Message);
            }
        }

        private static void Safe(Action action, string what)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Mod.Log.Error(ex, $"Error during {what}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            m_Disposed = true;
            m_Cts?.Cancel();
            var lm = Localization;
            if (lm != null)
            {
                lm.onActiveDictionaryChanged -= OnActiveDictionaryChanged;
            }
            if (m_WatchUpdater.HasValue)
            {
                MainThreadDispatcher.UnregisterUpdater(m_WatchUpdater.Value);
                m_WatchUpdater = null;
            }
            if (m_Gated != null)
            {
                m_Gated.Retired = true;
            }
        }
    }
}
