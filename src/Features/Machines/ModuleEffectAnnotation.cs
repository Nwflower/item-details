using System;
using ItemDetails.Integration;

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

            string note = ModuleStatEffects.Note(kind, item, out var changes);

            // 原行照旧，注释用灰色；这里自己收行，所以调用方不要再走原 AddLine
            var output = new TooltipSections.Writer(builder, item, "machine-effect", TooltipGroupIds.Machine);
            output.Field("stat", kind == 'p' ? "performance" : kind == 'e' ? "efficiency" : "quality");
            if (note.Length > 0) output.Field("effectText", note);
            if (output.Capturing)
            {
                string tag = kind == 'p' ? "TOTAL_PERCENTAGE_PERFORMANCE_BONUS_INT"
                    : kind == 'e' ? "TOTAL_PERCENTAGE_POWER_BONUS_INT" : "TOTAL_PERCENTAGE_QUALITY_BONUS_INT";
                if (GameRead.TagInt(item, tag) is int bonus) output.Field("bonusPercent", bonus);
                if (changes != null) output.Field("changes", changes);
            }
            string annotation = note.Length > 0 ? " (" + note + ")" : "";
            output.RequireWidth(ProgressBarStyle.WidthForLine(ProgressBarStyle.VisibleLength(text)
                + ProgressBarStyle.VisibleLength(annotation)));
            string label = kind == 'p' ? _perf : kind == 'e' ? _eff : _qual;
            string tag2 = kind == 'p' ? "TOTAL_PERCENTAGE_PERFORMANCE_BONUS_INT"
                : kind == 'e' ? "TOTAL_PERCENTAGE_POWER_BONUS_INT" : "TOTAL_PERCENTAGE_QUALITY_BONUS_INT";
            var cols = ChipRender(text, label, (GameRead.TagInt(item, tag2) ?? 0) >= 0, annotation);
            if (cols != null)
            {
                output.RequireWidth(ProgressBarStyle.WidthForLine(ProgressBarStyle.VisibleLength(text)
                    + ProgressBarStyle.VisibleLength(annotation)) + 4);
                output.AddRendered(text, color, cols.Value.Main);
                if (annotation.Length > 0) output.AddRendered(annotation, RenderHandler.ColorPalette.Gray, cols.Value.Note);
            }
            else
            {
                output.Add(text, color);
                if (annotation.Length > 0) output.Add(annotation, RenderHandler.ColorPalette.Gray);
            }
            output.NextLine();
            output.Emit();
            return true;
        }
        catch { return false; }
    }


    /// <summary>标签内侧左边距（em）：让文字避开左侧亮条。</summary>
    private const float ChipPadEm = 0.3f;
    private const float ColumnGapEm = 0.7f;

    /// <summary>
    /// 把「-效率: +32% (系数 0.5)」拆成三列：色块标签 / 数值 / 其余（系数与我们的附加信息）。
    /// 返回正文片段与注释片段各自的渲染写法；找不到标签或色块不可用时返回 null，调用方照原样写。
    /// 列位置按三个标签里最宽的算，几行之间对得齐；项目符号与冒号一并去掉，色块本身就是行首标记。
    /// </summary>
    private static (string Main, string Note)? ChipRender(string text, string label, bool positive, string annotation)
    {
        if (label.Length == 0 || !LabelChips.Available) return null;
        int at = text.IndexOf(label, StringComparison.Ordinal);
        if (at < 0) return null;

        int rest = at + label.Length;
        while (rest < text.Length && (text[rest] == ':' || text[rest] == '：' || text[rest] == ' ')) rest++;
        string tail = text.Substring(rest);
        int pct = tail.IndexOf('%');
        string value = pct >= 0 ? tail.Substring(0, pct + 1) : tail;
        string extra = pct >= 0 ? tail.Substring(pct + 1).Trim() : "";
        // 有我们的附加信息时不再显示原版的「系数」：附加信息更详细，两者并排只是重复
        if (annotation.Length > 0) extra = "";

        // 列宽：色块取三个标签里最宽的；第三列起点不能压住这一行自己的数值
        float chip = 0f;
        foreach (var l in new[] { _perf, _eff, _qual })
            if (l.Length > 0) chip = Math.Max(chip, WaterColumns.TextEm(" " + l + " ") + ChipPadEm);
        float col2 = chip + ColumnGapEm;
        float col3 = Math.Max(col2 + WaterColumns.TextEm("+100%"), col2 + WaterColumns.TextEm(value)) + ColumnGapEm;

        string fore = positive ? LabelChips.PositiveText : LabelChips.NegativeText;
        string valueColor = positive ? LabelChips.ValuePositive : LabelChips.ValueNegative;
        string chipText = LabelChips.Wrap(
            "<color=" + fore + "> <space=" + ChipPadEm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            + "em>" + label + " </color>", positive);

        string main = chipText + Pos(col2) + "<color=" + valueColor + ">" + value + "</color>";
        if (extra.Length > 0 || annotation.Length > 0) main += Pos(col3) + extra;
        // 注释原文以空格开头；有「系数」之类的内容时与它隔一格，否则直接从第三列起笔
        string note = (extra.Length > 0 ? " " : "") + annotation.TrimStart();
        return (main, note);
    }

    private static string Pos(float em)
        => "<pos=" + em.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "em>";

    /// <summary>
    /// 机器上的「模组效果:」表头：三项加成已经各带一块色标，表头多余，整行丢掉。
    /// 只认原版 <c>mech_effect_from_module</c> 这一句，且只对机器生效。
    /// </summary>
    internal static bool ShouldDropMachineHeader(string text, GameItem? item)
    {
        try
        {
            if (!ModSettings.ModuleEffects) return false;
            if (item == null || string.IsNullOrEmpty(text)) return false;
            if (!GameRead.IsTag(item, "machinery")) return false;
            string header = LocHelper.GetLocalizedMechanic("mech_effect_from_module") ?? "";
            return header.Length > 0 && string.Equals(text.Trim(), header.Trim(), StringComparison.Ordinal);
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
