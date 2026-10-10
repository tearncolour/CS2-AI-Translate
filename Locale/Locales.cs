using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Colossal;
using Newtonsoft.Json;

namespace AITranslate.Locale
{
    internal static class LocaleTable
    {
        // (选项名, 中文标签, 中文说明, 英文标签, 英文说明)
        // 写法：标签尽量两到六个字；说明一句话讲清“做什么”，必要时补一句“注意什么”。
        private static readonly (string, string, string, string, string)[] s_Options =
        {
            // ---- 翻译：概况（对全部 mod）
            (nameof(Setting.TranslateAllNeeded), "翻译全部", "翻译所有待翻译的 mod。开启了「翻译后补全资产描述」时，接着补描述。",
                "Translate all", "Translate every pending mod. If \"Describe assets after translating\" is on, missing descriptions are written afterwards."),
            (nameof(Setting.ReviewAllMods), "审校全部", "让 AI 检查所有 mod 的译文（范围见「高级」页），改写不好的。",
                "Review all", "Let the AI check the translations of all mods (scope set on the Advanced tab) and rewrite weak ones."),
            (nameof(Setting.DescribeAllAssets), "补全描述", "给所有缺描述的资产写一段简介；只有编号的资产顺便起个名字。",
                "Describe all", "Write a short description for every asset without one, and name assets that only have an ID."),
            (nameof(Setting.FixMixedAll), "修正混杂", "把游戏本体、DLC 和 mod 自带译文里没翻的英文翻译出来。品牌名、人名保留。",
                "Fix mixed text", "Translate English words left in official and bundled translations. Brands and names are kept."),
            (nameof(Setting.Cancel), "停止", "停止当前任务，已完成的部分会保留。",
                "Stop", "Stop the current job. Finished parts are kept."),

            // ---- 翻译：单个 mod
            (nameof(Setting.SearchText), "搜索", "输入名称、首字母缩写或中文译名，拼错也能找到。会自动选中最匹配的 mod。",
                "Search", "Type a name, initials or translated name; typos are fine. The best match is selected automatically."),
            (nameof(Setting.CategoryFilter), "类型", "只显示某一类 mod。",
                "Type", "Show only one kind of mod."),
            (nameof(Setting.StatusFilter), "状态", "只显示某种状态的 mod。",
                "Status", "Show only mods in one state."),
            (nameof(Setting.ClearFilters), "清除筛选", "显示全部 mod。",
                "Clear filters", "Show all mods."),
            (nameof(Setting.SelectedMod), "mod", "方括号里是状态：待翻译、待更新（mod 更新过）、缺描述、待优化（有可疑译文）、已翻译、无需翻译（mod 自带或已有译文）。",
                "Mod", "The tag shows the state: Pending, Update (the mod was updated), No description, Polish (suspicious translations), Translated, Not needed (already localized)."),
            (nameof(Setting.TranslateSelected), "翻译", "翻译这个 mod 缺少或变化了的文本。译文会保存，下次不用再翻。",
                "Translate", "Translate the missing or changed text of this mod. Results are saved and never translated again."),
            (nameof(Setting.ReviewSelectedMod), "AI 审校", "让 AI 检查这个 mod 的译文，改写不好的。",
                "Review", "Let the AI check this mod's translations and rewrite weak ones."),
            (nameof(Setting.DescribeSelectedMod), "补描述", "给这个 mod 里缺描述的资产写简介；只有编号的资产顺便起个名字。不会编造数值。",
                "Describe", "Describe this mod's assets that have no description, and name those with only an ID. No made-up numbers."),
            (nameof(Setting.FixMixedSelectedMod), "修正混杂", "把这个 mod（或游戏本体）自带译文里没翻的英文翻译出来。改动单独保存，不改原文件。",
                "Fix mixed text", "Translate English left in this mod's (or the base game's) own translations. Fixes are stored separately."),
            (nameof(Setting.RetranslateSelected), "重新翻译", "丢掉这个 mod 已保存的译文，全部重新翻译。",
                "Retranslate", "Discard this mod's saved translations and translate everything again."),
            (nameof(Setting.DeleteSelected), "删除译文", "删除这个 mod 的 AI 译文和生成的描述，恢复原文。",
                "Delete", "Delete this mod's AI translations and generated descriptions."),

            // ---- 日志
            (nameof(Setting.LogShowSamples), "显示样例", "每批结果下面显示一两条原文和译文。",
                "Show samples", "Show one or two source and translation samples under each batch."),
            (nameof(Setting.ClearLog), "清空日志", "只清空显示，不影响译文。",
                "Clear log", "Clears the display only; translations are not affected."),

            // ---- 设置：连接
            (nameof(Setting.ApiKey), "API Key", "在 platform.deepseek.com 创建，以 sk- 开头。只保存在本机。",
                "API key", "Create one at platform.deepseek.com (starts with sk-). Stored on this computer only."),
            (nameof(Setting.ApiUrl), "API 地址", "用 DeepSeek 不用改。换成其他 OpenAI 兼容服务时填它的地址。",
                "API URL", "Leave as is for DeepSeek. For another OpenAI-compatible service, enter its URL."),
            (nameof(Setting.ModelName), "模型", "DeepSeek 用 deepseek-flash 即可。",
                "Model", "deepseek-flash works for DeepSeek."),
            (nameof(Setting.TestConnection), "测试连接", "发一句示例，检查地址、Key 和模型能不能用。",
                "Test connection", "Send one sample to check the URL, key and model."),

            // ---- 设置：自动处理
            (nameof(Setting.AutoTranslateNewMods), "自动翻译新 mod", "安装或更新 mod 后，在后台自动翻译。",
                "Translate new mods automatically", "Translate mods in the background after they are installed or updated."),
            (nameof(Setting.AutoTranslateOnStartup), "进游戏时翻译全部", "每次进游戏后，自动翻译所有还没翻译的 mod，包括以前装的。",
                "Translate everything on startup", "Each time the game starts, translate every mod that is not translated yet, including older ones."),
            (nameof(Setting.AutoTranslateSkipLarge), "自动翻译跳过大型 mod", "超过 1500 条文本的 mod 不自动翻译，以免意外花费较多。可以手动翻译。",
                "Skip large mods", "Mods with more than 1500 entries are not translated automatically. You can still translate them by hand."),
            (nameof(Setting.AutoReview), "翻译后 AI 审校", "每翻完一批，让 AI 打分并改写低分译文。API 用量多约一半。",
                "Review after translating", "After each batch, the AI scores the results and rewrites weak ones. Uses about half again as much API."),
            (nameof(Setting.AutoDescribe), "翻译后补全资产描述", "翻译完成后，给建造菜单里没有描述的资产写简介。",
                "Describe assets after translating", "After translating, write descriptions for build-menu assets that have none."),
            (nameof(Setting.FollowGameLanguageOptions), "mod 语言跟随游戏", "有些 mod 的语言选项设成 English 时不显示译文；开启后自动改为跟随游戏。",
                "Mod language follows the game", "Some mods ignore translations when their own language option is English; this switches it to follow the game."),

            // ---- 高级：翻译参数
            (nameof(Setting.ExtraPrompt), "额外要求", "写给 AI 的补充要求，例如“Lane 统一译为车道”。",
                "Extra instructions", "Extra instructions for the AI, e.g. \"always translate Lane as …\"."),
            (nameof(Setting.ReviewScope), "审校范围", "点「AI 审校」时检查哪些译文。",
                "Review scope", "Which translations a review checks."),
            (nameof(Setting.UseGameGlossary), "参照游戏用词", "参考游戏自带译文里的用词（如分区、车道），和官方保持一致。",
                "Follow game terms", "Use the wording of the game's own translations (zones, lanes…) for consistency."),
            (nameof(Setting.Temperature), "温度", "DeepSeek 建议翻译用 1.3。越低越直译。",
                "Temperature", "DeepSeek recommends 1.3 for translation. Lower is more literal."),
            (nameof(Setting.BatchSize), "每批条数", "一次请求翻译多少条。出错多时调小。",
                "Batch size", "Entries per request. Lower it if requests often fail."),
            (nameof(Setting.Concurrency), "同时请求数", "遇到频率限制（429）时调小。",
                "Parallel requests", "Lower it if you hit rate limits (429)."),

            // ---- 高级：维护
            (nameof(Setting.OpenTranslationFolder), "打开译文目录", "译文可以直接编辑；删掉某个文件，对应的 mod 就会重新翻译。",
                "Open folder", "Translations can be edited by hand; delete a file to translate that mod again."),
            (nameof(Setting.ReapplyTranslations), "重新加载译文", "手动改过译文文件后点这里。",
                "Reload translations", "Use after editing translation files by hand."),
            (nameof(Setting.Rescan), "重新检测", "重新检查哪些 mod 需要翻译。平时会自动检测，一般不用点。",
                "Rescan", "Check again which mods need translating. Normally automatic."),
        };

