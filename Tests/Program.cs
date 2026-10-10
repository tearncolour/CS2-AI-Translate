using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AITranslate.Core;
using Newtonsoft.Json;

internal static class Program
{
    private static int s_Failed;

    private static void Check(bool cond, string name)
    {
        Console.WriteLine((cond ? "PASS " : "FAIL ") + name);
        if (!cond)
        {
            s_Failed++;
        }
    }

    private static int Main(string[] args)
    {
        // 导出设置页的英文原文（逻辑键 → 英文），供编写 Locale/Languages/*.json 参考。
        string exportUi = Environment.GetEnvironmentVariable("EXPORT_UI");
        if (!string.IsNullOrEmpty(exportUi))
        {
            var english = new System.Collections.Generic.SortedDictionary<string, string>(StringComparer.Ordinal);
            var chinese = new System.Collections.Generic.SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, zhText, text) in AITranslate.Locale.LocaleTable.AllEntries())
            {
                english[key] = text;
                chinese[key] = zhText;
            }
            File.WriteAllText(exportUi, Newtonsoft.Json.JsonConvert.SerializeObject(english, Newtonsoft.Json.Formatting.Indented), new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.ChangeExtension(exportUi, ".zh.json"), Newtonsoft.Json.JsonConvert.SerializeObject(chinese, Newtonsoft.Json.Formatting.Indented), new System.Text.UTF8Encoding(false));
            Console.WriteLine("exported " + english.Count);
            return 0;
        }

        Console.OutputEncoding = Encoding.UTF8;

        // ---------- TextRules
        Check(TextRules.IsTranslatable("Enable traffic lights", "zh-HANS"), "plain english");
        Check(TextRules.IsTranslatable("Remove {COUNT} trees", "zh-HANS"), "with placeholder");
        Check(!TextRules.IsTranslatable("{0}", "zh-HANS"), "placeholder only");
        Check(!TextRules.IsTranslatable("123 / 456", "zh-HANS"), "numbers only");
        Check(!TextRules.IsTranslatable("https://github.com/foo/bar", "zh-HANS"), "url");
        Check(!TextRules.IsTranslatable("v1.2.3", "zh-HANS"), "version");
        Check(!TextRules.IsTranslatable("Some_Prefab_Name01", "zh-HANS"), "identifier");
        Check(!TextRules.IsTranslatable("广州火车站 Guangzhou Station", "zh-HANS"), "already chinese");
        Check(TextRules.IsTranslatable("广州火车站 Guangzhou Station", "de-DE"), "chinese source for german target");
        Check(TextRules.IsTranslatable("Anarchy", "zh-HANS"), "single word");
        Check(TextRules.IsTranslatable("Cancelled.", "zh-HANS"), "single word with period");
        Check(TextRules.IsTranslatable("Ready!", "zh-HANS"), "single word with exclamation");
        Check(!TextRules.IsTranslatable("config.json", "zh-HANS"), "file name");
        Check(!TextRules.IsTranslatable("Tier2_Building", "zh-HANS"), "identifier with digits");
        Check(!TextRules.IsTranslatable("", "zh-HANS"), "empty");

        // ---------- 任意语言 → 游戏当前语言
        Check(!TextRules.IsTranslatable("Enable traffic lights", "en-US"), "en: english source not translated");
        Check(TextRules.IsTranslatable("广州火车站", "en-US"), "en: chinese source translated");
        Check(TextRules.IsTranslatable("Einstellungen für die Straße", "en-US"), "en: german source translated");
        Check(TextRules.IsTranslatable("Включить светофоры", "en-US"), "en: russian source translated");
        Check(!TextRules.IsTranslatable("广州火车站 Guangzhou Station", "en-US"), "en: bilingual name kept");
        Check(!TextRules.IsTranslatable("Straße auswählen", "de-DE"), "de: german source not translated");
        Check(TextRules.IsTranslatable("Enable traffic lights for the road", "de-DE"), "de: english source translated");
        Check(TextRules.IsTranslatable("Enable", "de-DE"), "de: short english label translated");
        Check(TextRules.IsTranslatable("广州南站2号线", "de-DE"), "de: chinese with digits translated");
        Check(!TextRules.IsTranslatable("Sélectionner la route", "fr-FR"), "fr: french source not translated");
        Check(TextRules.IsTranslatable("Select the road", "fr-FR"), "fr: english source translated");
        Check(TextRules.IsTranslatable("Seleccionar la carretera y añadir semáforos", "fr-FR"), "fr: spanish source translated");
        Check(!TextRules.IsTranslatable("Включить светофоры", "ru-RU"), "ru: russian source not translated");
        Check(TextRules.IsTranslatable("Увімкнути світлофори", "ru-RU"), "ru: ukrainian source translated");
        Check(!TextRules.IsTranslatable("道路を選択", "ja-JP"), "ja: japanese source not translated");
        Check(TextRules.IsTranslatable("选择道路并启用交通灯控制功能", "ja-JP"), "ja: long chinese source translated");
        Check(!TextRules.IsTranslatable("도로 선택", "ko-KR"), "ko: korean source not translated");
        Check(TextRules.IsTranslatable("道路を選択", "ko-KR"), "ko: japanese source translated");
        Check(TextRules.IsTranslatable("這個設定會顯示車輛", "zh-HANS"), "zh-HANS: traditional source converted");
        Check(!TextRules.IsTranslatable("这个设置会显示车辆", "zh-HANS"), "zh-HANS: simplified source kept");
        Check(TextRules.IsTranslatable("这个设置会显示车辆", "zh-HANT"), "zh-HANT: simplified source converted");
        Check(!TextRules.IsTranslatable("這個設定會顯示車輛", "zh-HANT"), "zh-HANT: traditional source kept");
        Check(TextRules.IsTranslatable("道路を選択", "zh-HANS"), "zh: japanese source translated");

        Check(TextRules.PlaceholdersMatch("Remove {COUNT} of {TOTAL}", "移除 {TOTAL} 中的 {COUNT}", out _), "reordered placeholders ok");
        Check(!TextRules.PlaceholdersMatch("Remove {COUNT}", "移除 {数量}", out _), "translated placeholder rejected");
        Check(TextRules.PlaceholdersMatch("<b>Hot</b>key %s", "<b>快捷</b>键 %s", out _), "tags + printf");
        Check(!TextRules.PlaceholdersMatch("<b>Bold</b>", "加粗", out _), "lost tags rejected");
        Check(!TextRules.IsValidTranslation("Line", "一\n二\n三\n四", out _), "line explosion rejected");

        // ---------- Glossary
        var en = new Dictionary<string, string> { ["a"] = "Roundabout", ["b"] = "Roundabout", ["c"] = "Zoning", ["d"] = "Zoning", ["e"] = "Traffic Lights", ["f"] = "Something with {0}", ["g"] = "Lane" };
        var zh = new Dictionary<string, string> { ["a"] = "环岛", ["b"] = "环岛", ["c"] = "分区", ["d"] = "分区", ["e"] = "红绿灯", ["f"] = "x", ["g"] = "车道" };
        var g = Glossary.Build(en, zh, "zh-HANS");
        var m = g.Match(new[] { "Add traffic lights to every roundabout", "Zoning tools" }, 10);
        Check(m.Any(t => t.Target == "红绿灯") && m.Any(t => t.Target == "环岛") && m.Any(t => t.Target == "分区"), "glossary matches: " + string.Join(", ", m.Select(t => t.English + "=" + t.Target)));
        Check(!m.Any(t => t.English == "Lane"), "single-occurrence single word excluded");

        // ---------- ParseResponse
        var items = new List<TranslateItem> { new TranslateItem { Key = "k1", Source = "Hello" }, new TranslateItem { Key = "k2", Source = "World" } };
        var parsed = Translator.ParseResponse("```json\n{\"1\":\"你好\",\"2\":\"世界\"}\n```", items);
        Check(parsed["k1"] == "你好" && parsed["k2"] == "世界", "parse fenced json");
        parsed = Translator.ParseResponse("{\"translations\":{\"1\":\"你好\"}}", items);
        Check(parsed.Count == 1 && parsed["k1"] == "你好", "parse wrapped json");

        // ---------- 资产内部名
        Check(ModScanner.HumanizePrefabName("CSBankTowerTall_01") == "CS Bank Tower Tall 01", "humanize: " + ModScanner.HumanizePrefabName("CSBankTowerTall_01"));
        Check(ModScanner.HumanizePrefabName("EU-Bus_Stop02") == "EU Bus Stop 02", "humanize: " + ModScanner.HumanizePrefabName("EU-Bus_Stop02"));
        Check(TextRules.IsTranslatable(ModScanner.HumanizePrefabName("ParkingHall0203"), "zh-HANS"), "humanized name translatable");
        // 修正混杂：mod 自己的名字、标签、路径不算照搬；只是换说法的改写不算改进
        Check(!QualityChecker.LooksMixed("Enter lane connection mode", "[Traffic] 进入车道连接模式", "zh-HANS", null, "Traffic"), "mixed: bracket tag is fine");
        Check(!QualityChecker.LooksMixed("If items placed with Anarchy disappear", "如果使用 Anarchy 放置的摆件消失", "zh-HANS", null, "Anarchy"), "mixed: own mod name is fine");
        Check(!QualityChecker.LooksMixed("Clears %LOCALAPPDATA%/CSLModernMap after check", "校验后清空本机的 %LOCALAPPDATA%/CSLModernMap", "zh-HANS", null, "CSLModernMap"), "mixed: path is fine");
        Check(QualityChecker.LooksMixed("Unlock roads in every Playset", "解锁所有 Playset 中的道路", "zh-HANS", null, "Road Builder [BETA]"), "mixed: real leftover word");
        Check(QualityChecker.LeftoverEnglish("Unlock roads in every Playset", "解锁所有游玩集中的道路", "zh-HANS", null, "Road Builder [BETA]")
            < QualityChecker.LeftoverEnglish("Unlock roads in every Playset", "解锁所有 Playset 中的道路", "zh-HANS", null, "Road Builder [BETA]"), "mixed: real fix improves");
        Check(QualityChecker.LeftoverEnglish("Mediterranean Heritage Residential", "Mediterranean Heritage中密度住宅", "zh-HANS") > 0, "mixed: classic case still caught");

        // 预置界面语言：每个语言文件都要有全部条目，占位符和换行与英文一致，没有多余的键
        {
            var english = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, _, text) in AITranslate.Locale.LocaleTable.AllEntries())
            {
                english[key] = text;
            }
            int Count(string s, string part) { int n = 0, i = 0; while ((i = s.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { n++; i += part.Length; } return n; }
            foreach (string locale in AITranslate.Locale.LocaleTable.BundledLocales)
            {
                if (locale == "en-US" || locale == "zh-HANS")
                {
                    continue;
                }
                var file = AITranslate.Locale.LocaleTable.LoadLanguageFile(locale);
                Check(file != null, "lang file embedded: " + locale);
                if (file == null)
                {
                    continue;
                }
                var missing = english.Keys.Where(k => !file.TryGetValue(k, out var t) || string.IsNullOrWhiteSpace(t)).ToList();
                var extra = file.Keys.Where(k => !english.ContainsKey(k)).ToList();
                var badPlaceholders = english.Where(p => file.TryGetValue(p.Key, out var t) && t != null
                    && (!TextRules.PlaceholdersMatch(p.Value, t, out _) || Count(p.Value, "\n") != Count(t, "\n"))).Select(p => p.Key).ToList();
                Check(missing.Count == 0, $"lang {locale}: all keys present" + (missing.Count > 0 ? " missing " + string.Join(",", missing.Take(5)) : ""));
                Check(extra.Count == 0, $"lang {locale}: no unknown keys" + (extra.Count > 0 ? " extra " + string.Join(",", extra.Take(5)) : ""));
                Check(badPlaceholders.Count == 0, $"lang {locale}: placeholders and line breaks" + (badPlaceholders.Count > 0 ? " bad " + string.Join(",", badPlaceholders.Take(5)) : ""));
            }
        }

        // 界面文字跟随游戏语言：已经写进日志、概况的文字，切换语言后按新语言重新显示
        {
            string lang = "en";
            LocText.Resolver = (n, a) => lang == "en" ? "Done " + a[0] : "完成 " + a[0];
            ActivityLog.Clear();
            ActivityLog.Info(LocText.Of("Done", 3));
            string before = ActivityLog.Render(false, "", null);
            lang = "zh";
            LocText.Invalidate();
            string after = ActivityLog.Render(false, "", null);
            Check(before.EndsWith("Done 3") && after.EndsWith("完成 3"), "log follows language: " + after);
            Check(LocText.Concat(LocText.Of("Done", 1), LocText.Literal("!")).ToString() == "完成 1!", "loc text concat");
            ActivityLog.Clear();
        }

        // 设置页标题：译名 (原名)
        Check(ModScanner.ComposeModTitle("推土机增强", "Better Bulldozer") == "推土机增强 (Better Bulldozer)", "mod title compose");
        Check(ModScanner.ComposeModTitle("移动工具（Move It）", "Move It") == "移动工具 (Move It)", "mod title strips echoed original: " + ModScanner.ComposeModTitle("移动工具（Move It）", "Move It"));
        Check(ModScanner.ComposeModTitle("Anarchy", "Anarchy") == "Anarchy", "mod title brand unchanged");
        Check(ModScanner.IsModTitleFormat("Move It", "移动工具 (Move It)") && ModScanner.IsModTitleFormat("Anarchy", "Anarchy") && !ModScanner.IsModTitleFormat("Move It", "移动工具"), "mod title format");
        Check(ModScanner.IsFixedFormat("Options.SECTION[MoveIt.MoveIt.Mod]", "Move It"), "mod title not reviewed");

        // ---------- 启发式质量检查
        Check(QualityChecker.Check("Click to align road edges", "点击对齐道路边缘", "zh-HANS", g).Count == 0, "quality: good zh");
        Check(QualityChecker.Check("Click to align road edges", "点击 align 道路边缘", "zh-HANS", g).Contains(QualityChecker.kLeftover), "quality: leftover english");
        Check(QualityChecker.Check("Click to align road edges", "Click to align road edges", "zh-HANS", g).Contains(QualityChecker.kUntranslated), "quality: untranslated");
        Check(QualityChecker.Check("Hold Ctrl to snap", "按住 Ctrl 吸附", "zh-HANS", g).Count == 0, "quality: key name allowed");
        Check(QualityChecker.Check("Add traffic lights to the roundabout", "给环形交叉口加交通信号", "zh-HANS", g).Any(i => i.StartsWith(QualityChecker.kGlossary)), "quality: glossary mismatch");
        Check(QualityChecker.Check("Remove {COUNT} trees", "移除树木", "zh-HANS", g).Contains(QualityChecker.kPlaceholder), "quality: placeholder lost");
        Check(QualityChecker.Check("Select the road and confirm the placement", "Straße", "de-DE", g).Contains(QualityChecker.kLength), "quality: too short");

        // ---------- 审校结果解析
        var reviewItems = new List<Translator.ReviewItem>
        {
            new Translator.ReviewItem { Key = "a", Source = "Remove {COUNT} trees", Translation = "移除树木 {COUNT}" },
            new Translator.ReviewItem { Key = "b", Source = "Remove {COUNT} trees", Translation = "移除 {COUNT} 棵树" },
            new Translator.ReviewItem { Key = "c", Source = "Remove {COUNT} trees", Translation = "移除树" },
        };
        var reviews = Translator.ParseReview("{\"1\":{\"s\":2,\"fix\":\"移除 {COUNT} 棵树\"},\"2\":{\"s\":5},\"3\":{\"s\":1,\"fix\":\"移除全部树木\"}}", reviewItems);
        Check(reviews["a"].Score == 2 && reviews["a"].Fix == "移除 {COUNT} 棵树", "review: fix accepted");
        Check(reviews["b"].Score == 5 && reviews["b"].Fix == null, "review: good kept");
        Check(reviews["c"].Fix == null, "review: fix without placeholder rejected");

        // ---------- 受保护的 mod 名
        var pnames = new ProtectedNames(new[] { "Bridge Builder", "Find It", "Road Builder [BETA]", "[G87] Road Markings (UK NA FR AR)", "Traffic", "ExtraLib", "Modular Canton Metro Pack | 模块化广州地铁包" });
        Check(pnames.Lost("移除所有 Bridge Builder 桥梁", "移除“桥梁建造者”的全部桥梁", out _), "names: translated mod name detected");
        Check(!pnames.Lost("移除所有 Bridge Builder 桥梁", "移除所有 Bridge Builder 桥梁", out _), "names: kept ok");
        Check(pnames.FindIn("Open Road Builder").Contains("Road Builder"), "names: trailing tag stripped");
        Check(!pnames.FindIn("G87 Road Markings Stripes").Any(), "names: leading tag kept, generic phrase not protected");
        Check(!pnames.FindIn("Traffic lights").Any(), "names: generic single word not protected");
        Check(pnames.FindIn("Requires ExtraLib").Contains("ExtraLib"), "names: camel-case single word");
        Check(pnames.FindIn("Modular Canton Metro Pack assets").Any(), "names: multilingual name split");
        Check(!pnames.FindIn("Finding items").Any(), "names: word boundary");
        Check(!QualityChecker.LooksMixed("Remove all Bridge Builder bridges", "移除所有 Bridge Builder 桥梁", "zh-HANS", pnames), "names: mod name not counted as mixed");

        Check(ModScanner.IsGeneratedName("CN WallDecor SINOPEC XL Decal", "CN Wall Decor SINOPEC XL Decal"), "generated: humanized name");
        Check(!ModScanner.IsGeneratedName("CN WallDecor SINOPEC XL Decal", "SINOPEC XL"), "generated: real name");
        Check(ModScanner.IsGeneratedName("CN_ResidentialLow03_L2_2x4", "CN Residential Low 03 L 2 2 x 4"), "generated: legacy split");
        // ---------- 创作者包与审校防护
        Check(Parsed("CCP5_ResidentialMediumLeft01_L5_3x2") == "[CCP5 Residential Medium Left] 01 - L5 3x2", "parse: creator pack " + Parsed("CCP5_ResidentialMediumLeft01_L5_3x2"));
        Check(QualityChecker.AddsRegion("High School", "东欧中学", "高中"), "region: invented from mod name");
        Check(!QualityChecker.AddsRegion("EE High School", "东欧中学", "高中"), "region: justified by code");
        Check(!QualityChecker.AddsRegion("Chinese Fire Station", "中式消防局", "消防站"), "region: justified by word");
        Check(QualityChecker.AddsRegion("Fire Station", "中式消防局", "消防站"), "region: invented chinese style");
        Check(!QualityChecker.AddsRegion("Fire Station", "消防局", "消防站"), "region: plain rewrite ok");

        // ---------- 中英混杂
        Check(QualityChecker.LooksMixed("Mediterranean Heritage Medium Density Housing", "Mediterranean Heritage中密度住宅", "zh-HANS"), "mixed: dlc name");
        Check(QualityChecker.LooksMixed("Convert legacy water sources to the latest version of the WaterSystem.", "将旧版本水源更新为至最新版本的WaterSystem。", "zh-HANS"), "mixed: system name");
        Check(!QualityChecker.LooksMixed("Opens the UI", "打开 UI", "zh-HANS"), "mixed: acronym ok");
        Check(!QualityChecker.LooksMixed("Low Density Housing", "低密度住宅", "zh-HANS"), "mixed: clean");
        Check(!QualityChecker.LooksMixed("Low Density Housing", "Low Density Housing", "zh-HANS"), "mixed: fully untranslated is not mixed");
        Check(!QualityChecker.LooksMixed("Low Density Housing", "Low Density Housing", "de-DE"), "mixed: latin target ignored");

        // ---------- 通用内部名解析
        string Parsed(string n) { var p = ModScanner.MatchBuildingTemplate(n); return p == null ? null : "[" + p.Value.phrase + "]" + p.Value.suffix; }
        Check(Parsed("CN_BonusResidentialHigh01a_L5_3x2") == "[CN Residential High] 01a - L5 3x2", "parse: bonus dropped " + Parsed("CN_BonusResidentialHigh01a_L5_3x2"));
        Check(Parsed("EE_ResidentialMedium01CornerL3x2x2Details") == "[EE Residential Medium Corner] 01 - L3 2x2", "parse: east europe " + Parsed("EE_ResidentialMedium01CornerL3x2x2Details"));
        Check(Parsed("CN_FinancialCenter Req01") == "[CN Financial Center]", "parse: req " + Parsed("CN_FinancialCenter Req01"));
        Check(Parsed("JP_CommercialStorefront01_1x1 (Styx's)") == "[JP Commercial Storefront] 01 1x1 (Styx's)", "parse: author tag " + Parsed("JP_CommercialStorefront01_1x1 (Styx's)"));
        Check(Parsed("EU_MixedLeft01_L1_2x2") == "[EU Mixed Left] 01 - L1 2x2", "parse: vanilla left");
        Check(Parsed("USNE_OfficeHighB01_L2_2x2") == "[USNE Office High] B01 - L2 2x2", "parse: upper variant " + Parsed("USNE_OfficeHighB01_L2_2x2"));
        Check(Parsed("SneakyParkingLot01_2x3") == null, "parse: no region prefix");
        Check(ModScanner.TemplateForEntry("Assets.NAME[CN_FinancialCenter Req01]", "CN_FinancialCenter Req01") != null, "parse: raw internal title");
        Check(ModScanner.TemplateForEntry("Assets.NAME[CN_ResidentialLow03_L2_2x4]", "CN Residential Low 03 L 2 2 x 4") != null, "parse: legacy source");
        Check(ModScanner.TemplateForEntry("Assets.NAME[CN_ResidentialLow03_L2_2x4]", "Chinese Courtyard House") == null, "parse: author name kept");
        Check(QualityChecker.Check("CN Residential Low 04B L3 2x4", "中国低层住宅 04B 3级 2×4", "zh-HANS", Glossary.Empty).Contains(QualityChecker.kFormat), "quality: level/lot code lost");
        Check(!QualityChecker.Check("CN Residential Low 04 - L3 2x4", "中国低层住宅 04 - L3 2x4", "zh-HANS", Glossary.Empty).Contains(QualityChecker.kFormat), "quality: codes kept ok");
        Check(QualityChecker.Check("CN Residential Low 04 - L3 2x4", "中国低层住宅 04 - L3 2×4", "zh-HANS", Glossary.Empty).Contains(QualityChecker.kFormat), "quality: lot code changed");

        // ---------- 区域建筑命名模板
        var bt = ModScanner.MatchBuildingTemplate("CN_ResidentialHigh01a_L1_6x6");
        Check(bt != null && bt.Value.phrase == "CN Residential High" && bt.Value.suffix == " 01a - L1 6x6", "building: template split");
        Check(ModScanner.HumanizePrefabName("EU_CommercialLow02_L3_2x2") == "EU Commercial Low 02 - L3 2x2", "building: official style source");
        Check(ModScanner.HumanizePrefabName("USNE_ResidentialMedium_L3_4x3") == "USNE Residential Medium - L3 4x3", "building: no number");
        Check(ModScanner.MatchBuildingTemplate("Chinese Courtyard House") == null, "building: plain name not parsed");
        var forced = Glossary.Empty.WithForced(new Dictionary<string, string> { ["Residential Low"] = "低层住宅" });
        Check(forced.Match(new[] { "CN Residential Low" }, 5).Any(t => t.Target == "低层住宅"), "glossary: forced official term");

        // ---------- 内部 ID
        Check(TextRules.IsMachineIdentifier("r6f3675cf-6970-4873-8230-f189dc3e3ae8-76561199478478565"), "id: guid + steam id");
        Check(TextRules.IsMachineIdentifier("r 6 f 3675 cf 6970 4873 8230 f 189 dc 3 e 3 ae 8 76561199478478565"), "id: humanized hash");
        Check(!TextRules.IsTranslatable("r 6 f 3675 cf 6970 4873 8230 f 189 dc 3 e 3 ae 8 76561199478478565", "zh-HANS"), "id: not translatable");
        Check(!TextRules.IsMachineIdentifier("CS Bank Tower Tall 01"), "id: normal name");
        Check(TextRules.IsTranslatable("Day (07:00-19:00)", "zh-HANS"), "id: time range still translatable");
        Check(TextRules.IsTranslatable("15 s", "zh-HANS"), "id: unit still translatable");

        // ---------- 模糊搜索
        string[] Plt(params string[] extra) => new[] { "Parking Lot Tool", "停车场工具", "161156" }.Concat(extra).ToArray();
        Check(FuzzyMatcher.Score("parking", Plt()) > 0, "fuzzy: prefix");
        Check(FuzzyMatcher.Score("lot", Plt()) > 0, "fuzzy: word start");
        Check(FuzzyMatcher.Score("plt", Plt()) > 0, "fuzzy: initials");
        Check(FuzzyMatcher.Score("parkng", Plt()) > 0, "fuzzy: typo");
        Check(FuzzyMatcher.Score("prklot", Plt()) > 0, "fuzzy: subsequence");
        Check(FuzzyMatcher.Score("停车", Plt()) > 0, "fuzzy: chinese translated name");
        Check(FuzzyMatcher.Score("161156", Plt()) > 0, "fuzzy: pdx id");
        Check(FuzzyMatcher.Score("parking tool", Plt()) > 0, "fuzzy: multiple terms");
        Check(FuzzyMatcher.Score("bridge", Plt()) == 0, "fuzzy: no match");
        Check(FuzzyMatcher.Score("roadbuilder", "Road Builder [BETA]") > 0, "fuzzy: ignore spaces");
        Check(FuzzyMatcher.Score("move", "Move It") > FuzzyMatcher.Score("move", "Better Bulldozer Remover"), "fuzzy: better match ranks higher");
        Check(FuzzyMatcher.Score("", Plt()) > 0, "fuzzy: empty query matches all");

        // ---------- 日志缓冲
        ActivityLog.Clear();
        ActivityLog.Info("first");
        ActivityLog.Detail("sample");
        ActivityLog.Success("second");
        string withDetails = ActivityLog.Render(true, "empty", l => l == ActivityLog.Level.Success ? "[ok] " : "");
        string withoutDetails = ActivityLog.Render(false, "empty", l => "");
        Check(withDetails.IndexOf("second") < withDetails.IndexOf("first") && withDetails.Contains("[ok] second"), "log: newest first with prefix");
        Check(withDetails.Contains("sample") && !withoutDetails.Contains("sample"), "log: details toggle");
        for (int i = 0; i < 200; i++)
        {
            ActivityLog.Info("line " + i);
        }
        Check(ActivityLog.Render(true, "", l => "").Split('\n').Length <= 80, "log: capped");
        ActivityLog.Clear();
        Check(ActivityLog.Render(true, "empty", l => "") == "empty", "log: empty text");

        // ---------- Endpoint normalization
        Check(ChatClient.NormalizeEndpoint("https://api.deepseek.com") == "https://api.deepseek.com/chat/completions", "endpoint base");
        Check(ChatClient.NormalizeEndpoint("api.deepseek.com/v1/") == "https://api.deepseek.com/v1/chat/completions", "endpoint v1");
        Check(ChatClient.NormalizeEndpoint("https://x.com/v1/chat/completions") == "https://x.com/v1/chat/completions", "endpoint full");

        // ---------- 真实文本在指定目标语言下被判为「需要翻译」的条目
        string regionRoot = Environment.GetEnvironmentVariable("REGION_ROOT");
        if (!string.IsNullOrEmpty(regionRoot))
        {
            int flagged = 0;
            foreach (string q in Directory.GetFiles(Path.Combine(regionRoot, "Quality", "zh-HANS"), "*.json"))
            {
                string name = Path.GetFileName(q);
                var scores = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(q));
                var tr = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(Path.Combine(regionRoot, "Localization", "zh-HANS", name)));
                var so = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(Path.Combine(regionRoot, "Sources", "zh-HANS", name)));
                foreach (var k in scores.Keys)
                {
                    if (tr.TryGetValue(k, out var t) && so.TryGetValue(k, out var s) && QualityChecker.AddsRegion(s, t))
                    {
                        flagged++;
                        if (flagged <= 15) Console.WriteLine($"  {s.Substring(0, Math.Min(50, s.Length))}  →  {t}");
                    }
                }
            }
            Console.WriteLine($"region-invented reviewed entries: {flagged}");
            return 0;
        }

        string dupRoot = Environment.GetEnvironmentVariable("DUP_ROOT");
        if (!string.IsNullOrEmpty(dupRoot))
        {
            var groups = new Dictionary<string, Dictionary<string, string>>();
            foreach (string file in Directory.GetFiles(Path.Combine(dupRoot, "Sources", "zh-HANS"), "*.json"))
            {
                string gk = Path.GetFileNameWithoutExtension(file);
                if (gk.StartsWith("desc_") || gk.StartsWith("fix_")) continue;
                groups[gk] = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file));
            }
            string Prefab(string k) => k.StartsWith("Assets.NAME[") && k.EndsWith("]") ? k.Substring(12, k.Length - 13) : null;
            var real = new HashSet<string>(groups.SelectMany(x => x.Value).Where(kv => Prefab(kv.Key) is string p && !ModScanner.IsGeneratedName(p, kv.Value)).Select(kv => kv.Key));
            int removed = 0;
            foreach (var grp in groups)
            {
                var dups = grp.Value.Where(kv => real.Contains(kv.Key) && Prefab(kv.Key) is string p && ModScanner.IsGeneratedName(p, kv.Value)).ToList();
                if (dups.Count == 0) continue;
                removed += dups.Count;
                Console.WriteLine($"{grp.Key}: {dups.Count}  e.g. {dups[0].Key} = {dups[0].Value}");
            }
            Console.WriteLine($"would remove {removed}");
            return 0;
        }

        string namesRoot = Environment.GetEnvironmentVariable("NAMES_ROOT");
        if (!string.IsNullOrEmpty(namesRoot))
        {
            var raw = new List<string>();
            var cache = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("PDX_CACHE")));
            raw.AddRange(cache["items"].Where(i => (i["tags"] as Newtonsoft.Json.Linq.JArray)?.Any(t => t["id"]?.ToString() == "Code Mod") == true).Select(i => i["displayName"]?.ToString()));
            var pn = new ProtectedNames(raw);
            Console.WriteLine($"protected {pn.Count} names");
            int lost = 0, total = 0;
            foreach (string file in Directory.GetFiles(Path.Combine(namesRoot, "Localization", "zh-HANS"), "*.json").Where(f => !Path.GetFileName(f).StartsWith("desc_")))
            {
                string src = Path.Combine(namesRoot, "Sources", "zh-HANS", Path.GetFileName(file));
                if (!File.Exists(src)) continue;
                var tr = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file));
                var so = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(src));
                foreach (var kv in tr)
                {
                    if (!so.TryGetValue(kv.Key, out var s)) continue;
                    total++;
                    if (pn.Lost(s, kv.Value, out var name))
                    {
                        lost++;
                        if (lost <= 25) Console.WriteLine($"  [{name}] {s.Replace("\n", " ").Substring(0, Math.Min(60, s.Length))}  →  {kv.Value.Replace("\n", " ").Substring(0, Math.Min(50, kv.Value.Length))}");
                    }
                }
            }
            Console.WriteLine($"lost names in {lost}/{total}");
            return 0;
        }

        string mixedDir = Environment.GetEnvironmentVariable("MIXED_DIR");
        if (!string.IsNullOrEmpty(mixedDir))
        {
            int total = 0, hits = 0;
            var samples = new List<string>();
            foreach (string dir in Directory.GetDirectories(mixedDir))
            {
                string enPath = Path.Combine(dir, "en-US.json"), zhPath = Path.Combine(dir, "zh-HANS.json");
                if (!File.Exists(enPath) || !File.Exists(zhPath)) continue;
                var mEn = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(enPath));
                var mZh = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(zhPath));
                foreach (var kv in mZh)
                {
                    if (!mEn.TryGetValue(kv.Key, out var src)) continue;
                    total++;
                    if (QualityChecker.LooksMixed(src, kv.Value, "zh-HANS"))
                    {
                        hits++;
                        samples.Add($"{Path.GetFileName(dir)}  {src.Replace("\n", " ")}  →  {kv.Value.Replace("\n", " ")}");
                    }
                }
            }
            Console.WriteLine($"mixed {hits}/{total}");
            var rnd = new Random(1);
            foreach (var s in samples.OrderBy(_ => rnd.Next()).Take(45)) Console.WriteLine("  " + (s.Length > 150 ? s.Substring(0, 150) : s));
            return 0;
        }

        string parseRoot = Environment.GetEnvironmentVariable("PARSE_ROOT");
        if (!string.IsNullOrEmpty(parseRoot))
        {
            var seen = new HashSet<string>();
            var phrases = new Dictionary<string, int>();
            int matched = 0, total = 0;
            foreach (string file in Directory.GetFiles(Path.Combine(parseRoot, "Sources", "zh-HANS"), "*.json").Where(f => !Path.GetFileName(f).StartsWith("desc_")))
            {
                foreach (var kv in JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file)))
                {
                    if (!kv.Key.StartsWith("Assets.NAME[") || !seen.Add(kv.Key))
                    {
                        continue;
                    }
                    total++;
                    var t = ModScanner.TemplateForEntry(kv.Key, kv.Value);
                    if (t == null)
                    {
                        continue;
                    }
                    matched++;
                    phrases.TryGetValue(t.Value.phrase, out int c);
                    phrases[t.Value.phrase] = c + 1;
                    if (matched % 60 == 1)
                    {
                        Console.WriteLine($"  {kv.Key.Substring(12, kv.Key.Length - 13)}  =>  [{t.Value.phrase}]{t.Value.suffix}");
                    }
                }
            }
            Console.WriteLine($"matched {matched}/{total} asset names into {phrases.Count} phrases:");
            Console.WriteLine("  " + string.Join(" | ", phrases.OrderByDescending(p => p.Value).Select(p => p.Key + " ×" + p.Value)));
            return 0;
        }

        string vanilla = Environment.GetEnvironmentVariable("VANILLA_DIR");
        if (!string.IsNullOrEmpty(vanilla))
        {
            var vEn = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(Path.Combine(vanilla, "en-US.json")));
            var vZh = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(Path.Combine(vanilla, "zh-HANS.json")));
            var phrases = new Dictionary<string, string>();
            var types = new Dictionary<string, string>();
            var regions = new Dictionary<string, string>();
            ModScanner.ExtractOfficialBuildingTerms(vEn, k => vZh.TryGetValue(k, out var v) ? v : null, phrases, types, regions);
            Console.WriteLine($"phrases {phrases.Count}, types {types.Count}, regions {regions.Count}");
            foreach (var kv in types)
            {
                Console.WriteLine($"  type  {kv.Key} => {kv.Value}");
            }
            foreach (var kv in regions)
            {
                Console.WriteLine($"  region {kv.Key} => {kv.Value}");
            }
            foreach (string n in new[] { "CN_ResidentialLow03_L2_2x4", "CN_ResidentialHigh01a_L1_6x6", "USNE_ResidentialMedium_L3_4x3", "CN_BonusOfficeHigh01a_L2_2x2" })
            {
                var t = ModScanner.MatchBuildingTemplate(n);
                Console.WriteLine($"  {n} => {(t == null ? "(no match) " + ModScanner.HumanizePrefabName(n) : t.Value.phrase + " |" + t.Value.suffix)}");
            }
            return 0;
        }

        string pruneRoot = Environment.GetEnvironmentVariable("PRUNE_ROOT");
        if (!string.IsNullOrEmpty(pruneRoot))
        {
            int total = 0;
            var pruned = new List<string>();
            foreach (string file in Directory.GetFiles(Path.Combine(pruneRoot, "Sources", "zh-HANS"), "*.json").Where(f => !Path.GetFileName(f).StartsWith("desc_")))
            {
                string tr = Path.Combine(pruneRoot, "Localization", "zh-HANS", Path.GetFileName(file));
                var so = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file));
                var tt = File.Exists(tr) ? JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(tr)) : new Dictionary<string, string>();
                foreach (var kv in so)
                {
                    total++;
                    tt.TryGetValue(kv.Key, out var cur);
                    if (TextRules.IsMachineIdentifier(kv.Value) || (!TextRules.IsTranslatable(kv.Value, "zh-HANS") && cur == kv.Value))
                    {
                        tt.TryGetValue(kv.Key, out var t);
                        pruned.Add($"{kv.Value}  →  {t}");
                    }
                }
            }
            Console.WriteLine($"Would prune {pruned.Count} of {total}:");
            foreach (var p in pruned)
            {
                Console.WriteLine("   " + p);
            }
            return 0;
        }

        string qualityRoot = Environment.GetEnvironmentVariable("QUALITY_ROOT");
        if (!string.IsNullOrEmpty(qualityRoot))
        {
            var counts = new Dictionary<string, int>();
            var samples = new Dictionary<string, List<string>>();
            int total = 0, suspicious = 0;
            foreach (string file in Directory.GetFiles(Path.Combine(qualityRoot, "Localization", "zh-HANS"), "*.json").Where(f => !Path.GetFileName(f).StartsWith("desc_")))
            {
                string src = Path.Combine(qualityRoot, "Sources", "zh-HANS", Path.GetFileName(file));
                if (!File.Exists(src))
                {
                    continue;
                }
                var tr = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file));
                var so = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(src));
                foreach (var kv in tr)
                {
                    if (!so.TryGetValue(kv.Key, out var s))
                    {
                        continue;
                    }
                    total++;
                    var issues = QualityChecker.Check(s, kv.Value, "zh-HANS", Glossary.Empty);
                    if (QualityChecker.IsSuspicious(issues))
                    {
                        suspicious++;
                    }
                    foreach (var i in issues)
                    {
                        counts.TryGetValue(i, out int n);
                        counts[i] = n + 1;
                        if (!samples.TryGetValue(i, out var list))
                        {
                            samples[i] = list = new List<string>();
                        }
                        if (list.Count < 8)
                        {
                            list.Add($"{s.Replace("\n", "\\n")}  →  {kv.Value.Replace("\n", "\\n")}");
                        }
                    }
                }
            }
            Console.WriteLine($"Quality: {total} entries, {suspicious} suspicious");
            foreach (var kv in counts.OrderByDescending(p => p.Value))
            {
                Console.WriteLine($"  {kv.Key}: {kv.Value}");
                foreach (var s in samples[kv.Key])
                {
                    Console.WriteLine("      " + (s.Length > 150 ? s.Substring(0, 150) : s));
                }
            }
            return 0;
        }

        string probe = Environment.GetEnvironmentVariable("PROBE_LOCALE");
        string probeText = Environment.GetEnvironmentVariable("PROBE_TEXT");
        if (!string.IsNullOrEmpty(probe) && !string.IsNullOrEmpty(probeText))
        {
            Console.WriteLine($"{probeText} → inTarget={LanguageDetector.IsInTargetLanguage(probeText, probe)} translatable={TextRules.IsTranslatable(probeText, probe)}");
            return 0;
        }
        if (!string.IsNullOrEmpty(probe))
        {
            var hits = new List<string>();
            int count = 0;
            foreach (string file in args)
            {
                foreach (var kv in JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file)))
                {
                    count++;
                    if (TextRules.IsTranslatable(kv.Value, probe))
                    {
                        hits.Add(kv.Value);
                    }
                }
            }
            Console.WriteLine($"Probe {probe}: {hits.Count}/{count} translatable");
            foreach (var s in hits.Distinct().Take(30))
            {
                Console.WriteLine("   + " + s.Replace("\n", "\\n").Substring(0, Math.Min(100, s.Length)));
            }
            return 0;
        }

        // ---------- Real mod texts
        if (args.Length > 0)
        {
            int total = 0, translatable = 0;
            var skipped = new List<string>();
            foreach (string file in args)
            {
                var d = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(file));
                foreach (var kv in d)
                {
                    total++;
                    if (TextRules.IsTranslatable(kv.Value, "zh-HANS"))
                    {
                        translatable++;
                    }
                    else
                    {
                        skipped.Add(kv.Value);
                    }
                }
            }
            Console.WriteLine($"Real data: {total} entries, {translatable} translatable, {skipped.Count} skipped. Skipped samples:");
            foreach (var s in skipped.Distinct().Take(40))
            {
                Console.WriteLine("   - " + s.Replace("\n", "\\n"));
            }
        }

        // ---------- Live HTTP path with an invalid key (expects 401)
        if (Environment.GetEnvironmentVariable("RUN_HTTP") == "1")
        {
            var client = new ChatClient("https://api.deepseek.com", "sk-invalid-test-key", "deepseek-chat", 0.7f);
            try
            {
                client.CompleteAsync("sys", "Reply with json {}", true, CancellationToken.None).GetAwaiter().GetResult();
                Check(false, "http should fail with invalid key");
            }
            catch (ApiException ex)
            {
                Console.WriteLine("   " + ex.Message);
                Check(ex.StatusCode == 401 && ex.Fatal, "http 401 is fatal");
            }
        }

        Console.WriteLine(s_Failed == 0 ? "ALL PASSED" : s_Failed + " FAILED");
        return s_Failed == 0 ? 0 : 1;
    }
}
