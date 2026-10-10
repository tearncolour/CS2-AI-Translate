using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AITranslate.Core
{
    internal sealed class TranslateItem
    {
        public string Key;
        public string Source;
    }

    internal sealed class Translator
    {
        private const int kMaxKeyContext = 120;
        private const int kGlossaryLimit = 60;

        private readonly ChatClient m_Client;
        private readonly string m_Locale;
        private readonly Glossary m_Glossary;
        private readonly string m_SystemPrompt;
        private readonly string m_ReviewPrompt;
        private readonly string m_ExtraSection;
        private readonly ProtectedNames m_Names;
        private string m_DescribePrompt;

        public int FailedItems;

        public Translator(ChatClient client, string locale, Glossary glossary, string extraPrompt, ProtectedNames names = null)
        {
            m_Client = client;
            m_Locale = locale;
            m_Glossary = glossary ?? Glossary.Empty;
            m_Names = names ?? ProtectedNames.Empty;
            m_SystemPrompt = BuildSystemPrompt(locale, null);
            m_ExtraSection = ExtraSection(locale, extraPrompt);
            m_ReviewPrompt = BuildReviewSystemPrompt() + m_ExtraSection;
            m_SystemPrompt += ExtraSection(locale, extraPrompt);
        }

        // ------------------------------------------------------------------ prompts

        private static readonly Dictionary<string, string> s_LanguageNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en-US"] = "English (United States)",
            ["zh-HANS"] = "Simplified Chinese (简体中文, mainland China)",
            ["zh-HANT"] = "Traditional Chinese (繁體中文, Taiwan)",
            ["ja-JP"] = "Japanese (日本語)",
            ["ko-KR"] = "Korean (한국어)",
            ["de-DE"] = "German (Deutsch)",
            ["fr-FR"] = "French (Français)",
            ["es-ES"] = "Spanish, Spain (Español de España)",
            ["it-IT"] = "Italian (Italiano)",
            ["pl-PL"] = "Polish (Polski)",
            ["pt-BR"] = "Brazilian Portuguese (Português do Brasil)",
            ["ru-RU"] = "Russian (Русский)",
            ["nl-NL"] = "Dutch (Nederlands)",
            ["tr-TR"] = "Turkish (Türkçe)",
            ["uk-UA"] = "Ukrainian (Українська)",
            ["cs-CZ"] = "Czech (Čeština)",
            ["fi-FI"] = "Finnish (Suomi)",
            ["sv-SE"] = "Swedish (Svenska)",
            ["da-DK"] = "Danish (Dansk)",
            ["nb-NO"] = "Norwegian Bokmål (Norsk bokmål)",
            ["hu-HU"] = "Hungarian (Magyar)",
            ["ro-RO"] = "Romanian (Română)",
            ["th-TH"] = "Thai (ไทย)",
            ["vi-VN"] = "Vietnamese (Tiếng Việt)",
            ["id-ID"] = "Indonesian (Bahasa Indonesia)",
            ["el-GR"] = "Greek (Ελληνικά)",
            ["ar-SA"] = "Arabic (العربية)",
            ["he-IL"] = "Hebrew (עברית)",
        };

        // 各语言的本地化习惯（只写把握较大的通用规范，具体用语以游戏官方术语表为准）。
        private static readonly Dictionary<string, string> s_LanguageNotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "Use natural US English as in the official game UI and follow the source's capitalization style. Chinese, Japanese and Korean proper names use their established English names, otherwise standard romanization (Hanyu Pinyin for Chinese, e.g. 广州南站 → Guangzhou South Railway Station); asset and building descriptions become fluent English.",
            ["de"] = "Use concise German UI language with natural compounds. For buttons and options prefer infinitive or noun style (e.g. \"Straße auswählen\") and keep the form of address consistent; avoid anglicisms when a common German term exists.",
            ["fr"] = "Follow French typography (non-breaking space before : ; ! ?, guillemets for quotes) and use the infinitive for buttons and actions (e.g. \"Sélectionner la route\").",
            ["es"] = "Use Spanish from Spain (not Latin American). Use the infinitive for buttons and actions and opening ¿ ¡ marks where needed.",
            ["it"] = "Use the infinitive for buttons and actions and natural Italian UI terminology.",
            ["pt"] = "Use Brazilian Portuguese spelling and vocabulary; infinitive for buttons and actions.",
            ["pl"] = "Use natural Polish with correct inflection. Placeholders may hold any number, so prefer phrasing that is correct for every number (e.g. \"Liczba: {0}\").",
            ["ru"] = "Use natural Russian with correct cases. Placeholders may hold any number, so prefer phrasing that is correct for every number (e.g. \"Количество: {0}\"). Use the infinitive or imperative consistently for buttons.",
            ["uk"] = "Use natural Ukrainian (not Russian) with correct cases; prefer number-neutral phrasing around numeric placeholders.",
            ["cs"] = "Use natural Czech with correct inflection; prefer number-neutral phrasing around numeric placeholders.",
            ["tr"] = "Use natural Turkish with correctly attached suffixes; prefer phrasing that does not depend on the placeholder value.",
            ["ja"] = "Use concise Japanese UI language: noun phrases or short verb forms for labels and buttons, です・ます調 for descriptions and tooltips. Use established katakana loanwords and full-width punctuation (、。「」).",
            ["ko"] = "Use concise Korean UI language: short noun forms for labels and buttons, 합니다체 for descriptions and tooltips, and standard Korean spacing.",
            ["th"] = "Use natural Thai UI language without unnecessary spaces between words.",
            ["vi"] = "Use natural Vietnamese UI language with full diacritics.",
        };

        public static string LanguageName(string locale)
        {
            return locale != null && s_LanguageNames.TryGetValue(locale, out var name) ? name : locale;
        }

        private static string BuildSystemPrompt(string locale, string extra)
        {
            string lower = (locale ?? string.Empty).ToLowerInvariant();
            var sb = new StringBuilder();
            if (lower == "zh-hans")
            {
                sb.Append(@"你是《城市：天际线2》（Cities: Skylines II）的资深简体中文本地化译者，熟悉城市规划、道路交通、公共交通、市政设施、建筑以及 mod 社区用语。请把 mod 的界面文本翻译成地道、自然、符合中国大陆玩家习惯的简体中文。
原文通常是英文，也可能是德语、俄语、日语、繁体中文等其他语言，一律译为简体中文；繁体原文请转换为简体并改用大陆用语（如 滑鼠→鼠标、預設→默认、檔案→文件）。

【翻译原则】
1. 本土化优先：理解原意后用中文习惯重新表达，杜绝翻译腔和逐词硬译。按钮、选项、标题要简短有力（常用动宾结构，结尾不加句号）；说明、提示文字通顺完整，语气友好自然。
2. 尽量全部译成中文：只要能翻译的外文都要翻译，不要中英混杂，不要在译文后括注原文。
   仅以下“特殊专有名词”保留原文：
   - 品牌、厂商、车型或产品型号（如 Volvo B8L、CR400AF、Mercedes-Benz、BYD K9A）；
   - 人名、作者名、外文网名；
   - 技术名词与缩写（如 Harmony、Burst、Unity、JSON、CSV、XML、URL、API、FPS、LOD、UI、ID、GPU、CPU、DLC）；
   - 键盘/鼠标按键名（Ctrl、Shift、Alt、Tab、Enter、Esc、F1 等）；
   - 翻译后会让玩家无法辨认的特殊名称。
   有通行中文译名的真实地名、地标、建筑、机构一律使用通行译名（如 Empire State Building → 帝国大厦，Central Park → 中央公园）。
3. mod、工具、资产包的名称是专有名词，一律保留英文原名，不翻译、不意译、不加中文注释（如“移除所有 Bridge Builder 桥梁”，不要写成“桥梁建造者”）；输入中 keep_names 列出的名称必须原样出现在译文里。设置页标题（key 以 Options.SECTION 开头）就是 mod 名，原样返回。
4. 术语统一：输入中的 glossary 是游戏官方中文译名，出现对应词时必须使用；同一批次、同一 mod 内的同一术语保持一致。常用对照：mod→模组，asset→资产，prefab→预制件，vanilla→原版，node→节点，segment→路段，lane→车道，intersection→路口，roundabout→环岛，hotkey/keybinding→快捷键，tooltip→提示，preset→预设，toggle→开关，enable/disable→启用/禁用，default→默认，overlay→叠加层，info view→信息视图，cims→市民。
5. 格式必须原样保留：
   - 大括号占位符 {0}、{NAME}、{{X}} 等保持原样，括号内的内容不得翻译或改动；
   - 富文本/HTML 标签 <b>、</b>、<color=#fff>、<br> 等保持原样；
   - %s、%d、%1$s 等格式符保持原样；
   - 换行（\n）的数量和位置与原文对应；项目符号、Markdown 标记保留；
   - 数字、单位、版本号不变，英制单位不换算。
6. 标点：使用中文全角标点（，。：；！？“”（）），省略号用“……”；占位符、数字、保留的英文专名内部的符号保持原样。
7. key 仅作上下文参考（可据此判断是标题、选项、描述、提示还是资产名），不要翻译 key。若原文本身是代码标识、文件路径、URL 或无意义字符串，原样返回。
8. 资产名称（key 以 Assets.NAME 或 Assets.UPGRADE_NAME 开头）可能是由内部文件名拆分而来（如 “CS Bank Tower Tall 01”、“EU Bus Stop 02”），请整理成玩家看得懂的自然中文资产名，保留编号、品牌和型号。按游戏官方格式：等级写作 L1~L5、占地写作 2x4（不要写成“3级”“2×4”），地区代码 EU=欧洲、NA=北美、CN=中国、JP=日本、EE=东欧、UK=英国、FR=法国、DE=德国、NL=荷兰、USNE=美国东北部、USSW=美国西南部，Low/Medium/High 在建筑名里是低层/中层/高层；Bonus、Extra、Req、ProcReq、Details、Random 等是作者的内部标记，直接去掉，不要翻译成“奖励”“额外”之类的词。

【输出格式】
只输出一个 JSON 对象，形如 {""1"":""译文"",""2"":""译文""}，必须包含输入 items 中的全部 id，不要输出任何解释或多余内容。");
            }
            else if (lower == "zh-hant")
            {
                sb.Append(@"你是《都市：天際線2》（Cities: Skylines II）的資深繁體中文在地化譯者，熟悉都市規劃、道路交通、大眾運輸、公共設施、建築及 mod 社群用語。請把 mod 的介面文字翻譯成道地、自然、符合台灣玩家習慣的繁體中文（使用台灣用語，例如：設定、載入、預設、資料、檔案、品質、滑鼠）。
原文通常是英文，也可能是簡體中文、德語、俄語、日語等其他語言，一律譯為繁體中文；簡體原文請轉換為繁體並改用台灣用語（如 鼠标→滑鼠、默认→預設、文件→檔案）。

【翻譯原則】
1. 在地化優先：理解原意後以中文習慣重新表達，避免翻譯腔與逐字硬譯。按鈕、選項、標題簡短有力（結尾不加句號）；說明、提示文字通順完整。
2. 盡量全部譯成中文：能翻譯的外文都要翻譯，不要中英夾雜，不要在譯文後括註原文。
   僅以下「特殊專有名詞」保留原文：品牌、廠商、車型或產品型號；人名、作者名；技術名詞與縮寫（Harmony、Burst、Unity、JSON、URL、API、FPS、LOD、UI、ID、DLC 等）；鍵盤/滑鼠按鍵名（Ctrl、Shift、Alt 等）；翻譯後玩家無法辨認的特殊名稱。
   有通行中文譯名的真實地名、地標、建築一律使用通行譯名。
3. mod、工具、資產包的名稱是專有名詞，一律保留英文原名，不翻譯、不加中文註釋；輸入中 keep_names 列出的名稱必須原樣出現在譯文中。設定頁標題（key 以 Options.SECTION 開頭）就是 mod 名，原樣返回。
4. 術語統一：輸入中的 glossary 是遊戲官方譯名，出現時必須使用；同一 mod 內同一術語保持一致。
5. 格式必須原樣保留：{0}、{NAME} 等大括號佔位符（括號內不得翻譯）、<b>、<color=…> 等標籤、%s、%d 等格式符、換行（\n）的數量與位置、數字、單位、版本號。
6. 標點使用全形中文標點（，。：；！？「」（）），刪節號用「……」。
7. key 僅作上下文參考，不要翻譯 key。原文若為代碼識別字、檔案路徑、URL，原樣返回。
8. 資產名稱（key 以 Assets.NAME 或 Assets.UPGRADE_NAME 開頭）可能由內部檔名拆分而來（如「CS Bank Tower Tall 01」），請整理成自然的中文資產名，保留編號、品牌與型號。依遊戲官方格式：等級寫作 L1~L5、占地寫作 2x4，地區代碼 EU=歐洲、NA=北美、CN=中國、JP=日本、EE=東歐、USNE=美國東北部、USSW=美國西南部，Low/Medium/High 在建築名中是低層/中層/高層；Bonus、Extra、Req、ProcReq、Details、Random 等是作者的內部標記，直接去掉。

【輸出格式】
只輸出一個 JSON 物件，形如 {""1"":""譯文"",""2"":""譯文""}，必須包含輸入 items 中的全部 id，不要輸出任何說明。");
            }
            else
            {
                string lang = LanguageName(locale);
                sb.Append("You are a senior game localizer for Cities: Skylines II, fluent in city-planning, traffic, transit, utilities, buildings and modding-community vocabulary. Translate the mod UI strings into natural, idiomatic ").Append(lang)
                  .Append(" exactly as native players would expect in the official localized version of the game.").Append(@"
The source text is usually English but may be written in any language (e.g. Chinese, German, Russian, Japanese); always translate from whatever language it is in into the target language.

Rules:
1. Localize, don't transliterate: rephrase naturally, avoid translationese. Buttons, options and titles must be short; descriptions fluent and complete.
2. Translate everything that can be translated. Do not mix in other languages and do not append the original in parentheses.
   Keep the original ONLY for truly special proper nouns: brands, manufacturers, vehicle or product model names; person/author names; technical terms and acronyms (Harmony, Burst, Unity, JSON, URL, API, FPS, LOD, UI, ID, DLC…); keyboard/mouse key names (Ctrl, Shift, Alt…); names that would become unrecognizable if translated.
   Real places, landmarks and buildings use their established names in the target language.
3. Names of mods, tools and asset packs are proper nouns: always keep them exactly in their original form, never translate them or add a translated gloss. Every name listed in keep_names must appear unchanged in the translation. Settings section titles (key starts with Options.SECTION) are mod names: return them unchanged.
4. Terminology: the glossary contains the game's official translations; use them whenever the term appears and stay consistent within a mod.
5. Preserve formatting exactly: {0}, {NAME}, {{X}} placeholders (never translate their content), rich-text/HTML tags like <b>, <color=…>, format specifiers like %s/%d, the number and position of line breaks (\n), numbers, units and version strings.
6. Use the target language's native punctuation and typography conventions.
7. The key is context only (title, option, description, tooltip, asset name…); never translate it. If the text is a code identifier, file path or URL, return it unchanged.");
                sb.Append("\n8. Asset names (key starts with Assets.NAME or Assets.UPGRADE_NAME) may be split from internal file names (e.g. \"CS Bank Tower Tall 01\"); turn them into natural asset names, keep numbering, brands and model names, and drop meaningless prefix abbreviations. Follow the official format: keep level codes as L1–L5 and lot sizes as 2x4; region codes mean EU = Europe, NA = North America, CN = China, JP = Japan, EE = Eastern Europe, UK = United Kingdom, FR = France, DE = Germany, NL = Netherlands, USNE = USA Northeast, USSW = USA Southwest; Low/Medium/High in building names are low-rise/mid-rise/high-rise; Bonus, Extra, Req, ProcReq, Details and Random are internal author markers: drop them, never translate them as words like \"reward\".");
                if (s_LanguageNotes.TryGetValue(LanguageDetector.LanguageOf(locale), out var notes))
                {
                    sb.Append("\n9. Language-specific: ").Append(notes);
                }
                sb.Append(@"

Output: return ONLY one JSON object like {""1"":""translation"",""2"":""translation""} containing every id from the input items. No explanations.");
            }
            return sb.ToString();
        }

        private static string ExtraSection(string locale, string extra)
        {
            if (string.IsNullOrWhiteSpace(extra))
            {
                return string.Empty;
            }
            bool chinese = (locale ?? string.Empty).StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            return (chinese ? "\n\n【玩家额外要求】\n" : "\n\nAdditional instructions from the player:\n") + extra.Trim();
        }

        private string BuildUserPrompt(string modName, IList<TranslateItem> items, bool retryHint)
        {
            var payload = new JObject
            {
                ["mod"] = modName,
                ["target_language"] = LanguageName(m_Locale),
            };
            var terms = m_Glossary.Match(items.Select(i => i.Source), kGlossaryLimit);
            if (terms.Count > 0)
            {
                var g = new JObject();
                foreach (var t in terms)
                {
                    g[t.English] = t.Target;
                }
                payload["glossary"] = g;
            }
            var arr = new JArray();
            for (int i = 0; i < items.Count; i++)
            {
                string key = items[i].Key;
                arr.Add(new JObject
                {
                    ["id"] = (i + 1).ToString(),
                    ["key"] = key.Length > kMaxKeyContext ? key.Substring(0, kMaxKeyContext) + "…" : key,
                    ["text"] = items[i].Source,
                });
            }
            AddKeepNames(payload, items.Select(i => i.Source));
            payload["items"] = arr;

            string prefix = retryHint
                ? (m_Locale.StartsWith("zh") ? "注意：上一次的部分译文丢失或改动了占位符/标签，请逐条仔细检查后重新翻译。\n" : "Note: some previous translations lost or altered placeholders/tags. Check each one carefully.\n")
                : string.Empty;
            return prefix + "Translate the following JSON items and reply with json:\n" + payload.ToString(Formatting.None);
        }

        /// <summary>本批文本中出现的 mod 名等专有名词，要求原样保留。</summary>
        private void AddKeepNames(JObject payload, IEnumerable<string> texts)
        {
            var names = texts.SelectMany(t => m_Names.FindIn(t)).Distinct().Take(30).ToList();
            if (names.Count > 0)
            {
                payload["keep_names"] = new JArray(names.Cast<object>().ToArray());
            }
        }

        // ------------------------------------------------------------------ translate

        /// <returns>key → 译文；无法得到合格译文的条目不包含在结果中。</returns>
        public async Task<Dictionary<string, string>> TranslateAsync(string modName, IList<TranslateItem> items, CancellationToken ct)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            List<TranslateItem> remaining = items.ToList();

            for (int round = 0; round < 2 && remaining.Count > 0; round++)
            {
                Dictionary<string, string> got = await CallWithRetryAsync(modName, remaining, round > 0, ct).ConfigureAwait(false);
                var failed = new List<TranslateItem>();
                foreach (var item in remaining)
                {
                    if (got.TryGetValue(item.Key, out var t) && TextRules.IsValidTranslation(item.Source, t, out _) && !m_Names.Lost(item.Source, t, out _))
                    {
                        result[item.Key] = t;
                    }
                    else
                    {
                        failed.Add(item);
                    }
                }
                remaining = failed;
            }

            // 最后对少量顽固条目逐条再试一次。
            if (remaining.Count > 0 && remaining.Count <= 5 && items.Count > 1)
            {
                var stillFailed = new List<TranslateItem>();
                foreach (var item in remaining)
                {
                    var got = await CallWithRetryAsync(modName, new List<TranslateItem> { item }, true, ct).ConfigureAwait(false);
                    if (got.TryGetValue(item.Key, out var t) && TextRules.IsValidTranslation(item.Source, t, out _) && !m_Names.Lost(item.Source, t, out _))
                    {
                        result[item.Key] = t;
                    }
                    else
                    {
                        stillFailed.Add(item);
                    }
                }
                remaining = stillFailed;
            }

            foreach (var item in remaining)
            {
                Interlocked.Increment(ref FailedItems);
                Mod.Log.Info("Skip untranslatable entry " + item.Key + ": " + ChatClient.Truncate(item.Source, 80));
            }
            return result;
        }

        private async Task<Dictionary<string, string>> CallWithRetryAsync(string modName, IList<TranslateItem> items, bool retryHint, CancellationToken ct)
        {
            const int kAttempts = 4;
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string content = await m_Client.CompleteAsync(m_SystemPrompt, BuildUserPrompt(modName, items, retryHint), jsonMode: true, ct).ConfigureAwait(false);
                    return ParseResponse(content, items);
                }
                catch (ApiException ex) when (!ex.Fatal && attempt < kAttempts)
                {
                    Mod.Log.Warn($"API error (attempt {attempt}): {ex.Message}");
                    ActivityLog.Retry(attempt, ex.Message);
                    await Task.Delay(Backoff(attempt, ex.StatusCode == 429), ct).ConfigureAwait(false);
                }
                catch (JsonException ex) when (attempt < kAttempts)
                {
                    Mod.Log.Warn($"Malformed JSON from model (attempt {attempt}): {ex.Message}");
                }
                catch (JsonException)
                {
                    return new Dictionary<string, string>();
                }
            }
        }

        private static TimeSpan Backoff(int attempt, bool rateLimited)
        {
            int seconds = rateLimited ? 10 * attempt : 2 * attempt * attempt;
            return TimeSpan.FromSeconds(seconds);
        }

        // ------------------------------------------------------------------ describe

        private string BuildDescribeSystemPrompt()
        {
            string lower = m_Locale.ToLowerInvariant();
            if (lower == "zh-hans")
            {
                return @"你是《城市：天际线2》（Cities: Skylines II）的资产说明撰写者。玩家在建造菜单里看到的这些 mod 资产没有说明文字，请为每个资产写一段简体中文说明，让玩家一眼知道它是什么、适合用在哪里。

【要求】
1. 1~2 句话，约 20~60 个汉字，客观简洁，语气像游戏官方的资产说明；不要以资产名称开头复述名字。
2. 只依据给出的信息（名称、英文名、内部名、菜单分类、分区与等级、所属服务、占地格数、功能组件）、mod 简介和公认常识来写，不要编造具体数值、容量、收益或游戏效果。
3. 如果是真实存在的知名建筑、地标、车辆或品牌，可以简要点明其真实身份（如所在城市、类型），但不确定时不要猜。
4. 不要出现 prefab、component、kind、menu_category 等技术词，也不要原样罗列内部字段。
5. 使用中文全角标点，品牌、型号等专有名词保留原文。
6. name 为 null 的资产只有一串随机编号作名字，请同时为它起一个名字：根据道路截面（cross_section：车道、人行道、中央分隔带的种类和数量）、道路/轨道类型、限速、菜单分类、占地、功能组件和 mod 简介判断它是什么，起一个简洁、像游戏官方风格的中文名（如“双向四车道道路”“高架单向匝道”“带中央分隔带的六车道大道”），不超过 16 个字；同一 mod 内看起来相同的资产在名字后加 01、02 区分。

【输出格式】
只输出一个 JSON 对象，必须包含输入中的全部 id，不要输出任何解释。普通资产的值是说明字符串；name 为 null 的资产的值是对象：
{""1"":""说明"",""2"":{""name"":""名字"",""description"":""说明""}}";
            }
            if (lower == "zh-hant")
            {
                return @"你是《都市：天際線2》（Cities: Skylines II）的資產說明撰寫者。玩家在建造選單裡看到的這些 mod 資產沒有說明文字，請為每個資產寫一段繁體中文（台灣用語）說明，讓玩家一眼知道它是什麼、適合用在哪裡。

【要求】
1. 1~2 句話，約 20~60 個字，客觀簡潔，語氣像遊戲官方的資產說明；不要以資產名稱開頭複述名字。
2. 只依據給出的資訊（名稱、英文名、內部名、選單分類、分區與等級、所屬服務、占地格數、功能元件）、mod 簡介與公認常識撰寫，不要編造具體數值、容量、收益或遊戲效果。
3. 若是真實存在的知名建築、地標、車輛或品牌，可簡要點明其真實身分，不確定時不要猜。
4. 不要出現 prefab、component 等技術詞，也不要原樣羅列內部欄位。
5. 使用全形中文標點，品牌、型號等專有名詞保留原文。
6. name 為 null 的資產只有一串隨機編號作名字，請同時為它取名：依據道路截面（cross_section）、道路/軌道類型、速限、選單分類、占地、功能元件與 mod 簡介判斷它是什麼，取一個簡潔、像遊戲官方風格的中文名，不超過 16 個字；同一 mod 內看起來相同的資產在名字後加 01、02 區分。

【輸出格式】
只輸出一個 JSON 物件，必須包含輸入中的全部 id。一般資產的值是說明字串；name 為 null 的資產的值是物件：
{""1"":""說明"",""2"":{""name"":""名字"",""description"":""說明""}}";
            }
            string lang = LanguageName(m_Locale);
            var sb = new StringBuilder();
            sb.Append("You write in-game asset descriptions for Cities: Skylines II. These mod assets appear in the build menu without any description. For each asset write a short description in ").Append(lang)
              .Append(@" so players immediately understand what it is and where to use it.

Rules:
1. One or two short sentences (about 10–30 words), objective and concise, in the style of the official asset descriptions. Do not start by repeating the asset name.
2. Base it only on the given information (name, English name, internal name, menu category, zone and level, service, lot size in cells, feature components), the mod summary and common knowledge. Never invent numbers, capacities, income or gameplay effects.
3. If it is a well-known real building, landmark, vehicle or brand you may briefly state what it really is (e.g. its city); do not guess when unsure.
4. Do not use technical words such as prefab, component, kind or menu_category, and do not list internal fields.
5. Keep brand and model names as they are.
6. Assets whose name is null only have a random ID as their name; also invent a name. Work out what it is from the cross_section (lanes, sidewalks, medians and their counts), network type, speed limit, menu category, lot size, features and the mod summary, and give a concise name in the style of the official game (e.g. ""Two-lane Road"", ""Elevated One-way Ramp""), at most about 5 words. Add 01, 02 to tell apart assets of the same mod that look identical.");
            if (s_LanguageNotes.TryGetValue(LanguageDetector.LanguageOf(m_Locale), out var notes))
            {
                sb.Append("\n7. Language-specific: ").Append(notes);
            }
            sb.Append(@"

Output: return ONLY one JSON object containing every id. No explanations. For normal assets the value is the description string; for assets whose name is null the value is an object:
{""1"":""description"",""2"":{""name"":""name"",""description"":""description""}}");
            return sb.ToString();
        }

        /// <returns>描述 key → 生成的描述；不合格的不包含在结果中。</returns>
        public async Task<Dictionary<string, string>> DescribeAsync(string modName, string modSummary, IList<AssetContext> assets, CancellationToken ct)
        {
            var payload = new JObject
            {
                ["mod"] = modName,
                ["target_language"] = LanguageName(m_Locale),
            };
            if (!string.IsNullOrWhiteSpace(modSummary))
            {
                payload["mod_summary"] = ChatClient.Truncate(modSummary.Trim(), 400);
            }
            var arr = new JArray();
            for (int i = 0; i < assets.Count; i++)
            {
                var o = assets[i].ToJson();
                o.AddFirst(new JProperty("id", (i + 1).ToString()));
                arr.Add(o);
            }
            payload["assets"] = arr;
            string user = "Write descriptions for the following JSON assets and reply with json:\n" + payload.ToString(Formatting.None);
            string system = m_DescribePrompt ?? (m_DescribePrompt = BuildDescribeSystemPrompt() + m_ExtraSection);

            const int kAttempts = 4;
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string content = await m_Client.CompleteAsync(system, user, jsonMode: true, ct).ConfigureAwait(false);
                    JObject obj = ExtractObject(content);
                    var result = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int i = 0; i < assets.Count; i++)
                    {
                        JToken token = obj[(i + 1).ToString()];
                        string text;
                        string name = null;
                        if (token != null && token.Type == JTokenType.Object)
                        {
                            text = token["description"]?.ToString()?.Trim();
                            name = token["name"]?.ToString()?.Trim();
                        }
                        else
                        {
                            text = token?.ToString()?.Trim();
                        }
                        if (IsValidDescription(text))
                        {
                            result[assets[i].DescriptionKey] = text;
                            if (assets[i].NeedsName && IsValidName(name))
                            {
                                result[assets[i].TitleKey] = name;
                            }
                        }
                        else
                        {
                            Interlocked.Increment(ref FailedItems);
                        }
                    }
                    return result;
                }
                catch (ApiException ex) when (!ex.Fatal && attempt < kAttempts)
                {
                    Mod.Log.Warn($"Describe API error (attempt {attempt}): {ex.Message}");
                    ActivityLog.Retry(attempt, ex.Message);
                    await Task.Delay(Backoff(attempt, ex.StatusCode == 429), ct).ConfigureAwait(false);
                }
                catch (JsonException ex) when (attempt < kAttempts)
                {
                    Mod.Log.Warn($"Malformed describe JSON (attempt {attempt}): {ex.Message}");
                }
                catch (JsonException)
                {
                    return new Dictionary<string, string>();
                }
            }
        }

        private bool IsValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 40 || name.IndexOf('\n') >= 0)
            {
                return false;
            }
            if (TextRules.ExtractPlaceholders(name).Count > 0 || TextRules.IsMachineIdentifier(name))
            {
                return false;
            }
            return LanguageDetector.ScriptMatches(name, m_Locale);
        }

        private bool IsValidDescription(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 240)
            {
                return false;
            }
            // 描述会直接显示在游戏里，不能出现会被当成占位符或富文本的符号。
            if (TextRules.ExtractPlaceholders(text).Count > 0 || text.IndexOf('\n') >= 0)
            {
                return false;
            }
            return LanguageDetector.ScriptMatches(text, m_Locale);
        }

        // ------------------------------------------------------------------ building types

        /// <summary>
        /// 翻译区域建筑的类型短语（"CN Bonus Residential High"）。与普通翻译不同：带上游戏原版的官方对照作范例，
        /// 并说明地区代码和内部标记的含义，让结果与原版格式完全一致。
        /// </summary>
        /// <param name="examples">原版官方对照（"EU Residential Low" → "欧洲低层住宅"）。</param>
        public async Task<Dictionary<string, string>> TranslateBuildingTypesAsync(IList<string> phrases, IReadOnlyDictionary<string, string> examples, CancellationToken ct,
            IReadOnlyDictionary<string, (string english, string translated)> zones = null)
        {
            var sb = new StringBuilder();
            sb.Append("You name growable zone buildings in Cities: Skylines II in ").Append(LanguageName(m_Locale)).Append(@".
Each input is a building type phrase made of a REGION CODE followed by a ZONE TYPE, taken from internal file names of region packs.
Translate each phrase so it matches the official game's naming style EXACTLY, imitating the official examples (same word order, same words for density/zone, same way of writing Left/Right, no extra words, no punctuation added).

Region codes: EU = Europe, NA = North America, CN = China, JP = Japan, EE = Eastern Europe, UK = United Kingdom, FR = France, DE = Germany, NL = Netherlands, USNE = USA Northeast, USSW = USA Southwest. Use the usual name of the region in the target language.
Zone words: Low / Medium / High describe the building height class as in the examples (low-rise / mid-rise / high-rise), Row = row housing, Low Rent = low-rent housing, Mixed = mixed housing, Courtyard = courtyard housing, Office = offices, Commercial = commercial.
""Bonus"" is an internal marker the pack authors use for extra style variants. It is NOT a reward: drop it completely (""CN Bonus Residential High"" is named like ""CN Residential High"").
Signature = a signature building: translate it with the game's usual term (e.g. 标志性 in Simplified Chinese). Unique = unique variant.
If the words form a specific building or object name rather than a zone type (e.g. Financial Center, Shopping Center, Book Shop, Skyline Palace, Public Bench), translate it as a natural name and drop the region code unless it is needed for meaning.
Keep any number exactly as it is.");
            // 本批短语涉及的官方分区名：说明创作者包代码（CCP5 等）代表哪个包，也给出该包的官方叫法。
            if (zones != null)
            {
                var codes = new HashSet<string>(phrases.Select(p => p.Split(' ')[0]), StringComparer.Ordinal);
                var related = zones.Where(z => codes.Contains(z.Key.Split(' ')[0])).Take(20).ToList();
                if (related.Count > 0)
                {
                    sb.Append("\n\nOfficial zone names for these codes (English => current official translation, which may itself be partly untranslated):\n");
                    foreach (var z in related)
                    {
                        sb.Append(z.Key).Append(" = \"").Append(z.Value.english).Append("\" => ").Append(z.Value.translated).Append('\n');
                    }
                    sb.Append("A code like CCP5 is an official Creator Pack: render it as the pack's name (taken from the zone names above) translated into the target language, followed by the zone type, in the same style as the official examples. Translate the pack name too unless it is a proper name.\n");
                }
            }
            if (examples != null && examples.Count > 0)
            {
                sb.Append("\n\nOfficial examples:\n");
                foreach (var kv in examples.Take(40))
                {
                    sb.Append(kv.Key).Append(" => ").Append(kv.Value).Append('\n');
                }
            }
            sb.Append(@"
Output: return ONLY one JSON object like {""1"":""translation"",""2"":""translation""} containing every id. No explanations.");

            var payload = new JObject();
            for (int i = 0; i < phrases.Count; i++)
            {
                payload[(i + 1).ToString()] = phrases[i];
            }
            string user = "Translate these building type phrases and reply with json:\n" + payload.ToString(Formatting.None);

            const int kAttempts = 4;
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string content = await m_Client.CompleteAsync(sb.ToString(), user, jsonMode: true, ct).ConfigureAwait(false);
                    JObject obj = ExtractObject(content);
                    var result = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int i = 0; i < phrases.Count; i++)
                    {
                        string t = obj[(i + 1).ToString()]?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(t) && t.Length <= 40 && t.IndexOf('\n') < 0)
                        {
                            result[phrases[i]] = t;
                        }
                    }
                    return result;
                }
                catch (ApiException ex) when (!ex.Fatal && attempt < kAttempts)
                {
                    Mod.Log.Warn($"Building type API error (attempt {attempt}): {ex.Message}");
                    ActivityLog.Retry(attempt, ex.Message);
                    await Task.Delay(Backoff(attempt, ex.StatusCode == 429), ct).ConfigureAwait(false);
                }
                catch (JsonException ex) when (attempt < kAttempts)
                {
                    Mod.Log.Warn($"Malformed building type JSON (attempt {attempt}): {ex.Message}");
                }
                catch (JsonException)
                {
                    return new Dictionary<string, string>();
                }
            }
        }

        // ------------------------------------------------------------------ mod titles

        /// <summary>
        /// 给 mod 名起一个本地化的简短译名（只用于选项菜单的 mod 列表，显示成“译名 (原名)”）。
        /// 返回原名 → 译名；无法意译的品牌名返回原名本身。
        /// </summary>
        public async Task<Dictionary<string, string>> TranslateModNamesAsync(IList<string> names, CancellationToken ct)
        {
            string system = "You localize the names of Cities: Skylines II mods into " + LanguageName(m_Locale) + @".
The result is shown as ""<your name> (<original name>)"" in the game's options menu, so players can tell at a glance what each mod does while still seeing the original name.
Rules:
- Give a short, natural name a native player would use, conveying what the mod does (e.g. ""Better Bulldozer"" => 推土机增强, ""Move It"" => 移动工具, ""Auto Bus Lines"" => 自动公交线路, ""More Money"" => 更多资金, ""529 Tiles"" => 529 地块 in Simplified Chinese).
- Do NOT repeat the original name, do not add brackets, quotes or explanations.
- Keep numbers, and keep abbreviations that have no translation (e.g. EAI, RCI) as they are.
- If the name is a pure brand or person name with no meaning to convey, return it unchanged.
Output: return ONLY one JSON object like {""1"":""name"",""2"":""name""} containing every id.";

            var payload = new JObject();
            for (int i = 0; i < names.Count; i++)
            {
                payload[(i + 1).ToString()] = names[i];
            }
            string user = "Localize these mod names and reply with json:\n" + payload.ToString(Formatting.None);

            const int kAttempts = 4;
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string content = await m_Client.CompleteAsync(system, user, jsonMode: true, ct).ConfigureAwait(false);
                    JObject obj = ExtractObject(content);
                    var result = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int i = 0; i < names.Count; i++)
                    {
                        string t = obj[(i + 1).ToString()]?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(t) && t.Length <= 40 && t.IndexOf('\n') < 0 && TextRules.ExtractPlaceholders(t).Count == 0)
                        {
                            result[names[i]] = t;
                        }
                    }
                    return result;
                }
                catch (ApiException ex) when (!ex.Fatal && attempt < kAttempts)
                {
                    Mod.Log.Warn($"Mod name API error (attempt {attempt}): {ex.Message}");
                    ActivityLog.Retry(attempt, ex.Message);
                    await Task.Delay(Backoff(attempt, ex.StatusCode == 429), ct).ConfigureAwait(false);
                }
                catch (JsonException ex) when (attempt < kAttempts)
                {
                    Mod.Log.Warn($"Malformed mod name JSON (attempt {attempt}): {ex.Message}");
                }
                catch (JsonException)
                {
                    return new Dictionary<string, string>();
                }
            }
        }

        // ------------------------------------------------------------------ review

        internal sealed class ReviewItem
        {
            public string Key;
            public string Source;
            public string Translation;
            public List<string> Issues;
        }

        internal sealed class ReviewResult
        {
            public int Score;
            public string Fix;
        }

        /// <summary>审校阈值：评分不高于此值的条目要给出改进译文。</summary>
        public const int kFixThreshold = 3;

        private string BuildReviewSystemPrompt()
        {
            if (m_Locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                int i = m_SystemPrompt.IndexOf("【输出格式】", StringComparison.Ordinal);
                if (i < 0)
                {
                    i = m_SystemPrompt.IndexOf("【輸出格式】", StringComparison.Ordinal);
                }
                return m_SystemPrompt.Substring(0, i > 0 ? i : m_SystemPrompt.Length)
                    + @"
【你现在的任务：审校】
以上是这批译文应当遵守的翻译规范。请逐条审校输入中的 translation（原文为 source，key 仅作上下文），按 1~5 分打分：
5 = 地道准确，可直接使用；4 = 基本没问题，只有极小瑕疵；3 = 能看懂但有翻译腔、用词不当或不符合术语表；2 = 有明显错误、漏译或残留未翻译的外文；1 = 意思错误或格式损坏。
issues 是程序自动检测到的疑点（如残留英文、与官方术语不一致、长度异常），仅供参考，需要你自己判断。
评分 ≤ 3 的条目必须给出改进后的译文 fix，fix 必须遵守全部翻译规范并原样保留所有占位符和标签；评分 ≥ 4 的条目不要给 fix。
不要为了“更具体”而改写已经正确的译文：fix 不能加入原文没有的信息，特别是不要根据 mod 名给译文加上“东欧”“北美”“中式”之类的地区词（原文 “High School” 就译“高中”，不要写成“东欧中学”）；意思正确、只是措辞不同的译文应给 4 分以上并保持原样。

【输出格式】
只输出一个 JSON 对象，形如 {""1"":{""s"":5},""2"":{""s"":2,""fix"":""改进后的译文""}}，必须包含输入中的全部 id，不要输出任何解释。";
            }
            return m_SystemPrompt.Substring(0, m_SystemPrompt.IndexOf("\n\nOutput:", StringComparison.Ordinal) is int j && j > 0 ? j : m_SystemPrompt.Length)
                + @"

Your task now is REVIEW. The rules above are the standard these translations must meet. Score every item's translation (source is the original, key is context only) from 1 to 5:
5 = natural and correct, ready to ship; 4 = fine with a negligible flaw; 3 = understandable but translationese, wrong word choice or not following the glossary; 2 = clear errors, omissions or untranslated words left in; 1 = wrong meaning or broken formatting.
issues lists automatic hints (leftover source language, glossary mismatch, odd length); use your own judgement.
For every item scored 3 or lower you MUST give an improved translation in fix that follows all rules and keeps every placeholder and tag. Do not rewrite a correct translation just to make it more specific: a fix must not add information that is not in the source, especially region or country words inferred from the mod name (""High School"" stays ""High School"", not ""Eastern European High School""). A correct translation with merely different wording deserves 4 or more and stays unchanged. Do not give fix for items scored 4 or 5.

Output: return ONLY one JSON object like {""1"":{""s"":5},""2"":{""s"":2,""fix"":""improved translation""}} containing every id. No explanations.";
        }

        private string BuildReviewUserPrompt(string modName, IList<ReviewItem> items)
        {
            var payload = new JObject
            {
                ["mod"] = modName,
                ["target_language"] = LanguageName(m_Locale),
            };
            var terms = m_Glossary.Match(items.Select(i => i.Source), kGlossaryLimit);
            if (terms.Count > 0)
            {
                var g = new JObject();
                foreach (var t in terms)
                {
                    g[t.English] = t.Target;
                }
                payload["glossary"] = g;
            }
            var arr = new JArray();
            for (int i = 0; i < items.Count; i++)
            {
                string key = items[i].Key;
                var o = new JObject
                {
                    ["id"] = (i + 1).ToString(),
                    ["key"] = key.Length > kMaxKeyContext ? key.Substring(0, kMaxKeyContext) + "…" : key,
                    ["source"] = items[i].Source,
                    ["translation"] = items[i].Translation,
                };
                if (items[i].Issues != null && items[i].Issues.Count > 0)
                {
                    o["issues"] = new JArray(items[i].Issues);
                }
                arr.Add(o);
            }
            AddKeepNames(payload, items.Select(i => i.Source));
            payload["items"] = arr;
            return "Review the following JSON items and reply with json:\n" + payload.ToString(Formatting.None);
        }

        /// <returns>key → 评分与改进译文。改进译文已通过占位符校验，不合格的改进会被丢弃（只保留评分）。</returns>
        public async Task<Dictionary<string, ReviewResult>> ReviewAsync(string modName, IList<ReviewItem> items, CancellationToken ct)
        {
            string system = m_ReviewPrompt;
            const int kAttempts = 4;
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string content = await m_Client.CompleteAsync(system, BuildReviewUserPrompt(modName, items), jsonMode: true, ct).ConfigureAwait(false);
                    return ParseReview(content, items, m_Names);
                }
                catch (ApiException ex) when (!ex.Fatal && attempt < kAttempts)
                {
                    Mod.Log.Warn($"Review API error (attempt {attempt}): {ex.Message}");
                    ActivityLog.Retry(attempt, ex.Message);
                    await Task.Delay(Backoff(attempt, ex.StatusCode == 429), ct).ConfigureAwait(false);
                }
                catch (JsonException ex) when (attempt < kAttempts)
                {
                    Mod.Log.Warn($"Malformed review JSON (attempt {attempt}): {ex.Message}");
                }
                catch (JsonException)
                {
                    return new Dictionary<string, ReviewResult>();
                }
            }
        }

        internal static Dictionary<string, ReviewResult> ParseReview(string content, IList<ReviewItem> items, ProtectedNames names = null)
        {
            JObject obj = ExtractObject(content);
            var result = new Dictionary<string, ReviewResult>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                JToken token = obj[(i + 1).ToString()];
                if (token == null)
                {
                    continue;
                }
                int score;
                string fix = null;
                if (token.Type == JTokenType.Object)
                {
                    JToken s = token["s"] ?? token["score"];
                    if (s == null || !int.TryParse(s.ToString(), out score))
                    {
                        continue;
                    }
                    fix = (token["fix"] ?? token["translation"])?.ToString();
                }
                else if (!int.TryParse(token.ToString(), out score))
                {
                    continue;
                }
                score = Math.Max(1, Math.Min(5, score));
                if (string.IsNullOrWhiteSpace(fix) || fix == items[i].Translation || !TextRules.IsValidTranslation(items[i].Source, fix, out _)
                    || (names != null && names.Lost(items[i].Source, fix, out _) && !names.Lost(items[i].Source, items[i].Translation, out _))
                    || QualityChecker.AddsRegion(items[i].Source, fix, items[i].Translation))
                {
                    fix = null;
                }
                result[items[i].Key] = new ReviewResult { Score = score, Fix = fix };
            }
            return result;
        }

        private static JObject ExtractObject(string content)
        {
            string json = content.Trim();
            int start = json.IndexOf('{');
            int end = json.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                throw new JsonReaderException("no JSON object in response");
            }
            return JObject.Parse(json.Substring(start, end - start + 1));
        }

        internal static Dictionary<string, string> ParseResponse(string content, IList<TranslateItem> items)
        {
            string json = content.Trim();
            int start = json.IndexOf('{');
            int end = json.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                throw new JsonReaderException("no JSON object in response");
            }
            json = json.Substring(start, end - start + 1);
            JObject obj = JObject.Parse(json);
            if (obj["translations"] is JObject inner)
            {
                obj = inner;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                JToken token = obj[(i + 1).ToString()];
                if (token == null)
                {
                    continue;
                }
                string value = token.Type == JTokenType.Object ? (token["text"] ?? token["translation"])?.ToString() : token.ToString();
                if (!string.IsNullOrEmpty(value))
                {
                    result[items[i].Key] = value;
                }
            }
            return result;
        }
    }
}