        /// <summary>运行时界面文字的本地化 key 前缀。其他语言由本 mod 自己翻译。</summary>
        public const string kUiPrefix = "AITranslate.UI.";

        // (名称, 中文, 英文)；{0} {1} … 为参数。显示在整行多行文本里，可以用 \n 分行。
        // 注意：游戏字体没有 ✔ ✘ ⚠ 等符号，不要使用。
        internal static readonly (string, string, string)[] s_Ui =
        {
            // 概况（每行一件事；对应的按钮就在下面）
            ("Waiting", "正在等待所有 mod 加载完成…", "Waiting for all mods to load…"),
            ("Scanning", "正在检测 mod…", "Scanning mods…"),
            ("StatusTarget", "翻译成：{0}（跟随游戏语言）", "Translating into: {0} (follows the game)"),
            ("StatusMods", "待翻译：{0} 个 mod，共 {1} 条", "Pending: {0} mods, {1} entries"),
            ("StatusModsDone", "全部 {0} 个 mod 都已翻译", "All {0} mods are translated"),
            ("StatusQuality", "可疑译文：{0} 条", "Suspicious translations: {0}"),
            ("StatusMixed", "夹着英文的译文：{0} 条", "Translations with leftover English: {0}"),
            ("StatusDescriptions", "缺描述的资产：{0} 个", "Assets without a description: {0}"),
            ("StatusDescriptionsNames", "缺描述的资产：{0} 个（其中 {1} 个没有名字）", "Assets without a description: {0} ({1} without a name)"),
            ("StatusNoKey", "还没填 API Key：到「设置」页填写后才能翻译", "No API key yet: enter it on the Settings tab to start"),

            // 下拉列表
            ("SelectMod", "— 选择 mod —", "— Select a mod —"),
            ("TagUpdate", "[待更新 {0}] ", "[Update {0}] "),
            ("TagPending", "[待翻译 {0}] ", "[Pending {0}] "),
            ("TagMissingDescriptions", "[缺描述 {0}] ", "[No description {0}] "),
            ("TagNeedsPolish", "[待优化 {0}] ", "[Polish {0}] "),
            ("TagTranslated", "[已翻译] ", "[Translated] "),
            ("TagNotNeeded", "[无需翻译] ", "[Not needed] "),

            // 所选 mod
            ("NotScanned", "还没检测", "Not scanned yet"),
            ("NothingSelected", "从上面的列表选一个 mod。", "Pick a mod from the list above."),
            ("ModCategoryLine", "类型：{0}", "Type: {0}"),
            ("ModText", "文本 {0} 条：已有译文 {1} · AI 已翻 {2} · 待翻译 {3}", "{0} entries: already localized {1} · AI {2} · pending {3}"),
            ("ModOutdated", "其中 {0} 条因 mod 更新需要重翻", "{0} of them changed in a mod update"),
            ("ModQuality", "质量：可疑 {0} 条", "Quality: {0} suspicious"),
            ("ModQualityReviewed", "质量：AI 评分 {0}（已审 {1} 条）· 可疑 {2} 条", "Quality: AI score {0} ({1} reviewed) · {2} suspicious"),
            ("ModMixed", "夹着英文的译文：{0} 条", "Translations with leftover English: {0}"),
            ("ModDescriptions", "描述：缺 {0} 个 · 已生成 {1} 个", "Descriptions: {0} missing · {1} written"),
            ("ModDescriptionsNames", "描述：缺 {0} 个（{1} 个没有名字，补描述时会起名）· 已生成 {2} 个", "Descriptions: {0} missing ({1} unnamed, will be named) · {2} written"),

            // 进度（附在概况下面）
            ("LastJob", "上次：{0}", "Last job: {0}"),
            ("Translating", "正在翻译 {0}/{1}（{2}%）· 失败 {3} · {4}", "Translating {0}/{1} ({2}%) · failed {3} · {4}"),
            ("Reviewing", "正在审校 {0}/{1}（{2}%）· 改写 {3} · {4}", "Reviewing {0}/{1} ({2}%) · rewritten {3} · {4}"),
            ("Describing", "正在写描述 {0}/{1}（{2}%）· 失败 {3} · {4}", "Describing {0}/{1} ({2}%) · failed {3} · {4}"),
            ("Fixing", "正在修正混杂 {0}/{1}（{2}%）· 改写 {3} · {4}", "Fixing mixed text {0}/{1} ({2}%) · rewritten {3} · {4}"),
            ("NotifyFix", "正在修正{0}混杂译文：{1}/{2}", "Fixing {0} mixed translations: {1}/{2}"),
            ("DoneFix", "检查 {0} 条混杂译文，改写 {1} 条，用时 {2}", "{0} mixed translations checked, {1} rewritten, {2}"),
            ("NothingToFix", "没有夹着英文的译文", "No mixed translations"),
            ("Cancelling", "正在停止…", "Stopping…"),
            ("NotifyProgress", "正在翻译为{0}：{1}/{2}", "Translating into {0}: {1}/{2}"),
            ("NotifyReview", "正在审校{0}译文：{1}/{2}", "Reviewing {0} translations: {1}/{2}"),
            ("NotifyDescribe", "正在生成{0}资产描述：{1}/{2}", "Writing {0} asset descriptions: {1}/{2}"),
            ("Done", "翻译 {0} 条，失败 {1} 条，用时 {2}", "{0} translated, {1} failed, {2}"),
            ("DoneWithReview", "翻译 {0} 条，失败 {1} 条，审校改写 {2} 条（平均 {3} 分），用时 {4}", "{0} translated, {1} failed, {2} rewritten by review (average {3}), {4}"),
            ("DoneReview", "审校 {0} 条，改写 {1} 条（平均 {2} 分），用时 {3}", "{0} reviewed, {1} rewritten (average {2}), {3}"),
            ("DoneDescribe", "写描述 {0} 个，失败 {1} 个，用时 {2}", "{0} descriptions written, {1} failed, {2}"),
            ("SomeBatchesFailed", "；部分批次出错：{0}", "; some batches failed: {0}"),
            ("Stopped", "出错停止：{0}（已完成 {1} 条）", "Stopped on error: {0} ({1} done)"),
            ("Cancelled", "已停止（已完成 {0} 条，已保存）", "Stopped ({0} done, saved)"),
            ("NothingToTranslate", "没有需要翻译的 mod", "Nothing to translate"),
            ("NothingToReview", "审校范围内没有要检查的译文", "Nothing to review in the selected scope"),
            ("NothingToDescribe", "没有缺描述的资产", "No assets without a description"),
            ("NeedApiKey", "请先到「设置」页填写 API Key", "Enter the API key on the Settings tab first"),
            ("SelectionUpToDate", "这个 mod 不需要翻译", "Nothing to translate for this mod"),
            ("Deleted", "已删除这个 mod 的译文和描述", "Translations and descriptions deleted"),
            ("Reloaded", "已重新加载译文", "Translations reloaded"),

            // 搜索与分类
            ("SelectModFiltered", "— 筛选出 {0} 个 —", "— {0} matches —"),
            ("SearchHint", "共 {0} 个 mod", "{0} mods"),
            ("SearchNone", "没找到，试试更短的关键词或清除筛选。", "No match. Try a shorter keyword or clear the filters."),
            ("SearchResults", "找到 {0} / {1} 个：{2}", "{0} of {1}: {2}"),
            ("SearchResultsMore", "找到 {0} / {1} 个：{2} 等", "{0} of {1}: {2}, …"),
            ("CategoryAll", "全部", "All"),
            ("CategoryGameplay", "玩法/工具 MOD", "Gameplay & tools"),
            ("CategoryBuilding", "建筑资产", "Buildings"),
            ("CategoryVehicle", "车辆资产", "Vehicles"),
            ("CategoryNetwork", "道路/轨道", "Roads & tracks"),
            ("CategoryProp", "道具/装饰", "Props & decorations"),
            ("CategoryVegetation", "植被", "Vegetation"),
            ("CategorySurface", "地表/区域", "Surfaces & areas"),
            ("CategoryMixed", "综合资产包", "Mixed asset pack"),
            ("CategoryOther", "其他", "Other"),
            ("CountOfBuilding", "建筑 {0}", "{0} buildings"),
            ("CountOfVehicle", "车辆 {0}", "{0} vehicles"),
            ("CountOfNetwork", "道路/轨道 {0}", "{0} roads/tracks"),
            ("CountOfProp", "道具 {0}", "{0} props"),
            ("CountOfVegetation", "植被 {0}", "{0} plants"),
            ("CountOfSurface", "地表/区域 {0}", "{0} surfaces/areas"),
            ("CategoryWithCounts", "{0}（{1}）", "{0} ({1})"),

            // 日志
            ("LogEmpty", "还没有记录。开始翻译后，这里会实时显示每一批的结果。", "Nothing yet. Each batch appears here as it finishes."),
            ("LogTagSuccess", "[完成] ", "[Done] "),
            ("LogTagWarning", "[注意] ", "[Warning] "),
            ("LogTagError", "[错误] ", "[Error] "),
            ("LogStartTranslate", "开始翻译：{0} 个 mod，共 {1} 条 → {2}（{3}）", "Translating {0} mods, {1} entries → {2} ({3})"),
            ("LogStartReview", "开始审校：{0} 个 mod，共 {1} 条（{3}）", "Reviewing {0} mods, {1} entries ({3})"),
            ("LogStartDescribe", "开始写描述：{0} 个 mod，共 {1} 个资产 → {2}（{3}）", "Describing {1} assets in {0} mods → {2} ({3})"),
            ("LogTranslateBatch", "{0}：翻译 {1} 条", "{0}: {1} translated"),
            ("LogTranslateBatchReviewed", "{0}：翻译 {1} 条，审校改写 {2} 条", "{0}: {1} translated, {2} rewritten by review"),
            ("LogReviewBatch", "{0}：审校 {1} 条，改写 {2} 条，平均 {3} 分", "{0}: {1} reviewed, {2} rewritten, average {3}"),
            ("LogDescribeBatch", "{0}：写描述 {1} 个", "{0}: {1} descriptions"),
            ("LogFixBatch", "{0}：检查 {1} 条混杂译文，改写 {2} 条", "{0}: {1} mixed translations checked, {2} rewritten"),
            ("LogStartFix", "开始修正混杂：{0} 个分组，共 {1} 条（{3}）", "Fixing mixed text: {0} groups, {1} entries ({3})"),
            ("LogFailedSuffix", "，失败 {0} 条", ", {0} failed"),
            ("LogSample", "{0} → {1}", "{0} → {1}"),
            ("LogDescribeSample", "{0}：{1}", "{0}: {1}"),
            ("LogRewrite", "改写：{0} → {1}", "Rewrote: {0} → {1}"),
            ("LogReviewFailed", "{0}：审校失败，保留原译文（{1}）", "{0}: review failed, kept the translation ({1})"),
            ("LogRetry", "请求出错，第 {0} 次重试：{1}", "Request failed, retry {0}: {1}"),
            ("LogScan", "检测完成：{0} 个 mod 待翻译（{1} 条），{2} 个资产缺描述", "Scan: {0} mods pending ({1} entries), {2} assets without a description"),
            ("LogLanguageChanged", "游戏语言切换为 {0}", "Game language changed to {0}"),
            ("LogI18NDetected", "已接入 I18N Everywhere {0}", "Connected to I18N Everywhere {0}"),
            ("LogLanguageOption", "已把 {0} 的语言选项改为跟随游戏", "Set the language option of {0} to follow the game"),
            ("LogNewMods", "发现 {0} 个新 mod：{1}", "Found {0} new mods: {1}"),
            ("LogAutoStart", "后台自动翻译 {0} 个新装/更新的 mod：{1}", "Translating {0} new or updated mods in the background: {1}"),
            ("LogAutoNeedsKey", "有新 mod 需要翻译，但还没填 API Key，已跳过", "New mods need translating but no API key is set; skipped"),
            ("NotifyAutoStart", "正在后台翻译 {0} 个新 mod", "Translating {0} new mods in the background"),

            // 接口
            ("Connecting", "正在连接 {0} …", "Connecting to {0} …"),
            ("ConnectOk", "连接成功 · {0} · {1} 秒\n示例：{2}", "Connected · {0} · {1} s\nSample: {2}"),
            ("ConnectFailed", "连接失败：{0}", "Connection failed: {0}"),

            // 运行环境
            ("NoI18N", "I18N Everywhere：未安装（译文直接写入游戏，建议安装）", "I18N Everywhere: not installed (writing to the game directly; installing it is recommended)"),
            ("PacksDisabled", "I18N Everywhere {0}：语言包加载已关闭，请在它的设置里开启“Load Language Packs”", "I18N Everywhere {0}: language packs are off; enable \"Load Language Packs\" in its settings"),
            ("PackRegistered", "I18N Everywhere {0}：正常", "I18N Everywhere {0}: OK"),
            ("OverwriteOff", "\n英语游戏要在 I18N Everywhere 里开启“Enable overwrite”才能显示译文", "\nEnable \"Enable overwrite\" in I18N Everywhere to show translations in English"),
            ("Folder", "\n译文目录：{0}", "\nTranslation folder: {0}"),
        };

