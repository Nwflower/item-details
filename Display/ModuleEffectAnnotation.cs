using System;

namespace ItemDetails;

/// <summary>
/// 把原版「模组效果: -性能: +16%」这类百分比行就地补上实际变更，例如：
///   -性能: +16%(+136毫升/天)
///   -效率: +16%(-0能量/天)
///   -质量: +8%(+2品质)
///
/// 为什么就地改而不是另起一行：玩家要的是"每个加成后面直接看到影响"，而这三行正是
/// 原版 `MachineHelper.AddTooltipModuleBonus` 用 `RichTextBuilder.AddLine` 打出来的，
/// 我们已经在 AddLine 前缀里，接得住。
///
/// 数值全部来自游戏写在机器物品上的标签，**不认识任何具体机器**：
///   · 性能：BASE_PROCESSING_SPEED_TAG → CURRENT_PROCESSING_SPEED_TAG；
///   · 效率：MACHINERY_DRAWN_POWER_BASE → machinery_drawn_power（游戏已按 +效率% 算好）；
///   · 质量：TOTAL_PERCENTAGE_QUALITY_BONUS_INT / MACHINE_QUALITY_RATIO_INT
///           （与 `ModuleHelper.GetAjustedQuality` 同一整数除法，没有比率标签的机器不补）。
/// 任何一项读不到就不补——宁可少一段，也不写错一个数。
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

    /// <summary>
    /// 按标签推导实际影响，**不认识任何机器 id**：
    ///   性能 → BASE_PROCESSING_SPEED_TAG → CURRENT_PROCESSING_SPEED_TAG（速度差值）
    ///   效率 → MACHINERY_DRAWN_POWER_BASE → machinery_drawn_power（耗电差值）
    ///   质量 → TOTAL_PERCENTAGE_QUALITY_BONUS_INT / MACHINE_QUALITY_RATIO_INT
    ///          （与游戏 ModuleHelper.GetAjustedQuality 同一整数除法；机器没有比率标签就不补）
    /// 质量在游戏里没有统一模型：水分是「加成+50」分档、熔炉是「加成/100」提纯度、打印机另算，
    /// 唯一通用的只有上面这个折算值，所以没有比率的机器保留原版百分比。
    /// </summary>
    private static string BuildNote(char kind, GameItem item)
    {
        bool zh = Locales.IsChinese;

        switch (kind)
        {
            case 'p':
                var speedBase = GameRead.TagInt(item, "BASE_PROCESSING_SPEED_TAG");
                var speedNow = GameRead.TagInt(item, "CURRENT_PROCESSING_SPEED_TAG");
                if (speedBase is int sb && speedNow is int sn && sb > 0 && sn > 0)
                    return Wrap(Signed(sn - sb) + (zh ? "速度" : " speed"));
                return "";

            case 'e':
                var powerBase = GameRead.TagInt(item, "MACHINERY_DRAWN_POWER_BASE");
                var powerNow = GameRead.TagInt(item, "machinery_drawn_power");
                if (powerBase is int pb && powerNow is int pn)
                    return Wrap(Signed(pn - pb) + (zh ? "能量/天" : " power/day"));
                return "";

            case 'q':
                var qualityBonus = GameRead.TagInt(item, "TOTAL_PERCENTAGE_QUALITY_BONUS_INT");
                var qualityRatio = GameRead.TagInt(item, "MACHINE_QUALITY_RATIO_INT");
                if (qualityBonus is int qb && qualityRatio is int qr && qr > 0)
                    return Wrap(Signed(qb / qr) + (zh ? "品质" : " quality"));
                return "";
        }

        return "";
    }


    /// <summary>零变化也写成负数（-0），和玩家「效率+16% 应该减耗」的直觉一致。</summary>
    private static string Signed(int delta) => delta <= 0 ? "-" + (-delta) : "+" + delta;

    private static string Wrap(string body) => "(" + body + ")";

    /// <summary>三项加成的标签名走游戏本地化；换语言后自动重建。</summary>
    private static void EnsureLabels()
    {
        string locale = Locales.Code;
        if (locale == _locale && _perf.Length > 0) return;

        try { _perf = LocHelper.GetLocalizedMechanic("mech_module_stat_performance") ?? ""; } catch { _perf = ""; }
        try { _eff = LocHelper.GetLocalizedMechanic("mech_module_stat_efficiency") ?? ""; } catch { _eff = ""; }
        try { _qual = LocHelper.GetLocalizedMechanic("mech_module_stat_quality") ?? ""; } catch { _qual = ""; }
        try { _header = LocHelper.GetLocalizedMechanic("mech_module_effect_header") ?? ""; } catch { _header = ""; }
        _locale = locale;
    }
}
