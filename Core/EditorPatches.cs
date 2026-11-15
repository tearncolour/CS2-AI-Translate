using System;
using Game.UI.Widgets;
using HarmonyLib;

namespace AITranslate.Core
{
    /// <summary>
    /// 让资产编辑器里的组件标题和“添加组件”列表显示译文。
    /// 这些文字在游戏里是写死的英文（NicifyVariableName 的结果直接当字面量显示），不查词典，
    /// 只能在这个函数的出口把类型名换成译文。字段名走词典（Editor.FIELD），不需要补丁。
    /// Harmony 相关的代码全部放在这个类里：缺少 0Harmony.dll 时只有“编辑器标题翻译”失效，mod 其他功能不受影响。
    /// </summary>
    internal static class EditorPatches
    {
        private const string kHarmonyId = "AITranslate.EditorTitles";

        private static Harmony s_Harmony;

        /// <summary>安装补丁（已安装则什么也不做）。失败时抛出异常，由调用方记录。</summary>
        public static void Install()
        {
            if (s_Harmony != null)
            {
                return;
            }
            var target = AccessTools.Method(typeof(WidgetReflectionUtils), nameof(WidgetReflectionUtils.NicifyVariableName), new[] { typeof(string) });
            if (target == null)
            {
                throw new MissingMethodException("WidgetReflectionUtils.NicifyVariableName not found (game changed)");
            }
            var harmony = new Harmony(kHarmonyId);
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(EditorPatches), nameof(Postfix)));
            s_Harmony = harmony;
        }

        public static void Uninstall()
        {
            try
            {
                s_Harmony?.UnpatchAll(kHarmonyId);
            }
            finally
            {
                s_Harmony = null;
            }
        }

        // Harmony 约定的参数名：name 是原函数的参数，__result 是返回值。
        private static void Postfix(string name, ref string __result)
        {
            EditorLabels.TranslateTitle(name, ref __result);
        }
    }
}