        // 固定文字：标签页、分组、下拉选项、确认框。(逻辑键, 中文, 英文)
        // 只放数据（不碰 Setting 类型），测试程序不加载游戏 dll 也能读取；本地化 ID 在 Build 里按键名算出。
        private static readonly (string key, string zh, string en)[] s_Fixed =
        {
            ("TAB:" + Setting.kMainTab, "翻译", "Translate"),
            ("TAB:" + Setting.kLogTab, "日志", "Log"),
            ("TAB:" + Setting.kSettingsTab, "设置", "Settings"),
            ("TAB:" + Setting.kAdvancedTab, "高级", "Advanced"),

            ("GROUP:" + Setting.kStatusGroup, "概况", "Overview"),
            ("GROUP:" + Setting.kModGroup, "单个 mod", "One mod"),
            ("GROUP:" + Setting.kLogGroup, "日志（最新的在最上面）", "Log (newest first)"),
            ("GROUP:" + Setting.kApiGroup, "连接", "Connection"),
            ("GROUP:" + Setting.kAutoGroup, "自动处理", "Automatic"),
            ("GROUP:" + Setting.kTuningGroup, "翻译参数", "Translation"),
            ("GROUP:" + Setting.kMaintenanceGroup, "维护", "Maintenance"),

            ("ENUM:StatusFilterOption.All", "全部", "All"),
            ("ENUM:StatusFilterOption.Actionable", "需要处理", "Needs attention"),
            ("ENUM:StatusFilterOption.Pending", "待翻译", "Pending"),
            ("ENUM:StatusFilterOption.MissingDescriptions", "缺描述", "No description"),
            ("ENUM:StatusFilterOption.Polish", "待优化", "To polish"),
            ("ENUM:StatusFilterOption.Done", "已完成", "Done"),

            ("ENUM:ReviewScopeOption.Suspicious", "只查可疑的", "Suspicious only"),
            ("ENUM:ReviewScopeOption.Unreviewed", "没审过的", "Not yet reviewed"),
            ("ENUM:ReviewScopeOption.All", "全部", "Everything"),

            ("WARN:" + nameof(Setting.RetranslateSelected), "重新翻译这个 mod 的全部文本？已保存的译文会被覆盖。", "Retranslate all text of this mod? Saved translations will be overwritten."),
            ("WARN:" + nameof(Setting.DeleteSelected), "删除这个 mod 的 AI 译文和生成的描述？", "Delete this mod's AI translations and generated descriptions?"),
            ("WARN:" + nameof(Setting.TranslateAllNeeded), "翻译所有待翻译的 mod？会产生 API 费用。", "Translate all pending mods? This uses your API credit."),
            ("WARN:" + nameof(Setting.ReviewAllMods), "审校所有 mod 的译文？会产生 API 费用。", "Review the translations of all mods? This uses your API credit."),
            ("WARN:" + nameof(Setting.DescribeAllAssets), "给所有缺描述的资产写简介？会产生 API 费用。", "Describe all assets without a description? This uses your API credit."),
            ("WARN:" + nameof(Setting.FixMixedAll), "修正所有夹着英文的译文？会产生 API 费用。", "Fix all translations with leftover English? This uses your API credit."),
        };

