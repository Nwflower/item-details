using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 设置面板每次打开前，把分组标题和下拉框选项换成当前语言；本模组配置写盘前再把选项换回固定文字。
/// 面板打开后按依赖表切换条目显隐，并给底色不透明度滑条补一个数值（<see cref="ConfigSliderValue"/>）。
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

    private const string WindowId = "mod_config:item_details";
    private const string Master = "show_info_enhance";

    private static readonly Func<CustomUIElement, bool> Checked = e => e.GetBool();
    /// <summary>下拉框序号 0 是「关闭」（见 <see cref="ModSettings.LiquidMode"/>）；面板按序号读写，与选项语言无关。</summary>
    private static readonly Func<CustomUIElement, bool> NotOff = e => e.GetIndex() != (int)ModSettings.LiquidMode.Off;

    /// <summary>
    /// 条目显隐依赖：子项键 → 直接父控件键 → 父控件取什么值时子项显示。父项自己被隐藏时子项也隐藏，
    /// 所以链式依赖（总开关 → 分类标签 → 淡化配色）只写直接父项。
    /// 总开关这一组以 ModSettings 里实际取值受 InfoEnhance 控制的访问器为准；淡化配色与底色不透明度
    /// 经由各自的上级开关受控。<c>water_advice</c> 有意独立，不在表里。
    /// </summary>
    private static readonly (string Child, string Parent, Func<CustomUIElement, bool> Shows)[] Dependencies =
    {
        ("show_cost_performance", Master, Checked),
        ("show_container_contents", Master, Checked),
        ("tooltip_text_formatting", Master, Checked),
        ("show_type_badge", Master, Checked),
        ("mute_type_badge_colors", "show_type_badge", Checked),
        ("show_container_name_display", Master, Checked),
        ("container_name_bg_opacity", "show_container_name_display", Checked),
        ("color_liquid_components", "liquid_analysis", NotOff),
        ("result_prefill_price", "show_result_prefill", Checked),
        ("info_price_modifiers", Master, Checked),
        ("info_black_market", Master, Checked),
        ("info_lottery", Master, Checked),
        ("info_brewing", Master, Checked),
        ("info_meat", Master, Checked),
        ("info_machines", Master, Checked),
        ("info_batteries", Master, Checked),
        ("info_progress_bar", Master, Checked),
        ("info_container_units", Master, Checked),
        ("info_gene_roman", Master, Checked),
        ("info_manufacturer_inline", Master, Checked),
    };

    private static void OpenPostfix(CustomUIWindow __result)
    {
        if (__result == null || __result.id != WindowId) return;
        try
        {
            // 只切换显示，不提交设置；功能值仍由游戏的「应用」按钮统一保存。
            var parents = new HashSet<string>();
            foreach (var (_, parent, _) in Dependencies)
            {
                if (!parents.Add(parent)) continue;
                var control = __result.Get(parent);
                if (control?.toggle != null) control.OnToggle(_ => RefreshVisibility(__result));
                else if (control?.dropdown != null) control.OnDropdown(_ => RefreshVisibility(__result));
            }
            RefreshVisibility(__result);
            ConfigSliderValue.Attach(__result);
        }
        catch (Exception ex) { Log.Warn("ConfigSections.Visibility", ex); }
    }

    private static void ResetPostfix(CustomUIWindow __0)
    {
        if (__0 == null || __0.id != WindowId) return;
        try
        {
            RefreshVisibility(__0);
            ConfigSliderValue.Refresh();
        }
        catch (Exception ex) { Log.Warn("ConfigSections.Visibility", ex); }
    }

    /// <summary>
    /// 0.46D ModConfigWindow.Open 为每组依次生成标题行、分隔线、条目行与空隙，都是滚动内容的直接子物体。
    /// 按 <see cref="Dependencies"/> 逐行显隐；一组条目全部隐藏时，标题行、分隔线与空隙一起隐藏，
    /// 不留空白。不删除控件、不改配置条目，所以关闭再开启、应用和重置都保留游戏原有语义。
    /// 某一组的结构不是预期样子时整组不动并记一条日志。
    /// </summary>
    internal static void RefreshVisibility(CustomUIWindow window)
    {
        if (window == null || !window.IsAlive || window.id != WindowId) return;
        var config = ModConfig.Find("item_details");
        if (config == null) return;

        var sections = new List<string>();
        foreach (var entry in config.Entries)
            if (!sections.Contains(entry.Section)) sections.Add(entry.Section);

        foreach (string section in sections)
        {
            var header = window.Get("section_" + section);
            var headerRow = header?.root != null ? header.root.transform.parent : null;
            var content = headerRow != null ? headerRow.parent : null;
            if (content == null)
            {
                Log.Warn("ConfigSections.Layout", $"分组 {section} 的标题行不是预期结构，保持原样");
                continue;
            }

            // 先核对：每个条目都是同一滚动内容下的一整行，记下最后一行的位置找空隙。
            int last = -1;
            bool expected = true;
            foreach (var entry in config.Entries)
            {
                if (entry.Section != section) continue;
                var control = window.Get(entry.Key);
                var row = control?.root != null ? control.root.transform.parent : null;
                if (row == null || row.parent != content) { expected = false; break; }
                last = Math.Max(last, row.GetSiblingIndex());
            }
            if (!expected || last < 0)
            {
                Log.Warn("ConfigSections.Layout", $"分组 {section} 的条目行不是预期结构，保持原样");
                continue;
            }

            bool any = false;
            foreach (var entry in config.Entries)
            {
                if (entry.Section != section) continue;
                bool shown = Shown(window, entry.Key, 0);
                window.Get(entry.Key)!.root.transform.parent!.gameObject.SetActive(shown);
                any |= shown;
            }

            headerRow!.gameObject.SetActive(any);
            int next = headerRow.GetSiblingIndex() + 1;
            if (next < content.childCount && content.GetChild(next).name.StartsWith("separator", StringComparison.Ordinal))
                content.GetChild(next).gameObject.SetActive(any);
            if (last + 1 < content.childCount && content.GetChild(last + 1).name == "space")
                content.GetChild(last + 1).gameObject.SetActive(any);
        }
    }

    /// <summary>沿依赖链判定条目是否显示；父控件不在面板上时按显示处理（深度上限防止表写成环）。</summary>
    private static bool Shown(CustomUIWindow window, string key, int depth)
    {
        foreach (var (child, parent, shows) in Dependencies)
        {
            if (child != key) continue;
            var control = window.Get(parent);
            if (control == null || depth > Dependencies.Length) return true;
            return shows(control) && Shown(window, parent, depth + 1);
        }
        return true;
    }
}
