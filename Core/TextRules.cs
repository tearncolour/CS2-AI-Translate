using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AITranslate.Core
{
    /// <summary>判断一条文本是否值得翻译，以及校验译文是否完整保留了占位符。</summary>
    internal static class TextRules
    {
        // {0} {NUMBER} {{x}} / %s %d %1$s / <b> </color> <br/>
        private static readonly Regex s_Placeholder = new Regex(
            @"\{[^{}\r\n]{0,80}\}|%(?:\d+\$)?[-+0#]*\d*(?:\.\d+)?[sdfixXc]|<\/?[A-Za-z][^<>\r\n]{0,80}>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex s_Url = new Regex(@"^\s*(https?|ftp)://\S+\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 无空格且带数字/下划线/斜杠/中间点的单个记号：型号、标识符、文件名、版本号等。
        private static readonly Regex s_CodeToken = new Regex(@"^(?:\S*[0-9_/\\]\S*|\S+[.:]\S+)$", RegexOptions.Compiled);

        private static readonly char[] s_TrailingPunctuation = { '.', '!', '?', ':', '…', ',', ';' };

        public static bool IsTranslatable(string source, string targetLocale)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }
            if (!LanguageDetector.HasLetters(source) || s_Url.IsMatch(source))
            {
                return false;
            }
            // 只对纯 ASCII 的单个记号做标识符判断；中日韩文本本身不含空格，不能套用。
            string trimmed = source.Trim().TrimEnd(s_TrailingPunctuation);
            if (trimmed.All(c => c < 128) && trimmed.IndexOfAny(new[] { ' ', '\t', '\n' }) < 0 && s_CodeToken.IsMatch(trimmed))
            {
                return false;
            }
            // 多个记号但都是内部标识（如 "USNE_CommercialHigh_L3_2x4 ProcReq"）：带下划线且没有任何普通小写单词。
            if (trimmed.All(c => c < 128) && trimmed.IndexOf('_') >= 0 && !HasCommonWord(trimmed))
            {
                return false;
            }
            if (IsMachineIdentifier(trimmed))
            {
                return false;
            }
            return !LanguageDetector.IsInTargetLanguage(source, targetLocale);
        }

        // GUID（8-4-4-4-12）、连续 12 位以上的十六进制串、17 位 Steam 用户号。
        private static readonly Regex s_MachineId = new Regex(
            @"[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}|(?<![0-9a-zA-Z])[0-9a-fA-F]{12,}(?![0-9a-zA-Z])|7656119\d{10}",
            RegexOptions.Compiled);

        /// <summary>内部 ID：含 GUID、长十六进制串或 Steam 用户号。</summary>
        public static bool IsMachineIdentifier(string text) => !string.IsNullOrEmpty(text) && s_MachineId.IsMatch(text);

        private static readonly Regex s_CommonWord = new Regex(@"(?<![\w.])[a-z]{3,}(?![\w.])", RegexOptions.Compiled);

        /// <summary>是否含有独立的全小写单词（普通词汇；专有名词、型号、标识符通常没有）。</summary>
        public static bool HasCommonWord(string text) => s_CommonWord.IsMatch(text ?? string.Empty);

        public static List<string> ExtractPlaceholders(string text)
        {
            var list = new List<string>();
            foreach (Match m in s_Placeholder.Matches(text ?? string.Empty))
            {
                list.Add(m.Value);
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>占位符集合（含次数）必须一致，顺序可以变化。</summary>
        public static bool PlaceholdersMatch(string source, string translation, out string reason)
        {
            reason = null;
            var a = ExtractPlaceholders(source);
            var b = ExtractPlaceholders(translation);
            if (a.SequenceEqual(b, StringComparer.Ordinal))
            {
                return true;
            }
            reason = "placeholders [" + string.Join(" ", a) + "] vs [" + string.Join(" ", b) + "]";
            return false;
        }

        public static bool IsValidTranslation(string source, string translation, out string reason)
        {
            if (string.IsNullOrWhiteSpace(translation))
            {
                reason = "empty";
                return false;
            }
            if (!PlaceholdersMatch(source, translation, out reason))
            {
                return false;
            }
            // 原文没有换行而译文多出大量换行，通常是模型把多条拼在一起了。
            int srcLines = Count(source, '\n');
            int dstLines = Count(translation, '\n');
            if (dstLines > srcLines + 2)
            {
                reason = "line count " + srcLines + " vs " + dstLines;
                return false;
            }
            return true;
        }

        private static int Count(string s, char c)
        {
            int n = 0;
            foreach (char ch in s)
            {
                if (ch == c)
                {
                    n++;
                }
            }
            return n;
        }
    }
}