        /// <summary>固定文字的逻辑键 → 游戏本地化 ID。</summary>
        private static string FixedId(Setting s, string key)
        {
            int colon = key.IndexOf(':');
            string kind = key.Substring(0, colon);
            string name = key.Substring(colon + 1);
            switch (kind)
            {
                case "TAB":
                    return s.GetOptionTabLocaleID(name);
                case "GROUP":
                    return s.GetOptionGroupLocaleID(name);
                case "WARN":
                    return s.GetOptionWarningLocaleID(name);
                case "ENUM":
                    int dot = name.IndexOf('.');
                    Type type = typeof(Setting).GetNestedType(name.Substring(0, dot));
                    return s.GetEnumValueLocaleID((Enum)Enum.Parse(type, name.Substring(dot + 1)));
                default:
                    throw new ArgumentException("Unknown UI key " + key);
            }
        }

        /// <summary>
        /// 预置的界面语言（英文、简体中文写在本文件里，其余放在 Locale/Languages/&lt;语言&gt;.json，编进 dll）。
        /// 就是游戏自带的全部语言；其他语言由本 mod 在填好 API Key 后自己翻译。
        /// </summary>
        public static readonly string[] BundledLocales =
        {
            "en-US", "zh-HANS", "zh-HANT", "de-DE", "es-ES", "fr-FR", "it-IT", "ja-JP", "ko-KR", "pl-PL", "pt-BR", "ru-RU",
        };

