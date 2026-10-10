using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AITranslate.Core
{
    /// <summary>
    /// mod 列表的模糊搜索。按匹配程度打分（越高越靠前），0 表示不匹配：
    ///   完全相同 &gt; 开头相同 &gt; 某个单词开头相同 &gt; 包含 &gt; 首字母缩写（plt → Parking Lot Tool）
    ///   &gt; 字符按顺序出现（prklot → Parking Lot）&gt; 拼写有一两个字母错误（parkng）。
    /// 多个关键词用空格分隔时，每个关键词都要匹配。
    /// </summary>
    internal static class FuzzyMatcher
    {
        public static int Score(string query, params string[] fields)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return 1;
            }
            var terms = query.ToLowerInvariant().Split(new[] { ' ', '　' }, StringSplitOptions.RemoveEmptyEntries);
            int total = 0;
            foreach (string term in terms)
            {
                int best = 0;
                foreach (string field in fields)
                {
                    if (!string.IsNullOrEmpty(field))
                    {
                        best = Math.Max(best, ScoreTerm(term, field));
                    }
                }
                if (best == 0)
                {
                    return 0;
                }
                total += best;
            }
            return total;
        }

        private static int ScoreTerm(string term, string field)
        {
            string text = field.ToLowerInvariant();
            if (text == term)
            {
                return 1000;
            }
            if (text.StartsWith(term, StringComparison.Ordinal))
            {
                return 900;
            }
            var words = Words(text);
            if (words.Any(w => w.StartsWith(term, StringComparison.Ordinal)))
            {
                return 800;
            }
            int index = text.IndexOf(term, StringComparison.Ordinal);
            if (index >= 0)
            {
                return 700 - Math.Min(index, 100);
            }
            string compact = Compact(text);
            if (compact.Contains(Compact(term)))
            {
                return 650; // 忽略空格和符号后包含，如 "roadbuilder"
            }
            if (term.Length >= 2 && Initials(words).StartsWith(term, StringComparison.Ordinal))
            {
                return 600;
            }
            int subsequence = SubsequenceScore(term, compact);
            if (subsequence > 0)
            {
                return subsequence;
            }
            return TypoScore(term, words);
        }

        /// <summary>按单词拆分（空格、符号、驼峰、中英文边界都算分隔）。</summary>
        private static List<string> Words(string lower)
        {
            var words = new List<string>();
            var sb = new StringBuilder();
            foreach (char c in lower)
            {
                if (char.IsLetterOrDigit(c))
                {
                    bool cjk = c >= 0x2E80;
                    if (sb.Length > 0 && (cjk != (sb[sb.Length - 1] >= 0x2E80)))
                    {
                        words.Add(sb.ToString());
                        sb.Clear();
                    }
                    sb.Append(c);
                }
                else if (sb.Length > 0)
                {
                    words.Add(sb.ToString());
                    sb.Clear();
                }
            }
            if (sb.Length > 0)
            {
                words.Add(sb.ToString());
            }
            return words;
        }

        private static string Compact(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray());

        private static string Initials(List<string> words) => new string(words.Where(w => w.Length > 0).Select(w => w[0]).ToArray());

        /// <summary>所有字符按顺序出现；字符越紧凑分越高（400~500）。</summary>
        private static int SubsequenceScore(string term, string compact)
        {
            string t = Compact(term);
            if (t.Length < 3)
            {
                return 0;
            }
            int pos = 0, first = -1, last = -1;
            foreach (char c in t)
            {
                int found = compact.IndexOf(c, pos);
                if (found < 0)
                {
                    return 0;
                }
                if (first < 0)
                {
                    first = found;
                }
                last = found;
                pos = found + 1;
            }
            int span = last - first + 1;
            return 400 + 100 * t.Length / Math.Max(span, t.Length);
        }

        /// <summary>拼写错误容忍：与某个单词（或其等长前缀）的编辑距离不超过 1（7 个字母以上不超过 2）。</summary>
        private static int TypoScore(string term, List<string> words)
        {
            if (term.Length < 4)
            {
                return 0;
            }
            int allowed = term.Length >= 7 ? 2 : 1;
            int best = int.MaxValue;
            foreach (string word in words)
            {
                if (word.Length + allowed < term.Length)
                {
                    continue;
                }
                string prefix = word.Length > term.Length + allowed ? word.Substring(0, term.Length) : word;
                best = Math.Min(best, Distance(term, prefix));
            }
            return best <= allowed ? 300 - best * 50 : 0;
        }

        private static int Distance(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
            {
                prev[j] = j;
            }
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var tmp = prev;
                prev = cur;
                cur = tmp;
            }
            return prev[b.Length];
        }
    }
}
