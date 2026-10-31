using System;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 设置面板每次打开前，把分组标题和下拉框选项换成当前语言；本模组配置写盘前再把选项换回固定文字。
///
/// 为什么需要这一步：ModConfig.Define 的 section 是**静态字符串**，直接存进
/// ConfigEntry.Section，ModConfigWindow.Open 里原样交给 AddHeader(text)（不做本地化）。
/// 而 ModSettings.Init 跑在主菜单早期，那时 LocalizationSettings.SelectedLocale
/// 可能还是 null，GetCurrentLocaleCode() 返回 ""——标题就被永久固定成那一刻的语言，
/// 而 label/tooltip 是 Func&lt;string&gt;（面板渲染时才求值）所以是中文。
/// 典型症状就是：条目中文、分组标题英文。
///
/// 游戏没有提供改标题的公开 API（Section 的 setter 是 internal），
/// 因此在面板入口处补一次重写；失败只影响标题语言，不影响面板本身。
/// </summary>
internal static class ConfigSectionRefresh
{
    private const string HarmonyId = "item_details.config_sections";

    private static Harmony? _harmony;
    private static bool _patched;
    private static bool _canLocalizeChoices;

    internal static void Apply()
    {
        if (_patched) return;
        try
        {
            var open = AccessTools.Method(typeof(ModConfigWindow), "Open", new[] { typeof(LoadedMod) });
            if (open == null)
            {
                Log.Warn("ConfigSections.Apply", "未找到 ModConfigWindow.Open，分组标题可能停留在启动时的语言");
                return;
            }

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(open, prefix: new HarmonyMethod(typeof(ConfigSectionRefresh), nameof(OpenPrefix)));

            // 下拉框选项在面板里显示当前语言，写盘前要换回固定文字（见 ModSettings.Choice）
            var save = AccessTools.Method(typeof(ModConfig), "Save", Type.EmptyTypes);
            if (save != null)
                _harmony.Patch(save, prefix: new HarmonyMethod(typeof(ConfigSectionRefresh), nameof(SavePrefix)));
            else
                Log.Warn("ConfigSections.Apply", "未找到 ModConfig.Save，下拉框保持固定文字");
            _canLocalizeChoices = save != null;

            _patched = true;
            Log.Msg("ConfigSectionRefresh: patched ModConfigWindow.Open/ModConfig.Save");
        }
        catch (Exception ex)
        {
            Log.Error("ConfigSections.Apply", ex);
        }
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _patched = false;
        _canLocalizeChoices = false;
    }

    /// <summary>面板在 Open 里一次性拼装完，所以这里改完立刻生效。</summary>
    private static void OpenPrefix()
    {
        ModSettings.RefreshSections();
        // 没挂上写盘前的还原，就不换选项：否则配置文件里会存下当前语言的文字
        if (_canLocalizeChoices) ModSettings.LocalizeChoices();
    }

    private static void SavePrefix(ModConfig __instance) => ModSettings.RestoreChoices(__instance);
}