        /// <summary>全部界面文字：(逻辑键, 中文, 英文)。逻辑键也是语言文件里的键。</summary>
        public static IEnumerable<(string key, string zh, string en)> AllEntries()
        {
            foreach (var (name, zhLabel, zhDesc, enLabel, enDesc) in s_Options)
            {
                yield return ("OPT:" + name, zhLabel, enLabel);
                yield return ("DESC:" + name, zhDesc, enDesc);
            }
            foreach (var (key, zh, en) in s_Fixed)
            {
                yield return (key, zh, en);
            }
            foreach (var (name, zh, en) in s_Ui)
            {
                yield return ("UI:" + name, zh, en);
            }
        }

        /// <summary>读取编进 dll 的语言文件（逻辑键 → 文字）；没有该语言时返回 null。</summary>
        public static Dictionary<string, string> LoadLanguageFile(string locale, Action<string> onError = null)
        {
            try
            {
                using (var stream = typeof(LocaleTable).Assembly.GetManifestResourceStream("AITranslate.Lang." + locale + ".json"))
                {
                    if (stream == null)
                    {
                        return null;
                    }
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
                    }
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Cannot read built-in {locale} UI text: {ex.Message}");   // 不直接用 Mod.Log：测试程序不加载游戏 dll
                return null;
            }
        }

