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

    private static bool _canLocalizeChoices;

    internal static void Install(PatchScope scope)
    {
        try
        {
            var open = AccessTools.Method(typeof(ModConfigWindow), "Open", new[] { typeof(LoadedMod) });
            if (open == null)
            {
                Log.Warn("ConfigSections.Apply", "未找到 ModConfigWindow.Open，分组标题可能停留在启动时的语言");
                return;
            }

            scope.Patch(open,
                prefix: new HarmonyMethod(typeof(ConfigSectionRefresh), nameof(OpenPrefix)),
                postfix: new HarmonyMethod(typeof(ConfigSectionRefresh), nameof(OpenPostfix)));

            // ResetWidgets 使用 SetIsOnWithoutNotify，不触发 OnToggle；重置后单独同步可见性。
            var reset = AccessTools.Method(typeof(ModConfigWindow), "ResetWidgets",
                new[] { typeof(CustomUIWindow), typeof(ModConfig) });
            if (reset != null)
                scope.Patch(reset, postfix: new HarmonyMethod(typeof(ConfigSectionRefresh), nameof(ResetPostfix)));

            // 下拉框选项在面板里显示当前语言，写盘前要换回固定文字（见 ModSettings.Choice）
            var save = AccessTools.Method(typeof(ModConfig), "Save", Type.EmptyTypes);
            if (save != null)
                scope.Patch(save, prefix: new HarmonyMethod(typeof(ConfigSectionRefresh), nameof(SavePrefix)));
            else
                Log.Warn("ConfigSections.Apply", "未找到 ModConfig.Save，下拉框保持固定文字");
            _canLocalizeChoices = save != null;

            Log.Msg("ConfigSectionRefresh: patched ModConfigWindow.Open/ModConfig.Save");
        }
        catch (Exception ex)
        {
            Log.Error("ConfigSections.Apply", ex);
        }
        scope.OnDispose(Remove);
    }

    internal static void Remove()
    {
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

    private static void OpenPostfix(CustomUIWindow __result)
    {
        if (__result == null || __result.id != "mod_config:item_details") return;
        try
        {
            // 只切换显示，不提交设置；功能值仍由游戏的「应用」按钮统一保存。
            __result.Get("show_info_enhance")?.OnToggle(_ => RefreshInfoVisibility(__result));
            RefreshInfoVisibility(__result);
        }
        catch (Exception ex) { Log.Warn("ConfigSections.Visibility", ex); }
    }

    private static void ResetPostfix(CustomUIWindow __0)
    {
        try { RefreshInfoVisibility(__0); }
        catch (Exception ex) { Log.Warn("ConfigSections.Visibility", ex); }
    }

    /// <summary>
    /// 0.46D ModConfigWindow.Open 为每组依次生成标题行、分隔线、条目行与空隙。
    /// 一起隐藏这些完整行，避免只隐藏控件留下大片空白；不删除控件或修改配置条目，
    /// 因而关闭再开启总开关、应用和重置都能保留游戏原有语义。
    /// </summary>
    internal static void RefreshInfoVisibility(CustomUIWindow window)
    {
        if (window == null || !window.IsAlive || window.id != "mod_config:item_details") return;
        var config = ModConfig.Find("item_details");
        var first = config?.Get("info_price_modifiers");
        if (first == null) return;
        var header = window.Get("section_" + first.Section)?.root;
        var last = window.Get("info_manufacturer_inline")?.root;
        var master = window.Get("show_info_enhance");
        if (header == null || last == null || master == null) return;
        var headerRow = header.transform.parent;
        var lastRow = last.transform.parent;
        if (headerRow == null || lastRow == null || headerRow.parent != lastRow.parent) return;
        var parent = headerRow.parent;
        if (parent == null) return;
        int start = headerRow.GetSiblingIndex(), end = lastRow.GetSiblingIndex();
        if (end < start) return;
        if (end + 1 < parent.childCount && parent.GetChild(end + 1).name == "space") end++;
        bool visible = master.GetBool();
        for (int i = start; i <= end; i++) parent.GetChild(i).gameObject.SetActive(visible);
    }
}
