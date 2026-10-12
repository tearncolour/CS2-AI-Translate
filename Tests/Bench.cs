using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AITranslate.Core;
using Newtonsoft.Json;

/// <summary>
/// 性能基准：用真实的游戏文本和已保存的译文测量后台分析里最重的几步。
/// 用法：BENCH_GAME=&lt;含 en-US.json / zh-HANS.json 的目录&gt; BENCH_STORE=&lt;AITranslateLocalization 目录&gt; AITranslate.Tests.exe
/// </summary>
internal static class Bench
{
    public static void Run(string gameDir, string storeDir)
    {
        var en = Load(Path.Combine(gameDir, "en-US.json"));
        var zh = Load(Path.Combine(gameDir, "zh-HANS.json"));
        Console.WriteLine($"game text: {en.Count} en, {zh.Count} zh");

        // 已保存的译文 + 原文（质量检查的输入）。
        var pairs = new List<(string source, string translation)>();
        if (!string.IsNullOrEmpty(storeDir))
        {
            foreach (string file in Directory.GetFiles(Path.Combine(storeDir, "Localization", "zh-HANS"), "*.json"))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("desc_") || name.StartsWith("fix_"))
                {
                    continue;
                }
                var t = Load(file);
                string srcFile = Path.Combine(storeDir, "Sources", "zh-HANS", name);
                var s = File.Exists(srcFile) ? Load(srcFile) : new Dictionary<string, string>();
                foreach (var kv in t)
                {
                    if (s.TryGetValue(kv.Key, out var src))
                    {
                        pairs.Add((src, kv.Value));
                    }
                }
            }
            Console.WriteLine($"saved translations: {pairs.Count}");
        }

        // 受保护名称：用本机已安装 mod 的显示名（游戏里是全部代码 mod 的名字和设置页标题）。
        var modNames = new List<string> { "Road Builder", "Move It", "Bridge Builder", "Extra Assets Importer", "I18N Everywhere" };
        string modsJson = string.IsNullOrEmpty(storeDir) ? null : Path.Combine(storeDir, "mods.json");
        if (modsJson != null && File.Exists(modsJson))
        {
            modNames.AddRange(Load(modsJson).Values);
        }
        var names = new ProtectedNames(modNames);
        Console.WriteLine($"protected names: {names.Count}");

        Measure("LooksMixed over all game text (CollectMixedNative)", 3, () =>
        {
            int mixed = 0;
            foreach (var kv in en)
            {
                if (zh.TryGetValue(kv.Key, out var cur) && cur != kv.Value && QualityChecker.LooksMixed(kv.Value, cur, "zh-HANS", names, null))
                {
                    mixed++;
                }
            }
            return mixed;
        });

        Measure("IsTranslatable over all game text", 3, () => en.Values.Count(v => TextRules.IsTranslatable(v, "zh-HANS")));

        // 混杂检查每条都要查一次所属分组的修正记录（store.Get → FileNameFor）。
        string[] groupKeys = { "pdx_160539", "game_vanilla", "local_BetaTestDrivingMod", "self_AITranslate", "dlc_MediterraneanHeritage" };
        Measure("FileNameFor x game text count", 3, () =>
        {
            int n = 0;
            for (int i = 0; i < en.Count; i++)
            {
                n += TranslationStore.FileNameFor(TranslationStore.FixGroup(groupKeys[i % groupKeys.Length])).Length;
            }
            return n;
        });

        Glossary glossary = null;
        Measure("Glossary.Build", 3, () =>
        {
            var shortEn = en.Where(kv => kv.Value.Length >= 2 && kv.Value.Length <= 40 && zh.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var shortZh = shortEn.Keys.ToDictionary(k => k, k => zh[k]);
            glossary = Glossary.Build(shortEn, shortZh, "zh-HANS");
            return glossary.Count;
        });

        if (pairs.Count > 0)
        {
            // 首次读取当前语言的全部译文（TranslationStore.GetLocale：译文 + 原文 + 评分）。
            Measure("load all zh-HANS store files (GetLocale)", 3, () =>
            {
                int n = 0;
                foreach (string file in Directory.GetFiles(Path.Combine(storeDir, "Localization", "zh-HANS"), "*.json"))
                {
                    n += Load(file).Count;
                    string src = Path.Combine(storeDir, "Sources", "zh-HANS", Path.GetFileName(file));
                    if (File.Exists(src))
                    {
                        n += Load(src).Count;
                    }
                    string quality = Path.Combine(storeDir, "Quality", "zh-HANS", Path.GetFileName(file));
                    if (File.Exists(quality))
                    {
                        n += JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(quality)).Count;
                    }
                }
                return n;
            });
            SaveCost(storeDir);

            // 质量检查前过滤格式固定的条目（以前在主线程）。
            Measure("IsFixedFormat over saved translations (was on main thread)", 3, () => pairs.Count(p => ModScanner.IsFixedFormat("Assets.NAME[x]", p.source)));

            // 检测后清理旧译文的判断（以前在主线程，正则在前；现在在后台，先比较字符串）。
            Measure("prune check, old order (was on main thread)", 3, () => pairs.Count(p => TextRules.IsMachineIdentifier(p.source) || (!TextRules.IsTranslatable(p.source, "zh-HANS") && p.translation == p.source)));
            Measure("prune check, new order (background)", 3, () => pairs.Count(p => (p.translation == p.source && !TextRules.IsTranslatable(p.source, "zh-HANS")) || TextRules.IsMachineIdentifier(p.source)));
            Measure("QualityChecker.Check over saved translations", 3, () =>
            {
                int suspicious = 0;
                foreach (var (s, t) in pairs)
                {
                    if (QualityChecker.IsSuspicious(QualityChecker.Check(s, t, "zh-HANS", glossary)))
                    {
                        suspicious++;
                    }
                }
                return suspicious;
            });
        }
    }

    /// <summary>翻译任务每批结果都会把整组译文写回磁盘：测一下最大的一组序列化 + 写入要多久。</summary>
    public static void SaveCost(string storeDir)
    {
        string dir = Path.Combine(storeDir, "Localization", "zh-HANS");
        string biggest = Directory.GetFiles(dir, "*.json").OrderByDescending(f => new FileInfo(f).Length).First();
        var t = Load(biggest);
        var s = Load(Path.Combine(storeDir, "Sources", "zh-HANS", Path.GetFileName(biggest)));
        string tmp = Path.Combine(Path.GetTempPath(), "aitranslate_bench.json");
        Measure($"save {Path.GetFileName(biggest)} ({t.Count} entries, translations+sources)", 5, () =>
        {
            File.WriteAllText(tmp, JsonConvert.SerializeObject(new SortedDictionary<string, string>(t, StringComparer.Ordinal), Formatting.Indented));
            File.WriteAllText(tmp, JsonConvert.SerializeObject(new SortedDictionary<string, string>(s, StringComparer.Ordinal), Formatting.Indented));
            return t.Count;
        });
        File.Delete(tmp);
    }

    private static Dictionary<string, string> Load(string path) =>
        JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path)) ?? new Dictionary<string, string>();

    private static void Measure(string name, int runs, Func<int> action)
    {
        action(); // 预热（JIT、正则缓存）
        var times = new List<long>();
        long allocated = 0;
        int result = 0;
        for (int i = 0; i < runs; i++)
        {
            GC.Collect();
            long before = GC.GetTotalMemory(true);
            int gen0 = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            result = action();
            times.Add(sw.ElapsedMilliseconds);
            allocated = GC.CollectionCount(0) - gen0;
            _ = before;
        }
        Console.WriteLine($"{name,-50} {times.Min(),6} ms (median {times.OrderBy(t => t).ElementAt(times.Count / 2)} ms)  gen0 GCs/run {allocated,3}  result {result}");
    }
}