        /// <summary>某种语言的全部界面文字（本地化 ID → 文字）。语言文件里缺的条目用英文。</summary>
        public static Dictionary<string, string> Build(Setting s, string locale)
        {
            bool chinese = locale == "zh-HANS";
            var file = chinese || locale == "en-US" ? null : LoadLanguageFile(locale, message => Mod.Log.Warn(message));
            string Pick(string key, string zh, string en)
            {
                if (chinese)
                {
                    return zh;
                }
                return file != null && file.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text) ? text : en;
            }

            var d = new Dictionary<string, string> { { s.GetSettingsLocaleID(), Mod.DisplayName } };
            foreach (var (key, zh, en) in s_Fixed)
            {
                d[FixedId(s, key)] = Pick(key, zh, en);
            }
            foreach (var (name, zhLabel, zhDesc, enLabel, enDesc) in s_Options)
            {
                d[s.GetOptionLabelLocaleID(name)] = Pick("OPT:" + name, zhLabel, enLabel);
                d[s.GetOptionDescLocaleID(name)] = Pick("DESC:" + name, zhDesc, enDesc);
            }
            foreach (var (name, zh, en) in s_Ui)
            {
                string text = Pick("UI:" + name, zh, en);
                d[kUiPrefix + name] = text;
                // 「类型」下拉框的选项与界面里的类型名共用同一份文字。
                if (name.StartsWith("Category", StringComparison.Ordinal)
                    && Enum.TryParse(name.Substring("Category".Length), out Setting.ModCategory category))
                {
                    d[s.GetEnumValueLocaleID(category)] = text;
                }
            }
            return d;
        }

        public static string English(string name)
        {
            foreach (var (n, _, en) in s_Ui)
            {
                if (n == name)
                {
                    return en;
                }
            }
            return name;
        }
    }

    /// <summary>本 mod 设置页的一种语言。</summary>
    public class LocaleSource : IDictionarySource
    {
        private readonly Setting m_Setting;
        private readonly string m_Locale;

        public LocaleSource(Setting setting, string locale)
        {
            m_Setting = setting;
            m_Locale = locale;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return LocaleTable.Build(m_Setting, m_Locale);
        }

        public void Unload()
        {
        }
    }
}
