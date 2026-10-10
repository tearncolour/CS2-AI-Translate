using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AITranslate.Core
{
    /// <summary>
    /// 从游戏现有的「英文 ↔ 目标语言」对照中提取短术语（如 Zoning → 分区），
    /// 翻译时只把本批文本中实际出现的术语附给模型，保证与官方译名一致。
    /// </summary>
    internal sealed class Glossary
    {
        public static readonly Glossary Empty = new Glossary(new Dictionary<string, Term>());

        private const int kMaxWords = 4;

        private static readonly Regex s_Word = new Regex(@"[A-Za-z][A-Za-z'&\-]*", RegexOptions.Compiled);

        private static readonly Regex s_TermShape = new Regex(@"^[A-Za-z][A-Za-z'&\- ]{1,39}$", RegexOptions.Compiled);

        internal sealed class Term
        {
            public string English;
            public string Target;
        }

        private readonly Dictionary<string, Term> m_Terms;

        private Glossary(Dictionary<string, Term> terms)
        {
            m_Terms = terms;
        }

        public int Count => m_Terms.Count;

        /// <summary>加入（并覆盖）一定使用的官方译名，例如区域建筑类型 "Residential Low" → "低层住宅"。</summary>
        public Glossary WithForced(IReadOnlyDictionary<string, string> terms)
        {
            if (terms == null || terms.Count == 0)
            {
                return this;
            }
            var merged = new Dictionary<string, Term>(m_Terms, StringComparer.Ordinal);
            foreach (var kv in terms)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                {
                    merged[Normalize(kv.Key)] = new Term { English = kv.Key, Target = kv.Value };
                }
            }
            return new Glossary(merged);
        }

        /// <param name="english">key → 英文</param>
        /// <param name="translated">key → 目标语言（只含真正翻译过的条目）</param>
        public static Glossary Build(IReadOnlyDictionary<string, string> english, IReadOnlyDictionary<string, string> translated, string targetLocale)
        {
            // 英文小写 → (译文 → 次数)
            var votes = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            var display = new Dictionary<string, string>(StringComparer.Ordinal);
            bool latinTarget = LanguageDetector.ScriptMatches("a", targetLocale);

            foreach (var kv in english)
            {
                string en = kv.Value?.Trim();
                if (string.IsNullOrEmpty(en) || !s_TermShape.IsMatch(en))
                {
                    continue;
                }
                int words = en.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
                if (words > kMaxWords)
                {
                    continue;
                }
                if (!translated.TryGetValue(kv.Key, out var target) || string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }
                target = target.Trim();
                if (target.Length > (latinTarget ? 40 : 20) || target == en || target.IndexOfAny(new[] { '{', '<', '\n', '%' }) >= 0)
                {
                    continue;
                }
                // 非拉丁文字的语言要求译名确实是该文字；拉丁文字语言只要求与英文不同。
                if ((!latinTarget && !LanguageDetector.ScriptMatches(target, targetLocale)) || string.Equals(target, en, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string norm = Normalize(en);
                if (!votes.TryGetValue(norm, out var bucket))
                {
                    bucket = new Dictionary<string, int>(StringComparer.Ordinal);
                    votes[norm] = bucket;
                    display[norm] = en;
                }
                bucket.TryGetValue(target, out int n);
                bucket[target] = n + 1;
            }

            var terms = new Dictionary<string, Term>(StringComparer.Ordinal);
            foreach (var kv in votes)
            {
                int total = kv.Value.Values.Sum();
                var best = kv.Value.OrderByDescending(p => p.Value).First();
                bool singleWord = kv.Key.IndexOf(' ') < 0;
                // 单词歧义大，要求至少出现两次且译法占多数；词组只要译法占多数。
                if (best.Value * 10 < total * 6 || (singleWord && best.Value < 2))
                {
                    continue;
                }
                terms[kv.Key] = new Term { English = display[kv.Key], Target = best.Key };
            }
            return new Glossary(terms);
        }

        /// <summary>找出文本中出现的术语，长词组优先。</summary>
        public List<Term> Match(IEnumerable<string> texts, int limit)
        {
            var found = new Dictionary<string, Term>(StringComparer.Ordinal);
            if (m_Terms.Count == 0)
            {
                return new List<Term>();
            }
            foreach (string text in texts)
            {
                var words = s_Word.Matches(text).Cast<Match>().Select(m => m.Value.ToLowerInvariant()).ToArray();
                for (int i = 0; i < words.Length; i++)
                {
                    string gram = null;
                    for (int n = 1; n <= kMaxWords && i + n <= words.Length; n++)
                    {
                        gram = n == 1 ? words[i] : gram + " " + words[i + n - 1];
                        if (m_Terms.TryGetValue(gram, out var term))
                        {
                            found[gram] = term;
                        }
                    }
                }
            }
            return found
                .OrderByDescending(p => p.Key.Count(c => c == ' '))
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .Take(limit)
                .Select(p => p.Value)
                .ToList();
        }

        private static string Normalize(string en)
        {
            return string.Join(" ", en.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
