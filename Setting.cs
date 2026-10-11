using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI.Localization;
using Game.UI.Widgets;
using AITranslate.Core;

namespace AITranslate
{
    /// <summary>
    /// 设置页布局：
    ///   翻译：概况（状态 + 进度 + 对全部 mod 的操作）/ 单个 mod（搜索 + 选择 + 详情 + 操作）
    ///   日志：实时日志
    ///   设置：连接 / 自动处理
    ///   高级：翻译参数 / 维护
    /// 按钮只在有事可做时出现（没有待翻译的 mod 就不显示「翻译全部」），任务进行中变灰。
    /// 状态类文字都用整行多行文本显示，避免长文本把左侧标题挤成竖排。
    /// </summary>
    [FileLocation("ModsSettings\\" + Mod.Name + "\\" + Mod.Name)]
    [SettingsUITabOrder(kMainTab, kLogTab, kSettingsTab, kAdvancedTab)]
    [SettingsUIGroupOrder(kStatusGroup, kModGroup, kLogGroup, kApiGroup, kAutoGroup, kTuningGroup, kMaintenanceGroup)]
    [SettingsUIShowGroupName(kStatusGroup, kModGroup, kApiGroup, kAutoGroup, kTuningGroup, kMaintenanceGroup)]
    public class Setting : ModSetting
    {
        public const string kMainTab = "Main";
        public const string kSettingsTab = "Settings";
        public const string kAdvancedTab = "Advanced";
        public const string kLogTab = "Log";

        public const string kStatusGroup = "Status";
        public const string kModGroup = "Mod";
        public const string kLogGroup = "LogGroup";
        public const string kApiGroup = "Api";
        public const string kAutoGroup = "Auto";
        public const string kTuningGroup = "Tuning";
        public const string kMaintenanceGroup = "Maintenance";

        public const string kNoneMod = "";
        public const string kDefaultApiUrl = "https://api.deepseek.com";
        public const string kDefaultModel = "deepseek-flash";

        /// <summary>以前的默认模型名；读设置时仍是它的（没自己改过），换成新的默认值。</summary>
        public const string kLegacyDefaultModel = "deepseek-chat";

        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        private static TranslationService Service => Mod.Instance?.Service;

        // ================================================================ 翻译：概况

        [SettingsUISection(kMainTab, kStatusGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(GetStatusText))]
        public string StatusPanel => string.Empty;

