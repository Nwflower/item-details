using System;

namespace ItemDetails;

/// <summary>
/// 把原版「模组效果: -性能: +16%」这类百分比行就地补上实际变更，例如：
///   -性能: +16%(+136毫升/天)
///   -效率: +16%(-3能量/天)
///   -质量: +40%(+20%吸引力)
///
/// 为什么就地改而不是另起一行：玩家要的是"每个加成后面直接看到影响"，而这三行正是
/// 原版 `MachineHelper.AddTooltipModuleBonus` 用 `RichTextBuilder.AddLine` 打出来的，
/// 我们已经在 AddLine 前缀里，接得住。
///
/// 数值由 <see cref="ModuleStatEffects"/> 在探针上用游戏函数做差分得出；
/// 任何一项得不出可靠结论就不补——宁可少一段，也不写错一个数。
/// </summary>
internal static class ModuleEffectAnnotation
{
    private static string _locale = "";
    private static string _perf = "";
    private static string _eff = "";
    private static string _qual = "";
    private static string _header = "";

    /// <summary>命中「加成行」时自己写完整行并返回 true（调用方丢弃原行）。</summary>
    internal static bool TryApply(ref string text, RichTextBuilder builder,
        RenderHandler.ColorPalette color, GameItem? item)
    {
        try
        {
            if (!ModSettings.ModuleEffects) return false;
            if (item == null || string.IsNullOrEmpty(text)) return false;
            // 只对机器生效：模块物品（MODULE_TAG）用的是另一套 BONUS_PERCENTAGE_* 标签，
            // 拿机器口径去算会得出莫名其妙的值（质量行甚至会去问水分收集器的档位）。
            if (!GameRead.IsTag(item, "machinery")) return false;
            if (text.IndexOf('%') < 0) return false;   // 只认「+16%」这种加成行

            EnsureLabels();

            char kind;
            if (_perf.Length > 0 && text.Contains(_perf, StringComparison.Ordinal)) kind = 'p';
            else if (_eff.Length > 0 && text.Contains(_eff, StringComparison.Ordinal)) kind = 'e';
            else if (_qual.Length > 0 && text.Contains(_qual, StringComparison.Ordinal)) kind = 'q';
            else return false;

            string note = BuildNote(kind, item);
            if (note.Length == 0) return false;

            // 原行照旧，注释用灰色；这里自己收行，所以调用方不要再走原 AddLine
            builder.Add(text, color);
            builder.Add(note, RenderHandler.ColorPalette.Gray);
            builder.NextLine();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 模块物品的空「模组效果：」表头：原版 `CreateModuleTooltip` 无条件打表头，
    /// 三项加成为 0 时后面什么也没有。只有在「确实没有任何效果」时才丢掉这一行，
    /// 有加成、有特殊效果（MODULE_EFFECT*）、有附加说明或卡滞提示时照常保留。
    /// </summary>
    internal static bool ShouldDrop(string text, GameItem? item)
    {
        try
        {
            if (!ModSettings.ModuleEffects) return false;
            if (item == null || string.IsNullOrEmpty(text)) return false;
            if (!GameRead.IsTag(item, "MODULE_TAG")) return false;

            EnsureLabels();
            if (_header.Length == 0) return false;
            if (!string.Equals(text.Trim(), _header, StringComparison.Ordinal)) return false;

            if (AnyNonZero(item, "BONUS_PERCENTAGE_PERFORMANCE_INT", "TEMP_PERCENTAGE_PERFORMANCE_INT")) return false;
            if (AnyNonZero(item, "BONUS_PERCENTAGE_EFFICIENCY_INT", "TEMP_PERCENTAGE_EFFICIENCY_INT")) return false;
            if (AnyNonZero(item, "BONUS_PERCENTAGE_QUALITY_INT", "TEMP_PERCENTAGE_QUALITY_INT")) return false;

            if (GameRead.IsTag(item, "MODULE_EFFECT1_TAG")) return false;
            if (GameRead.IsTag(item, "MODULE_EFFECT2_TAG")) return false;
            if (GameRead.IsTag(item, "ADDITIONAL_EFFECT_STRING")) return false;
            if (GameRead.IsTag(item, "MODULE_STUCK_TAG")) return false;

            return true;
        }
        catch { return false; }
    }

    private static bool AnyNonZero(GameItem item, string baseTag, string tempTag)
    {
        var b = GameRead.TagInt(item, baseTag);
        var t = GameRead.TagInt(item, tempTag);
        return (b is int bv && bv != 0) || (t is int tv && tv != 0);
    }

    private static string BuildNote(char kind, GameItem item)
    {
        string note = ModuleStatEffects.Note(kind, item);
        return note.Length == 0 ? "" : Wrap(note);
    }

    private static string Wrap(string body) => " (" + body + ")";

    /// <summary>三项加成的标签名走游戏本地化；换语言后自动重建。</summary>
    private static void EnsureLabels()
    {
        string locale = Loc.Code;
        if (locale == _locale && _perf.Length > 0) return;

        try { _perf = LocHelper.GetLocalizedMechanic("mech_module_stat_performance") ?? ""; } catch { _perf = ""; }
        try { _eff = LocHelper.GetLocalizedMechanic("mech_module_stat_efficiency") ?? ""; } catch { _eff = ""; }
        try { _qual = LocHelper.GetLocalizedMechanic("mech_module_stat_quality") ?? ""; } catch { _qual = ""; }
        try { _header = LocHelper.GetLocalizedMechanic("mech_module_effect_header") ?? ""; } catch { _header = ""; }
        _locale = locale;
    }
}
