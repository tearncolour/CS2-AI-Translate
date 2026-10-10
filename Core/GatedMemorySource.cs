using System.Collections.Generic;
using System.Linq;
using Colossal;
using Colossal.Localization;
using Game.SceneFlow;

namespace AITranslate.Core
{
    /// <summary>
    /// 未安装 I18N Everywhere 时把译文注入游戏本地化的源。只在其语言为当前游戏语言时提供条目，
    /// 停用（Retired）后永远返回空：LocalizationManager 不会真正移除用户源，资产批量重载时还会重新加入。
    /// </summary>
    internal sealed class GatedMemorySource : IDictionarySource
    {
        public string Locale { get; }

        public Dictionary<string, string> Entries { get; }

        public bool Retired { get; set; }

        public GatedMemorySource(string locale, Dictionary<string, string> entries)
        {
            Locale = locale;
            Entries = entries;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            if (Retired || GameManager.instance?.localizationManager?.activeLocaleId != Locale)
            {
                return Enumerable.Empty<KeyValuePair<string, string>>();
            }
            foreach (var kv in Entries)
            {
                LocalizationEntry entry = LocalizationValidation.ParseEntry(kv);
                if (entry != null && (entry.type == LocalizationEntry.IdentifierType.HashedIndexed || entry.type == LocalizationEntry.IdentifierType.Indexed))
                {
                    string id = entry.identifierWithoutIndex;
                    if (!indexCounts.TryGetValue(id, out int count) || count < entry.index + 1)
                    {
                        indexCounts[id] = entry.index + 1;
                    }
                }
            }
            return Entries.ToList();
        }

        public void Unload()
        {
        }
    }
}