        [SettingsUISection(kMainTab, kStatusGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup("AllActions")]
        [SettingsUIConfirmation]
        [SettingsUIHideByCondition(typeof(Setting), nameof(NothingToTranslate))]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsBusy))]
        public bool TranslateAllNeeded
        {
            set { Service?.TranslateAllNeeded(); }
        }

        [SettingsUISection(kMainTab, kStatusGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup("AllActions")]
        [SettingsUIConfirmation]
        [SettingsUIHideByCondition(typeof(Setting), nameof(NothingToReview))]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsBusy))]
        public bool ReviewAllMods
        {
            set { Service?.ReviewAll(); }
        }

        [SettingsUISection(kMainTab, kStatusGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup("AllActions")]
        [SettingsUIConfirmation]
        [SettingsUIHideByCondition(typeof(Setting), nameof(NothingToDescribe))]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsBusy))]
        public bool DescribeAllAssets
        {
            set { Service?.DescribeAll(); }
        }

        [SettingsUISection(kMainTab, kStatusGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup("AllActions")]
        [SettingsUIConfirmation]
        [SettingsUIHideByCondition(typeof(Setting), nameof(NothingToFixMixed))]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsBusy))]
        public bool FixMixedAll
        {
            set { Service?.FixMixedAll(); }
        }

        [SettingsUISection(kMainTab, kStatusGroup)]
        [SettingsUIButton]
        [SettingsUIHideByCondition(typeof(Setting), nameof(IsIdle))]
        public bool Cancel
        {
            set { Service?.Cancel(); }
        }

        // ================================================================ 翻译：单个 mod

        public enum ModCategory
        {
            All,
            Gameplay,
            Building,
            Vehicle,
            Network,
            Prop,
            Vegetation,
            Surface,
            Mixed,
            Other,
        }

        public enum StatusFilterOption
        {
            All,
            Actionable,
            Pending,
            MissingDescriptions,
            Polish,
            Done,
        }

        private string m_SearchText = string.Empty;
        private ModCategory m_CategoryFilter;
        private StatusFilterOption m_StatusFilter;

        [SettingsUISection(kMainTab, kModGroup)]
        [SettingsUITextInput]
        public string SearchText
        {
            get => m_SearchText;
            set
            {
                m_SearchText = value ?? string.Empty;
                Service?.OnFilterChanged();
            }
        }

        [SettingsUISection(kMainTab, kModGroup)]
        public ModCategory CategoryFilter
        {
            get => m_CategoryFilter;
            set
            {
                m_CategoryFilter = value;
                Service?.OnFilterChanged();
            }
        }

        [SettingsUISection(kMainTab, kModGroup)]
        public StatusFilterOption StatusFilter
        {
            get => m_StatusFilter;
            set
            {
                m_StatusFilter = value;
                Service?.OnFilterChanged();
            }
        }

        [SettingsUISection(kMainTab, kModGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(GetSearchText))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HasNoFilter))]
        public string SearchPanel => string.Empty;

        [SettingsUISection(kMainTab, kModGroup)]
        [SettingsUIButton]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HasNoFilter))]
        public bool ClearFilters
        {
            set
            {
                m_SearchText = string.Empty;
                m_CategoryFilter = ModCategory.All;
                m_StatusFilter = StatusFilterOption.All;
                Service?.OnFilterChanged();
                ApplyAndSave();
            }
        }

        [SettingsUISection(kMainTab, kModGroup)]
        [SettingsUIDropdown(typeof(Setting), nameof(GetModItems))]
        [SettingsUIValueVersion(typeof(TranslationService), nameof(TranslationService.GetUiVersion))]
        public string SelectedMod { get; set; }

        [SettingsUISection(kMainTab, kModGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(GetModText))]
        public string ModPanel => string.Empty;

        [SettingsUISection(kMainTab, kModGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup("ModActions")]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideTranslateSelected))]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsBusy))]
        public bool TranslateSelected
        {
            set { Service?.TranslateMods(new[] { SelectedMod }, force: false); }
        }

        [SettingsUISection(kMainTab, kModGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup("ModActions")]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideReviewSelected))]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsBusy))]
        public bool ReviewSelectedMod
        {
            set { Service?.ReviewSelected(SelectedMod); }
        }

        [SettingsUISection(kMainTab, kModGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup("ModActions")]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideDescribeSelected))]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsBusy))]
        public bool DescribeSelectedMod
        {
            set { Service?.DescribeSelected(SelectedMod); }
        }

        [SettingsUISection(kMainTab, kModGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup("ModActions")]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideFixMixedSelected))]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsBusy))]
        public bool FixMixedSelectedMod
        {
            set { Service?.FixMixedSelected(SelectedMod); }
        }

        [SettingsUISection(kMainTab, kModGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup("ModMore")]
        [SettingsUIConfirmation]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideRetranslateSelected))]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsBusy))]
        public bool RetranslateSelected
        {
            set { Service?.TranslateMods(new[] { SelectedMod }, force: true); }
        }

        [SettingsUISection(kMainTab, kModGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup("ModMore")]
        [SettingsUIConfirmation]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideDeleteSelected))]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsBusy))]
        public bool DeleteSelected
        {
            set { Service?.DeleteTranslation(SelectedMod); }
        }

        // ================================================================ 日志

        [SettingsUISection(kLogTab, kLogGroup)]
        public bool LogShowSamples { get; set; }

        [SettingsUISection(kLogTab, kLogGroup)]
        [SettingsUIButton]
        public bool ClearLog
        {
            set { Service?.ClearLog(); }
        }

        [SettingsUISection(kLogTab, kLogGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(GetLogText))]
        public string LogPanel => string.Empty;

        // ================================================================ 设置：连接

        [SettingsUISection(kSettingsTab, kApiGroup)]
        [SettingsUITextInput]
        public string ApiKey { get; set; }

        [SettingsUISection(kSettingsTab, kApiGroup)]
        [SettingsUITextInput]
        public string ApiUrl { get; set; }

        [SettingsUISection(kSettingsTab, kApiGroup)]
        [SettingsUITextInput]
        public string ModelName { get; set; }

        [SettingsUISection(kSettingsTab, kApiGroup)]
        [SettingsUIButton]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsTesting))]
        public bool TestConnection
        {
            set { Service?.TestConnection(); }
        }

        [SettingsUISection(kSettingsTab, kApiGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(GetApiText))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HasNoApiText))]
        public string ApiPanel => string.Empty;

        // ================================================================ 设置：自动处理

        [SettingsUISection(kSettingsTab, kAutoGroup)]
        public bool AutoTranslateNewMods { get; set; }

        [SettingsUISection(kSettingsTab, kAutoGroup)]
        public bool AutoTranslateOnStartup { get; set; }

        [SettingsUISection(kSettingsTab, kAutoGroup)]
        [SettingsUIHideByCondition(typeof(Setting), nameof(NoAutoTranslate))]
        public bool AutoTranslateSkipLarge { get; set; }

        [SettingsUISection(kSettingsTab, kAutoGroup)]
        public bool AutoReview { get; set; }

        [SettingsUISection(kSettingsTab, kAutoGroup)]
        public bool AutoDescribe { get; set; }

        [SettingsUISection(kSettingsTab, kAutoGroup)]
        public bool IncludeRoadBuilderRoads { get; set; }

        [SettingsUISection(kSettingsTab, kAutoGroup)]
        public bool FollowGameLanguageOptions { get; set; }

        // ================================================================ 高级：翻译参数

        public enum ReviewScopeOption
        {
            Suspicious,
            Unreviewed,
            All,
        }

        [SettingsUISection(kAdvancedTab, kTuningGroup)]
        [SettingsUITextInput]
        public string ExtraPrompt { get; set; }

        [SettingsUISection(kAdvancedTab, kTuningGroup)]
        public ReviewScopeOption ReviewScope { get; set; }

        [SettingsUISection(kAdvancedTab, kTuningGroup)]
        public bool UseGameGlossary { get; set; }

        [SettingsUISection(kAdvancedTab, kTuningGroup)]
        [SettingsUISlider(min = 0f, max = 2f, step = 0.1f, scalarMultiplier = 1, unit = "floatSingleFraction")]
        public float Temperature { get; set; }

        [SettingsUISection(kAdvancedTab, kTuningGroup)]
        [SettingsUISlider(min = 5, max = 80, step = 5, scalarMultiplier = 1, unit = "integer")]
        public int BatchSize { get; set; }

        [SettingsUISection(kAdvancedTab, kTuningGroup)]
        [SettingsUISlider(min = 1, max = 8, step = 1, scalarMultiplier = 1, unit = "integer")]
        public int Concurrency { get; set; }

        // ================================================================ 高级：维护

        [SettingsUISection(kAdvancedTab, kMaintenanceGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(GetEnvironmentText))]
        public string EnvironmentPanel => string.Empty;

        [SettingsUISection(kAdvancedTab, kMaintenanceGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup("Maintenance")]
        public bool OpenTranslationFolder
        {
            set { Service?.OpenFolder(); }
        }

        [SettingsUISection(kAdvancedTab, kMaintenanceGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup("Maintenance")]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsBusy))]
        public bool ReapplyTranslations
        {
            set { Service?.ReloadFromDisk(); }
        }

        [SettingsUISection(kAdvancedTab, kMaintenanceGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup("Maintenance")]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsBusy))]
        public bool Rescan
        {
            set { Service?.Rescan(); }
        }

        // ================================================================ 辅助

        public DropdownItem<string>[] GetModItems()
        {
            return Service?.GetDropdownItems() ?? new[]
            {
                new DropdownItem<string> { value = kNoneMod, displayName = "—" },
            };
        }

        /// <summary>概况 + 进度（进度只在有内容时附在后面）。</summary>
        public LocalizedString GetStatusText()
        {
            string overview = Service?.OverviewText ?? string.Empty;
            string progress = Service?.ProgressText ?? string.Empty;
            return LocalizedString.Value(string.IsNullOrEmpty(progress) ? overview : overview + "\n\n" + progress);
        }

        public LocalizedString GetModText() => LocalizedString.Value(Service?.DescribeMod(SelectedMod) ?? string.Empty);

        public LocalizedString GetApiText() => LocalizedString.Value(Service?.ApiStatusText ?? string.Empty);

        public LocalizedString GetEnvironmentText() => LocalizedString.Value(Service?.EnvironmentText ?? string.Empty);

        public bool HasNoApiText() => string.IsNullOrEmpty(Service?.ApiStatusText);

        public LocalizedString GetSearchText() => LocalizedString.Value(Service?.SearchSummary ?? string.Empty);

        public bool HasNoFilter() => string.IsNullOrWhiteSpace(m_SearchText) && m_CategoryFilter == ModCategory.All && m_StatusFilter == StatusFilterOption.All;

        public LocalizedString GetLogText() => LocalizedString.Value(Service?.GetLogText() ?? string.Empty);

        public bool IsBusy() => Service == null || Service.IsBusy;

        public bool IsIdle() => Service == null || !Service.IsJobRunning;

        public bool IsTesting() => Service == null || Service.IsTesting;

        public bool NoAutoTranslate() => !AutoTranslateNewMods && !AutoTranslateOnStartup;

        // 对全部 mod 的操作：没有可做的事就不显示。
        public bool NothingToTranslate() => Service == null || !Service.HasPendingMods;

        public bool NothingToReview() => Service == null || !Service.HasSuspicious;

        public bool NothingToDescribe() => Service == null || !Service.HasMissingDescriptions;

        public bool NothingToFixMixed() => Service == null || !Service.HasMixed;

        // 对所选 mod 的操作：不适用就不显示。
        public bool HideTranslateSelected() => Service == null || !Service.HasPending(SelectedMod);

        public bool HideReviewSelected() => Service == null || !Service.CanReview(SelectedMod);

        public bool HideDescribeSelected() => Service == null || !Service.CanDescribe(SelectedMod);

        public bool HideFixMixedSelected() => Service == null || !Service.CanFixMixed(SelectedMod);

        public bool HideRetranslateSelected() => Service == null || !Service.HasTranslation(SelectedMod);

        public bool HideDeleteSelected() => Service == null || !Service.HasTranslation(SelectedMod);

        public override void SetDefaults()
        {
            ApiUrl = kDefaultApiUrl;
            ApiKey = string.Empty;
            ModelName = kDefaultModel;
            SelectedMod = kNoneMod;
            AutoTranslateOnStartup = false;
            AutoTranslateSkipLarge = true;
            BatchSize = 40;
            Concurrency = 3;
            Temperature = 1.3f;
            UseGameGlossary = true;
            ExtraPrompt = string.Empty;
            AutoReview = true;
            ReviewScope = ReviewScopeOption.Suspicious;
            FollowGameLanguageOptions = true;
            AutoDescribe = true;
            AutoTranslateNewMods = true;
            LogShowSamples = true;
        }
    }
}
