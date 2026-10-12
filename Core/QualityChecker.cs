using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AITranslate.Core
{
    /// <summary>
    /// 不调用 API 的快速质量检查：找出明显有问题的译文，供 AI 审校优先处理。
    /// </summary>
    internal static class QualityChecker
    {
        public const string kUntranslated = "untranslated";
        public const string kLeftover = "leftover-source-language";
        public const string kGlossary = "glossary";
        public const string kLength = "length";
        public const string kPlaceholder = "placeholder";
        public const string kFormat = "format";

        // 给 AI 的提示：译文照搬了原文的英文单词，品牌/人名/技术名词可保留，其余应翻译。
        public const string kMixed = "mixed-language: some English words were copied untranslated; translate them unless they are brand, product, person or technical names";

        // 资产名里的等级（L3）和占地（2x4）代码，译文必须原样保留。
        private static readonly Regex s_Codes = new Regex(@"(?<![A-Za-z0-9])(?:L[1-5]|\d+x\d+)(?![A-Za-z0-9])", RegexOptions.Compiled);

        private static readonly Regex s_Strip = new Regex(
            @"\{[^{}\r\n]{0,80}\}|%(?:\d+\$)?[-+0#]*\d*(?:\.\d+)?[sdfixXc]|<\/?[A-Za-z][^<>\r\n]{0,80}>|https?://\S+",
            RegexOptions.Compiled);

        // 全小写的英文单词（品牌、缩写、按键名通常带大写，不在此列）。
        private static readonly Regex s_LowerWord = new Regex(@"(?<![A-Za-z0-9_])[a-z]{3,}(?![A-Za-z0-9_])", RegexOptions.Compiled);

        public static List<string> Check(string source, string translation, string locale, Glossary glossary)
        {
            var issues = new List<string>();
            if (string.IsNullOrWhiteSpace(translation))
            {
                issues.Add(kUntranslated);
                return issues;
            }
            foreach (Match code in s_Codes.Matches(source))
            {
                if (translation.IndexOf(code.Value, StringComparison.Ordinal) < 0)
                {
                    issues.Add(kFormat);
                    break;
                }
            }
            if (!TextRules.PlaceholdersMatch(source, translation, out _))
            {
                issues.Add(kPlaceholder);
            }

            string src = s_Strip.Replace(source, " ").Trim();
            string dst = s_Strip.Replace(translation, " ").Trim();
            string lang = LanguageDetector.LanguageOf(locale);

            // 原样未译：只在原文含普通词汇时才算（品牌、车型、型号保留原文是正确做法）。
            bool hasCommonWords = TextRules.HasCommonWord(src);
            if (hasCommonWords && string.Equals(src, dst, StringComparison.OrdinalIgnoreCase) && TextRules.IsTranslatable(source, locale))
            {
                issues.Add(kUntranslated);
            }
            else if (hasCommonWords && !LanguageDetector.IsInTargetLanguage(translation, locale) && src.Split(' ').Length >= 2)
            {
                issues.Add(kUntranslated);
            }

            // 中日韩译文里残留的小写外文单词，通常是漏翻。
            if ((lang == "zh" || lang == "ja" || lang == "ko") && HasLeftoverWord(src, dst))
            {
                issues.Add(kLeftover);
            }

            if (glossary != null && glossary.Count > 0)
            {
                foreach (var term in glossary.Match(new[] { source }, 20))
                {
                    if (translation.IndexOf(term.Target, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        issues.Add(kGlossary + ":" + term.English + "=" + term.Target);
                    }
                }
            }

            // 长度异常：太短可能漏译，太长可能多译或混入了解释。
            // 设置页标题按规范写成“译名 (原名)”，长度自然偏长。
            bool keepsOriginal = translation.IndexOf("(" + source.Trim() + ")", StringComparison.Ordinal) >= 0;
            if (src.Length >= 12 && !keepsOriginal)
            {
                double ratio = (double)dst.Length / src.Length;
                bool compact = lang == "zh" || lang == "ja" || lang == "ko";
                double min = compact ? 0.12 : 0.4;
                double max = compact ? 1.6 : 2.6;
                if (ratio < min || ratio > max)
                {
                    issues.Add(kLength);
                }
            }
            return issues;
        }

        /// <summary>
        /// 译文里的小写外文单词是否属于漏翻：要在原文里也出现过，且不属于文件名（Player.log）
        /// 或夹在大写词之间的专有名词（Allianz pro Schiene）。
        /// </summary>
        private static bool HasLeftoverWord(string src, string dst)
        {
            foreach (Match m in s_LowerWord.Matches(dst))
            {
                string word = m.Value;
                int before = m.Index - 1;
                int after = m.Index + word.Length;
                if ((before >= 0 && dst[before] == '.') || (after < dst.Length && dst[after] == '.' && after + 1 < dst.Length && char.IsLetter(dst[after + 1])))
                {
                    continue; // 文件名、扩展名
                }
                if (src.IndexOf(word, StringComparison.Ordinal) < 0)
                {
                    continue; // 原文里没有，不是漏翻（如单位 cm、kmph 已在原文里则另说）
                }
                if (Regex.IsMatch(src, @"[A-Z]\w*\s+" + Regex.Escape(word) + @"\s+[A-Z]"))
                {
                    continue; // 专有名词中的小写连接词
                }
                if (Regex.IsMatch(src, @"\d" + Regex.Escape(word) + @"\b"))
                {
                    continue; // 数字后的单位，如 200kmph
                }
                return true;
            }
            return false;
        }

        private static readonly Regex s_LatinWord = new Regex(@"(?<![A-Za-z0-9_])[A-Za-z][A-Za-z']{2,}(?![A-Za-z0-9_])", RegexOptions.Compiled);

        // 照搬是正确的部分：方括号标签（"[Traffic]"）、环境变量和文件路径、带点的标识符。
        private static readonly Regex s_KeepAsIs = new Regex(
            @"\[[^\]\n]{1,40}\]|%[A-Za-z_]+%\S*|\S*[\\/]\S*|\b[A-Za-z_]+(?:\.[A-Za-z_]+)+\b",
            RegexOptions.Compiled);

        /// <summary>
        /// 中日韩译文里夹着直接照搬原文的英文单词（如 "Mediterranean Heritage中密度住宅"）。
        /// 只看原文里也有的普通单词；全大写缩写（UI、DLC）、带数字的型号、占位符和标签里的内容不算。
        /// 是否真的该翻译（品牌、人名可以保留）交给 AI 判断。
        /// </summary>
        /// <param name="ownerName">文本所属 mod 的名字：说明里提到自己的名字（"Anarchy 放置的…"）是正确的。</param>
        public static bool LooksMixed(string source, string translation, string locale, ProtectedNames names = null, string ownerName = null)
        {
            return LeftoverEnglish(source, translation, locale, names, ownerName) > 0;
        }

        /// <summary>
        /// 译文里照搬原文、本该翻译的英文单词个数（不适用的语言或整句未翻译时返回 0）。
        /// 修正混杂的改写只有让这个数变小才算改进；只是换个说法的改写不采用。
        /// </summary>
        public static int LeftoverEnglish(string source, string translation, string locale, ProtectedNames names = null, string ownerName = null)
        {
            string lang = LanguageDetector.LanguageOf(locale);
            if ((lang != "zh" && lang != "ja" && lang != "ko") || string.IsNullOrEmpty(translation) || string.IsNullOrEmpty(source))
            {
                return 0;
            }
            // 绝大多数译文里根本没有拉丁字母单词。清洗只会删掉内容或换成空格，不会拼出新单词，可以直接跳过（检测时要对几万条官方译文做这一步）。
            if (!HasLatinRun(translation))
            {
                return 0;
            }
            // mod 名等受保护的专有名词、标签和路径照搬是正确的，先去掉再检查。
            string dst = Clean(translation, names, ownerName);
            if (!LanguageDetector.ScriptMatches(dst, locale))
            {
                return 0; // 整句都没翻，属于“未翻译”，不在这里处理
            }
            string src = Clean(source, names, ownerName);
            int copied = 0;
            foreach (Match m in s_LatinWord.Matches(dst))
            {
                string word = m.Value;
                if (word.ToUpperInvariant() == word)
                {
                    continue; // 缩写
                }
                if (src.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    copied++;
                }
            }
            return copied;
        }

        /// <summary>是否含有 s_LatinWord 可能匹配的片段：一个拉丁字母后面紧跟两个拉丁字母或撇号。</summary>
        private static bool HasLatinRun(string text)
        {
            int run = 0;
            foreach (char c in text)
            {
                bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                if (letter || (c == '\'' && run > 0))
                {
                    if (++run >= 3)
                    {
                        return true;
                    }
                }
                else
                {
                    run = 0;
                }
            }
            return false;
        }

        private static readonly Regex s_TrailingTag = new Regex(@"\s*[\[(（【][^\])）】]*[\])）】]\s*$", RegexOptions.Compiled);

        private static string Clean(string text, ProtectedNames names, string ownerName)
        {
            text = names?.Strip(text) ?? text;
            if (!string.IsNullOrWhiteSpace(ownerName))
            {
                // "Road Builder [BETA]" 在说明里通常写成 "Road Builder"。
                string full = ownerName.Trim();
                string bare = s_TrailingTag.Replace(ownerName, string.Empty).Trim();
                text = RemoveIgnoreCase(text, full);
                if (bare != full)
                {
                    text = RemoveIgnoreCase(text, bare);
                }
            }
            text = s_KeepAsIs.Replace(text, " ");
            return s_Strip.Replace(text, " ");
        }

        /// <summary>把 <paramref name="name"/> 的所有出现（不分大小写）换成空格；少于 3 个字符的名字不处理。</summary>
        private static string RemoveIgnoreCase(string text, string name)
        {
            if (name.Length < 3)
            {
                return text;
            }
            int i = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (i < 0)
            {
                return text;
            }
            var sb = new System.Text.StringBuilder(text.Length);
            int start = 0;
            while (i >= 0)
            {
                sb.Append(text, start, i - start).Append(' ');
                start = i + name.Length;
                i = text.IndexOf(name, start, StringComparison.OrdinalIgnoreCase);
            }
            return sb.Append(text, start, text.Length - start).ToString();
        }

        // 地区词（简/繁中文）→ 原文里能证明它的英文写法。审校常凭 mod 名（"Eastern Europe Pack"）给译文加上地区，这不是改进。
        private static readonly (string[] words, string[] hints)[] s_RegionWords =
        {
            (new[] { "东欧", "東歐" }, new[] { "EE", "Eastern Europe", "East European" }),
            (new[] { "北美", "美式" }, new[] { "NA", "North America", "American" }),
            (new[] { "欧洲", "欧式", "歐洲", "歐式" }, new[] { "EU", "Europe", "European" }),
            (new[] { "中国", "中式", "中國" }, new[] { "CN", "China", "Chinese" }),
            (new[] { "日本", "日式" }, new[] { "JP", "Japan", "Japanese" }),
            (new[] { "美国", "美國" }, new[] { "US", "USA", "USNE", "USSW", "America", "American" }),
            (new[] { "英国", "英式", "英國" }, new[] { "UK", "Britain", "British", "England", "English" }),
            (new[] { "法国", "法式", "法國" }, new[] { "FR", "France", "French" }),
            (new[] { "德国", "德式", "德國" }, new[] { "DE", "Germany", "German" }),
            (new[] { "荷兰", "荷式", "荷蘭" }, new[] { "NL", "Netherlands", "Dutch" }),
        };

        /// <summary>
        /// 译文里有原文没有依据的地区词（如原文 "High School" → "东欧中学"）。
        /// <paramref name="before"/> 不为空时只看相对它新增的地区词（用于判断审校改写）。
        /// </summary>
        public static bool AddsRegion(string source, string translation, string before = null)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translation))
            {
                return false;
            }
            foreach (var (words, hints) in s_RegionWords)
            {
                string word = words.FirstOrDefault(w => translation.IndexOf(w, StringComparison.Ordinal) >= 0);
                if (word == null || (before != null && words.Any(w => before.IndexOf(w, StringComparison.Ordinal) >= 0)))
                {
                    continue;
                }
                bool justified = hints.Any(h => Regex.IsMatch(source, @"(?<![A-Za-z])" + Regex.Escape(h) + @"(?![a-z])"))
                    || words.Any(w => source.IndexOf(w, StringComparison.Ordinal) >= 0);
                if (!justified)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>只含术语提示时不算严重问题。</summary>
        public static bool IsSuspicious(List<string> issues) => issues.Any(i => !i.StartsWith(kGlossary, StringComparison.Ordinal)) || issues.Count >= 2;
    }
}
