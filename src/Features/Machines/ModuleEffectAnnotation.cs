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

    /// <summary>说明被别处删去某一句后（如自学习强化机去掉概率句），剩下的仍是说明的前缀。</summary>
    private static bool IsFlavorPart(string text, string? flavor)
    {
        if (string.IsNullOrEmpty(flavor)) return false;
        string core = text.TrimEnd('。', '.', ' ');
        return core.Length >= 8 && flavor!.Trim().StartsWith(core, StringComparison.Ordinal);
    }

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
            // 机器自己的说明（自学习强化机以「效率…」开头且带百分号）不是加成行
            string trimmed = text.Trim();
            if (trimmed.Length > 0 && (string.Equals(trimmed, item.flavorText?.Trim(), StringComparison.Ordinal)
                || string.Equals(trimmed, item.shortDescription?.Trim(), StringComparison.Ordinal)
                || IsFlavorPart(trimmed, item.flavorText))) return false;

            EnsureLabels();

            // 只认以标签开头的加成行（前面可有项目符号）：描述、背景故事里碰巧提到「性能」和百分号的句子不算，
            // 否则整句话会被拆成色块（自学习强化机的说明就中过招）
            string head = text;
            int lead = 0;
            while (lead < head.Length && !char.IsLetterOrDigit(head[lead])) lead++;
            head = head.Substring(lead);
            char kind;
            if (_perf.Length > 0 && head.StartsWith(_perf, StringComparison.Ordinal)) kind = 'p';
            else if (_eff.Length > 0 && head.StartsWith(_eff, StringComparison.Ordinal)) kind = 'e';
            else if (_qual.Length > 0 && head.StartsWith(_qual, StringComparison.Ordinal)) kind = 'q';
            else return false;

            string note = ModuleStatEffects.Note(kind, item, out var changes);
            // 熔炉的冶炼预览（纯度档位、再加多少升档）作为质量行的第三列，不再另起一行
            (int? Ingots, int? Purity, int Gain, int? NextBonus)? furnace = null;
            if (kind == 'q' && note.Length == 0 && LabelChips.Available
                && string.Equals(item.identifier, "furnace", StringComparison.Ordinal))
            {
                var ctx = MachineInterior.ReadContext(item);
                string? preview = ctx == null ? null : FurnaceQuality.Note(ctx, out furnace, FurnaceQuality.Part.Gain);
                if (!string.IsNullOrEmpty(preview)) note = preview!;
            }

            // 原行照旧，注释用灰色；这里自己收行，所以调用方不要再走原 AddLine
            var output = new TooltipSections.Writer(builder, item, "machine-effect", TooltipGroupIds.Machine);
            output.Field("stat", kind == 'p' ? "performance" : kind == 'e' ? "efficiency" : "quality");
            if (note.Length > 0) output.Field("effectText", note);
            if (furnace.HasValue) output.Field("furnace", furnace.Value);
            if (output.Capturing)
            {
                string tag = kind == 'p' ? "TOTAL_PERCENTAGE_PERFORMANCE_BONUS_INT"
                    : kind == 'e' ? "TOTAL_PERCENTAGE_POWER_BONUS_INT" : "TOTAL_PERCENTAGE_QUALITY_BONUS_INT";
                if (GameRead.TagInt(item, tag) is int bonus) output.Field("bonusPercent", bonus);
                if (changes != null) output.Field("changes", changes);
            }
            string annotation = note.Length > 0 ? " (" + note + ")" : "";
            // 净水器的质量：第三列直接用它自己的「纯度上限」（原版文案与数值），比「再加多少能到纯水」更直观。
            // 原版单独那行只在色块模式下才被丢，所以纯度也只在色块可用时并进来；
            // 规范文本同时带注释与纯度上限，渲染层的第三列再取舍，读正文的一方信息不少
            string? purity = kind == 'q' && LabelChips.Available ? PurityText(item) : null;
            if (purity != null)
                annotation = " (" + (note.Length > 0 ? note + "; " : "") + purity + ")";
            // 第三列的显示文字：列已经靠缩进分隔，不再加括号
            string shown = purity ?? note;
            output.RequireWidth(ProgressBarStyle.WidthForLine(ProgressBarStyle.VisibleLength(text)
                + ProgressBarStyle.VisibleLength(annotation)));
            string label = kind == 'p' ? _perf : kind == 'e' ? _eff : _qual;
            string tag2 = kind == 'p' ? "TOTAL_PERCENTAGE_PERFORMANCE_BONUS_INT"
                : kind == 'e' ? "TOTAL_PERCENTAGE_POWER_BONUS_INT" : "TOTAL_PERCENTAGE_QUALITY_BONUS_INT";
            var cols = ChipRender(text, label, (GameRead.TagInt(item, tag2) ?? 0) >= 0, shown);
            if (cols != null)
            {
                output.RequireWidth(ProgressBarStyle.WidthForLine(ProgressBarStyle.VisibleLength(text)
                    + ProgressBarStyle.VisibleLength(annotation)) + 4);
                output.AddRendered(text, color, cols.Value.Main);
                string noteRender = cols.Value.Note;
                if (kind == 'q' && SelfLearningMachine.IsMachine(item))
                    noteRender = SelfLearningMachine.WithTrainingToken(noteRender, shown);
                if (annotation.Length > 0) output.AddRendered(annotation, RenderHandler.ColorPalette.Gray, noteRender);
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
    private static (string Main, string Note)? ChipRender(string text, string label, bool positive, string shown)
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
        if (shown.Length > 0) extra = "";

        // 列宽：色块取三个标签里最宽的；第三列起点不能压住这一行自己的数值
        float col2 = ColumnEm();
        float col3 = Math.Max(col2 + WaterColumns.TextEm("+100%"), col2 + WaterColumns.TextEm(value)) + ColumnGapEm;

        string fore = positive ? LabelChips.PositiveText : LabelChips.NegativeText;
        string valueColor = positive ? LabelChips.ValuePositive : LabelChips.ValueNegative;
        string chipText = ChipMarkup(label, positive ? '+' : '-', fore);

        string main = chipText + Pos(col2) + "<color=" + valueColor + ">" + value + "</color>";
        if (extra.Length > 0 || shown.Length > 0) main += Pos(col3) + extra;
        // 注释原文以空格开头；有「系数」之类的内容时与它隔一格，否则直接从第三列起笔
        string note = (extra.Length > 0 ? " " : "") + shown;
        return (main, note);
    }

    /// <summary>色块标签的渲染写法：链接包住（供矩形层定位）+ 内边距 + 同色系亮字。</summary>
    internal static string ChipMarkup(string label, char kind, string foreColor)
        => LabelChips.Wrap("<color=" + foreColor + "> <space="
            + ChipPadEm.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            + "em>" + label + " </color>", kind);

    /// <summary>色块标签占的宽度（em，含内边距）。</summary>
    internal static float ChipEm(string label) => WaterColumns.TextEm(" " + label + " ") + ChipPadEm;

    /// <summary>第二列起点：三个加成标签里最宽的色块加列间距。</summary>
    internal static float ColumnEm()
    {
        EnsureLabels();
        float chip = 0f;
        foreach (var l in new[] { _perf, _eff, _qual })
            if (l.Length > 0) chip = Math.Max(chip, ChipEm(l));
        return chip + ColumnGapEm;
    }

    internal static string Pos(float em)
        => "<pos=" + em.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "em>";

    /// <summary>净水器的原版「纯度上限：99.65%」整句；不是净水器或坏了时为 null。</summary>
    private static string? PurityText(GameItem item)
    {
        try
        {
            if (!string.Equals(item.identifier, "water_purifier", StringComparison.Ordinal)) return null;
            if (GameRead.IsTag(item, "BROKEN_MACHINE_TAG")) return null;
            string cap = ((double)MachinePurifier.GetMaxPurityCap(MachineryHelper.GetCurrentQualityBonus(item)) / 100.0)
                .ToString("0.##");
            return LocHelper.GetLocalizedMechanicOrDefault("mech_purifier_tooltip_max_purity", "Max Purity: {0}%", cap);
        }
        catch { return null; }
    }

    /// <summary>
    /// 净水器原版单独一行的「纯度上限」：质量色块那行的第三列已经写了同一句，这里去掉重复。
    /// 只在质量行确实会出现（有模组加成且质量加成不为零）、色块可用时才去掉。
    /// </summary>
    internal static bool ShouldDropPurityLine(string text, GameItem? item)
    {
        try
        {
            if (!ModSettings.ModuleEffects || !LabelChips.Available) return false;
            if (item == null || string.IsNullOrEmpty(text)) return false;
            if (!GameRead.IsTag(item, "machinery")) return false;
            if (!GameRead.TagBool(item, "IS_RECEIVING_BONUS_FROM_MODULE_BOOL")) return false;
            if ((GameRead.TagInt(item, "TOTAL_PERCENTAGE_QUALITY_BONUS_INT") ?? 0) == 0) return false;
            string? purity = PurityText(item);
            return purity != null && string.Equals(text.Trim(), purity.Trim(), StringComparison.Ordinal);
        }
        catch { return false; }
    }

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
