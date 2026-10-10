using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AITranslate.Core
{
    /// <summary>
    /// 必须原样保留的专有名词：已安装 mod 的名称（如 "Bridge Builder"、"Move It"）。
    /// 翻译时告诉模型、校验译文是否保留、找出以前被翻掉的译文，以及“中英混杂”检查时忽略它们。
    /// </summary>
    internal sealed class ProtectedNames
    {
        public static readonly ProtectedNames Empty = new ProtectedNames(Enumerable.Empty<string>());

        // 只去掉末尾的标签（"[BETA]"、"(Styx's)"）；开头的 "[G87]" 是名字的一部分，去掉会变成 "Road Markings" 这种普通词组。
        private static readonly Regex s_Brackets = new Regex(@"(\s*[\[(（【][^\])）】]*[\])）】])+\s*$", RegexOptions.Compiled);
        private static readonly Regex s_Version = new Regex(@"\s+v?\d+(\.\d+)+\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly List<string> m_Names;

        public int Count => m_Names.Count;

        public ProtectedNames(IEnumerable<string> rawNames)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in rawNames ?? Enumerable.Empty<string>())
            {
                foreach (string name in Normalize(raw))
                {
                    if (IsDistinctive(name))
                    {
                        names.Add(name);
                    }
                }
            }
            // 长名字优先，避免 "Move It" 抢先匹配 "Move It Extended" 的一部分。
            m_Names = names.OrderByDescending(n => n.Length).ToList();
        }

        /// <summary>显示名可能带 "[BETA]"、"(Styx's)"、版本号，或用 "|" "/" 拼了多语言名称，拆出英文名部分。</summary>
        private static IEnumerable<string> Normalize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                yield break;
            }
            string s = s_Brackets.Replace(raw, " ");
            foreach (string part in s.Split(new[] { '|', '/', '：', ':' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = s_Version.Replace(part.Trim(), string.Empty).Trim().Trim('-', '–', ' ');
                if (p.Length > 0)
                {
                    yield return p;
                }
            }
        }

        /// <summary>
        /// 只保护有辨识度的名字：拉丁字母写成、至少两个单词（Bridge Builder），或单个词但带内部大写/数字（ExtraLib、MoveIt）。
        /// "Traffic"、"Anarchy" 这类普通单词作名字时会误伤普通文本，不保护（交给提示词规则）。
        /// </summary>
        private static bool IsDistinctive(string name)
        {
            if (name.Length < 4 || name.Length > 40 || name.Any(c => c > 0x2E80) || !name.Any(char.IsLetter))
            {
                return false;
            }
            string[] words = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2)
            {
                return words.Length <= 5 && char.IsUpper(words[0][0]);
            }
            string w = words[0];
            return w.Skip(1).Any(char.IsUpper) || w.Any(char.IsDigit);
        }

        /// <summary>文本中出现的受保护名称（按原文大小写、完整单词匹配）。</summary>
        public List<string> FindIn(string text)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                return found;
            }
            foreach (string name in m_Names)
            {
                int i = text.IndexOf(name, StringComparison.Ordinal);
                while (i >= 0)
                {
                    bool startOk = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
                    int end = i + name.Length;
                    bool endOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                    if (startOk && endOk)
                    {
                        if (!found.Any(f => f.Contains(name)))
                        {
                            found.Add(name);
                        }
                        break;
                    }
                    i = text.IndexOf(name, i + 1, StringComparison.Ordinal);
                }
            }
            return found;
        }

        /// <summary>原文里的受保护名称在译文中丢失（被翻译或改写了）。</summary>
        public bool Lost(string source, string translation, out string name)
        {
            foreach (string n in FindIn(source))
            {
                if (translation == null || translation.IndexOf(n, StringComparison.Ordinal) < 0)
                {
                    name = n;
                    return true;
                }
            }
            name = null;
            return false;
        }

        /// <summary>去掉文本中的受保护名称（混杂检查前使用）。</summary>
        public string Strip(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            foreach (string n in m_Names)
            {
                text = text.Replace(n, " ");
            }
            return text;
        }
    }
}
