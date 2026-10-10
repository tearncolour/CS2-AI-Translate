using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AITranslate.Core
{
    /// <summary>
    /// 轻量语言识别：判断一段 mod 文本是否已经是游戏当前语言。
    /// 非拉丁文字按书写系统判断；拉丁文字语言按常用虚词与特有字母打分，证据不足时
    /// 视为英文（mod 默认语言）。
    /// </summary>
    internal static class LanguageDetector
    {
        private enum Script
        {
            Latin,
            Han,
            Kana,
            Hangul,
            Cyrillic,
            Greek,
            Thai,
            Arabic,
            Hebrew,
            Other,
        }

        private static readonly Regex s_Strip = new Regex(
            @"\{[^{}\r\n]{0,80}\}|%(?:\d+\$)?[-+0#]*\d*(?:\.\d+)?[sdfixXc]|<\/?[A-Za-z][^<>\r\n]{0,80}>|https?://\S+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex s_Word = new Regex(@"[\p{L}']+", RegexOptions.Compiled);

        // ---------------------------------------------------------------- 拉丁文字语言特征

        private static readonly Dictionary<string, HashSet<string>> s_StopWords = new Dictionary<string, HashSet<string>>
        {
            ["en"] = Set("the a an and of to is are was were be been for with this that these those you your it its at in on as by from or not no can will would should could has have had do does did when where what which who how all any each some more most less least than then there their they them he she we our us my me if into out up down over under about after before between only also just now per new"),
            ["de"] = Set("der die das und ist nicht mit für ein eine einen dem den des auf sie wird werden zu im von bei oder auch wenn alle kann nur sind wie"),
            ["fr"] = Set("le la les des et est pour avec une un du ce cette vous dans sur pas qui sont ne au aux être peut tous si en par"),
            ["es"] = Set("el la los las y es para con una un del que se por en no al este esta puede son todos si como más"),
            ["it"] = Set("il lo la gli le e è per con una un del della che non di nel sono questo questa può tutti se come"),
            ["pt"] = Set("o a os as e é para com uma um do da que não em no na são este esta pode todos se como mais você"),
            ["nl"] = Set("de het een en is van voor met niet dat dit zijn op te kan worden wordt als alle ook"),
            ["pl"] = Set("i w z na nie jest do się że to dla od jak są po przez lub może wszystkie ten"),
            ["cs"] = Set("a v na je se že to pro s z do není jsou jak nebo může všechny tento"),
            ["sv"] = Set("och att det är för med en ett inte som på av till kan alla om"),
            ["da"] = Set("og at det er for med en et ikke som på af til kan alle hvis"),
            ["nb"] = Set("og at det er for med en et ikke som på av til kan alle hvis"),
            ["fi"] = Set("ja on ei se että kun tai kaikki voi ovat tämä myös"),
            ["tr"] = Set("ve bir bu için ile da de değil olan tüm çok daha ya veya"),
            ["hu"] = Set("a az és egy nem van hogy meg is ez minden vagy lehet"),
            ["ro"] = Set("și de la în nu este pentru cu un o care sunt toate sau"),
            ["id"] = Set("dan yang di ini untuk dengan tidak dari ke akan dapat semua atau"),
            ["vi"] = Set("và của là cho với không này được các một có những"),
        };

        // 某种语言独有（或高度特征）的字母。
        private static readonly Dictionary<string, string> s_Letters = new Dictionary<string, string>
        {
            ["de"] = "äöüß",
            ["fr"] = "àâçéèêëîïôûùœÿ",
            ["es"] = "ñ¿¡áíóú",
            ["it"] = "àèìòù",
            ["pt"] = "ãõçâêôá",
            ["pl"] = "ąćęłńśźż",
            ["cs"] = "ěščřžůý",
            ["sv"] = "åäö",
            ["da"] = "æøå",
            ["nb"] = "æøå",
            ["fi"] = "äö",
            ["tr"] = "ğışçöü",
            ["hu"] = "őű",
            ["ro"] = "ăâîșțş",
            ["vi"] = "ơưđạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ",
        };

        // 只出现在其中一种语言里的字母（用于排除）。
        private static readonly Dictionary<string, string> s_Exclusive = new Dictionary<string, string>
        {
            ["de"] = "ß",
            ["es"] = "ñ¿¡",
            ["pl"] = "ąęłńśźż",
            ["cs"] = "ěřů",
            ["tr"] = "ğış",
            ["ro"] = "ășț",
            ["hu"] = "őű",
            ["fr"] = "œ",
            ["pt"] = "ãõ",
            ["vi"] = "ơưđ",
        };

        // ---------------------------------------------------------------- 简繁

        private const string kTraditionalOnly = "這個們來時會說與為開關頁設據車鐵橋線區場輛點選擇顯資訊檔載預觀燈號運廣將後應發當實對壓價錢議儲標體專業網絡電動購買學門問題從還讓種樣經務項變離數據總認進過無長書飛機場園環邊視錯誤請隱藏導覽";
        private const string kSimplifiedOnly = "这个们来时会说与为开关页设据车铁桥线区场辆点选择显资讯档载预观灯号运广将后应发当实对压价钱议储标体专业网络电动购买学门问题从还让种样经务项变离数据总认进过无长书飞机场园环边视错误请隐藏导览";

        private static HashSet<string> Set(string words) => new HashSet<string>(words.Split(' '), StringComparer.Ordinal);

        public static string LanguageOf(string locale)
        {
            string l = (locale ?? string.Empty).ToLowerInvariant();
            int dash = l.IndexOf('-');
            return dash > 0 ? l.Substring(0, dash) : l;
        }

        private static Script ScriptOf(char c)
        {
            if (c < 0x0250)
            {
                return char.IsLetter(c) ? Script.Latin : Script.Other;
            }
            if (c >= 0x1E00 && c <= 0x1EFF)
            {
                return Script.Latin; // 越南语等扩展拉丁字母
            }
            if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF))
            {
                return Script.Han;
            }
            if (c >= 0x3040 && c <= 0x30FF)
            {
                return Script.Kana;
            }
            if ((c >= 0xAC00 && c <= 0xD7AF) || (c >= 0x1100 && c <= 0x11FF) || (c >= 0x3130 && c <= 0x318F))
            {
                return Script.Hangul;
            }
            if (c >= 0x0400 && c <= 0x052F)
            {
                return Script.Cyrillic;
            }
            if (c >= 0x0370 && c <= 0x03FF)
            {
                return Script.Greek;
            }
            if (c >= 0x0E00 && c <= 0x0E7F)
            {
                return Script.Thai;
            }
            if (c >= 0x0600 && c <= 0x06FF)
            {
                return Script.Arabic;
            }
            if (c >= 0x0590 && c <= 0x05FF)
            {
                return Script.Hebrew;
            }
            return char.IsLetter(c) ? Script.Other : Script.Other;
        }

        private static Script ScriptOfLanguage(string lang)
        {
            switch (lang)
            {
                case "zh": return Script.Han;
                case "ja": return Script.Kana;
                case "ko": return Script.Hangul;
                case "ru":
                case "uk":
                case "bg":
                case "sr":
                case "be":
                case "kk": return Script.Cyrillic;
                case "el": return Script.Greek;
                case "th": return Script.Thai;
                case "ar":
                case "fa": return Script.Arabic;
                case "he": return Script.Hebrew;
                default: return Script.Latin;
            }
        }

        /// <summary>去掉占位符、标签、URL 后是否还有任何文字。</summary>
        public static bool HasLetters(string text)
        {
            string s = s_Strip.Replace(text ?? string.Empty, " ");
            return s.Any(char.IsLetter);
        }

        /// <summary>只按书写系统判断（用于术语表等短词，拉丁语言之间不区分）。</summary>
        public static bool ScriptMatches(string text, string locale)
        {
            Script target = ScriptOfLanguage(LanguageOf(locale));
            if (target == Script.Kana)
            {
                return text.Any(c => ScriptOf(c) == Script.Kana || ScriptOf(c) == Script.Han);
            }
            return text.Any(c => ScriptOf(c) == target);
        }

        /// <summary>文本是否已经是目标语言（无需翻译）。</summary>
        public static bool IsInTargetLanguage(string text, string locale)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(locale))
            {
                return false;
            }
            string lang = LanguageOf(locale);
            string s = s_Strip.Replace(text, " ");

            var counts = new Dictionary<Script, int>();
            int letters = 0;
            foreach (char c in s)
            {
                if (!char.IsLetter(c))
                {
                    continue;
                }
                Script sc = ScriptOf(c);
                counts.TryGetValue(sc, out int n);
                counts[sc] = n + 1;
                letters++;
            }
            if (letters == 0)
            {
                return true;
            }
            int Count(Script sc) => counts.TryGetValue(sc, out int n) ? n : 0;

            switch (lang)
            {
                case "zh":
                    // 含汉字且没有假名即视为中文（中英混排的双语名称也算），再区分简繁。
                    if (Count(Script.Han) == 0 || Count(Script.Kana) > 0)
                    {
                        return false;
                    }
                    return MatchesChineseVariant(s, locale);
                case "ja":
                    if (Count(Script.Kana) > 0)
                    {
                        return true;
                    }
                    // 只有汉字：短词可能是日文（設定、道路），长句更可能是中文。
                    return Count(Script.Han) > 0 && Count(Script.Han) <= 6 && Count(Script.Latin) == 0;
                case "ko":
                    return Count(Script.Hangul) > 0;
            }

            Script target = ScriptOfLanguage(lang);
            if (target != Script.Latin)
            {
                if (Count(target) * 2 < letters)
                {
                    return false;
                }
                return target != Script.Cyrillic || MatchesCyrillicVariant(s, lang);
            }

            // 拉丁文字目标语言：先要求以拉丁字母为主。
            if (Count(Script.Latin) * 2 < letters)
            {
                return false;
            }
            return LatinLanguageMatches(s, lang);
        }

        private static bool MatchesChineseVariant(string s, string locale)
        {
            int trad = s.Count(c => kTraditionalOnly.IndexOf(c) >= 0);
            int simp = s.Count(c => kSimplifiedOnly.IndexOf(c) >= 0);
            bool wantTraditional = locale.IndexOf("hant", StringComparison.OrdinalIgnoreCase) >= 0
                || locale.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                || locale.EndsWith("-HK", StringComparison.OrdinalIgnoreCase);
            return wantTraditional ? !(simp > 0 && trad == 0) : !(trad > 0 && simp == 0);
        }

        private static bool MatchesCyrillicVariant(string s, string lang)
        {
            string lower = s.ToLowerInvariant();
            bool ukrainian = lower.IndexOfAny("іїєґ".ToCharArray()) >= 0;
            bool russian = lower.IndexOfAny("ыэъё".ToCharArray()) >= 0;
            switch (lang)
            {
                case "ru": return !(ukrainian && !russian);
                case "uk": return !(russian && !ukrainian);
                default: return true;
            }
        }

        private static bool LatinLanguageMatches(string s, string lang)
        {
            string lower = s.ToLowerInvariant();

            // 出现其他语言独有字母而没有目标语言字母 → 不是目标语言。
            bool hasOwnLetter = s_Letters.TryGetValue(lang, out var own) && lower.IndexOfAny(own.ToCharArray()) >= 0;
            foreach (var kv in s_Exclusive)
            {
                if (kv.Key != lang && !hasOwnLetter && lower.IndexOfAny(kv.Value.ToCharArray()) >= 0)
                {
                    return false;
                }
            }

            var words = s_Word.Matches(lower).Cast<Match>().Select(m => m.Value).ToList();
            var scores = new Dictionary<string, int>();
            foreach (var kv in s_StopWords)
            {
                // 单字母词（a、s、i、e、o…）在多种语言里都是虚词，不计分。
                int score = words.Count(w => w.Length > 1 && kv.Value.Contains(w));
                if (s_Letters.TryGetValue(kv.Key, out var letters) && lower.IndexOfAny(letters.ToCharArray()) >= 0)
                {
                    score += 1; // 带重音的专有名词（如 Condé Nast）很常见，只算一处证据
                }
                scores[kv.Key] = score;
            }
            scores.TryGetValue(lang, out int mine);
            var best = scores.Where(p => p.Key != lang).OrderByDescending(p => p.Value).First();

            // 一个虚词撞车（如英文 "at" 同时是丹麦语）不足为凭，至少两处证据才认定是另一种语言。
            if (best.Value >= 2 && best.Value > mine)
            {
                return false;
            }
            if (mine > 0 && mine >= best.Value)
            {
                return true;
            }
            // 证据不足（多为一两个词的短标签）：mod 默认用英文书写。
            return lang == "en";
        }
    }
}
