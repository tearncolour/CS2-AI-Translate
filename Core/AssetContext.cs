using System;
using System.Collections.Generic;
using System.Linq;
using Game.Prefabs;
using Newtonsoft.Json.Linq;

namespace AITranslate.Core
{
    /// <summary>
    /// 一个资产的可见信息，用来让模型写描述。全部在主线程从预制件读取，之后可在后台使用。
    /// </summary>
    internal sealed class AssetContext
    {
        public string DescriptionKey;
        public string TitleKey;
        public string PrefabName;
        public string Title;            // 当前语言下显示的名称
        public string EnglishTitle;     // 英文名称（可能为空）
        public string Kind;             // 预制件类型，如 BuildingPrefab
        public string Category;         // 建造菜单分类
        public string Zone;
        public int Level;
        public string Service;
        public string Lot;
        public List<string> Features = new List<string>();

        /// <summary>资产只有随机编号作名字（GUID/哈希），需要同时起名。</summary>
        public bool NeedsName;

        /// <summary>资产自带的描述（只需起名时提供给模型参考，生成的描述不保存）。</summary>
        public string OwnDescription;

        // 道路、轨道、步道：车道构成（截面名 × 数量）、限速、类型。
        public List<string> Sections = new List<string>();
        public float SpeedLimit;
        public string NetType;

        // 这些组件几乎每个资产都有，对判断用途没有帮助。
        private static readonly HashSet<string> s_BoringComponents = new HashSet<string>(StringComparer.Ordinal)
        {
            "UIObject", "ObjectSubObjects", "ObjectSubAreas", "ObjectSubNets", "ObjectSubLanes", "ObjectMeshes", "SubMeshes",
            "EditorAssetCategoryOverride", "ContentPrerequisite", "Unlockable", "UnlockOnBuild", "PlaceableObject",
            "SpawnableObject", "ThemeObject", "AssetPackItem", "ObsoleteIdentifiers", "PlaceholderObject", "BuildingTerraformOverride",
            "StandingObject", "Pillar", "NetSubObjects", "AuxiliaryNets", "EffectSource", "ActivityLocation", "ObjectAnimation",
        };

        public static AssetContext From(PrefabBase prefab, string descriptionKey, string titleKey, string title, string englishTitle)
        {
            var ctx = new AssetContext
            {
                DescriptionKey = descriptionKey,
                TitleKey = titleKey,
                PrefabName = prefab.name,
                Title = title,
                EnglishTitle = englishTitle,
                Kind = prefab.GetType().Name,
            };
            if (prefab is BuildingPrefab building)
            {
                ctx.Lot = building.m_LotWidth + "x" + building.m_LotDepth;
            }
            if (prefab.TryGet<SpawnableBuilding>(out var spawnable))
            {
                ctx.Zone = spawnable.m_ZoneType?.name;
                ctx.Level = spawnable.m_Level;
            }
            if (prefab.TryGet<ServiceObject>(out var service))
            {
                ctx.Service = service.m_Service?.name;
            }
            if (prefab.TryGet<UIObject>(out var ui))
            {
                ctx.Category = ui.m_Group?.name;
            }
            ReadNetInfo(prefab, ctx);
            foreach (var component in prefab.components)
            {
                if (component == null)
                {
                    continue;
                }
                string name = component.GetType().Name;
                if (!s_BoringComponents.Contains(name) && !ctx.Features.Contains(name))
                {
                    ctx.Features.Add(name);
                }
                if (ctx.Features.Count >= 15)
                {
                    break;
                }
            }
            return ctx;
        }

        /// <summary>道路/轨道/步道的截面构成，例如 "Car Drive Section 3 ×4"、"Sidewalk 3 ×2"，能看出车道数和类型。</summary>
        private static void ReadNetInfo(PrefabBase prefab, AssetContext ctx)
        {
            try
            {
                switch (prefab)
                {
                    case RoadPrefab road:
                        ctx.NetType = "road:" + road.m_RoadType + (road.m_HighwayRules ? ",highway" : string.Empty);
                        ctx.SpeedLimit = road.m_SpeedLimit;
                        break;
                    case TrackPrefab track:
                        ctx.NetType = "track:" + track.m_TrackType;
                        ctx.SpeedLimit = track.m_SpeedLimit;
                        break;
                    case PathwayPrefab path:
                        ctx.NetType = "pathway";
                        ctx.SpeedLimit = path.m_SpeedLimit;
                        break;
                }
                if (prefab is NetGeometryPrefab geometry && geometry.m_Sections != null)
                {
                    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                    var order = new List<string>();
                    foreach (var section in geometry.m_Sections)
                    {
                        string name = section?.m_Section?.name;
                        if (string.IsNullOrEmpty(name))
                        {
                            continue;
                        }
                        if (!counts.ContainsKey(name))
                        {
                            order.Add(name);
                            counts[name] = 0;
                        }
                        counts[name]++;
                    }
                    foreach (string name in order.Take(12))
                    {
                        ctx.Sections.Add(counts[name] > 1 ? name + " ×" + counts[name] : name);
                    }
                }
            }
            catch (Exception)
            {
                // 部分道路预制件字段为空，忽略。
            }
        }

        /// <summary>给模型的 JSON 描述（只含有值的字段）。</summary>
        public JObject ToJson()
        {
            var o = new JObject();
            if (NeedsName)
            {
                // 名字只是随机编号，不给模型看，避免它照抄。
                o["name"] = null;
                if (!string.IsNullOrEmpty(OwnDescription))
                {
                    o["existing_description"] = OwnDescription;
                }
            }
            else
            {
                o["name"] = Title;
                if (!string.IsNullOrEmpty(EnglishTitle) && EnglishTitle != Title)
                {
                    o["english_name"] = EnglishTitle;
                }
                o["internal_name"] = PrefabName;
            }
            o["kind"] = Kind;
            if (!string.IsNullOrEmpty(Category))
            {
                o["menu_category"] = Category;
            }
            if (!string.IsNullOrEmpty(Zone))
            {
                o["zone"] = Zone;
                o["level"] = Level;
            }
            if (!string.IsNullOrEmpty(Service))
            {
                o["service"] = Service;
            }
            if (!string.IsNullOrEmpty(Lot))
            {
                o["lot_cells"] = Lot;
            }
            if (Features.Count > 0)
            {
                o["features"] = new JArray(Features.Cast<object>().ToArray());
            }
            if (!string.IsNullOrEmpty(NetType))
            {
                o["network_type"] = NetType;
            }
            if (SpeedLimit > 0)
            {
                o["speed_limit_kmh"] = Math.Round(SpeedLimit);
            }
            if (Sections.Count > 0)
            {
                o["cross_section"] = new JArray(Sections.Cast<object>().ToArray());
            }
            return o;
        }

        /// <summary>用于记录“依据什么生成的描述”。</summary>
        public override string ToString() => ToJson().ToString(Newtonsoft.Json.Formatting.None);
    }
}
