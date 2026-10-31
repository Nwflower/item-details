using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 瑕疵/状态的价值影响。
///
/// 原版 tooltip 只写「凹陷」「锈蚀」，不告诉你影响多少。本模块读
/// ItemFeature.realCondition（ItemCondition）的 modValue，补上数值。
///
/// 依据（正式版 0.46D 源码）：
///   ItemCondition.modValue  —— CreateDented() = -5、CreateRusted() = -40
///   **单位是百分比**：ItemFeature.GetFormattedActualModifier() 返回
///      " (" + GetActualValueModifier() + "%)"，而 GetActualValueModifier()
///      在 useCondition 时正是返回 realCondition.modValue。
///   例外：isTransformative 的条件走 GetModValueFormated()，输出不带 %，按点数显示。
///   ItemCondition.display   —— 已本地化的显示名（如「凹陷」）
///   ItemFeature.realCondition —— 条件挂载点；水质/锭纯度/装备状况/化学品纯度均走此路径
///
/// 文本拼装放在这里（而不是调用方）：金额符号、百分号与红绿配色都取决于
/// 条件本身，散到 ModEntry 里只会再多一段逐行判断。
///
/// 只读；逐项 try/catch。
/// </summary>
internal static class ConditionEffects
{
    private const RenderHandler.ColorPalette Penalty = RenderHandler.ColorPalette.LightRed;
    private const RenderHandler.ColorPalette Bonus = RenderHandler.ColorPalette.LightGreen;

    /// <summary>最多展示几条，避免 tooltip 过长。</summary>
    private const int MaxLines = 4;

    /// <param name="skipRevealed">FeatureReveal 开着时为 true：它已把伪装特性的真实状态和影响写成一行。</param>
    internal static List<TooltipLine> Collect(GameItem? item, bool skipRevealed = false)
    {
        var lines = new List<TooltipLine>();
        if (item == null) return lines;

        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in FeatureScan.All(item))
            {
                if (lines.Count >= MaxLines) break;
                if (skipRevealed && FeatureReveal.Reveals(f)) continue;
                TryOne(f, lines, seen);
            }

            if (lines.Count > 0) Log.MarkOk("ConditionEffects");
        }
        catch (Exception ex)
        {
            Log.Warn("ConditionEffects", ex);
        }
        return lines;
    }

    /// <summary>
    /// 条件对价格的展示数值：<c>modValueDisplay</c> 优先，其次 <c>modValue</c>。
    /// 与游戏 ItemCondition.GetModValueFormated 的取法一致——转换型条件
    /// （赝品香烟 -50、正品邮票 +100 等）只写 modValueDisplay，modValue 为 0。
    /// </summary>
    internal static int DisplayAmount(ItemCondition cond)
        => cond.modValueDisplay != 0 ? cond.modValueDisplay : cond.modValue;

    /// <summary>转换型条件按点数显示（无 %），其余按百分比显示。</summary>
    internal static string Suffix(ItemCondition cond) => cond.isTransformative ? "" : "%";

    private static void TryOne(ItemFeature f, List<TooltipLine> lines, HashSet<string> seen)
    {
        try
        {
            // 已禁用的特性不参与计价，跳过
            if (f.isDisabled) return;

            // 原版 LabelerHelper 已经展示的特性（已揭露或非隐藏），其百分比已由
            // LabelImpact 追加到标签行，这里不再另起“状态”行重复一次。
            bool shownByLabeler;
            try { shownByLabeler = f.IsExposed() || !f.GetPublicHidden(); }
            catch { shownByLabeler = false; }
            if (shownByLabeler) return;

            ItemCondition? cond = f.realCondition;
            if (cond == null) return;

            string display = cond.display ?? "";
            if (display.Length == 0) return;

            // modValueDisplay 为展示用的覆盖值；为 0 时回落到 modValue
            int amount = DisplayAmount(cond);
            if (amount == 0) return;

            // 同一条件可能被多个特性引用，去重
            if (!seen.Add(display)) return;

            // isTransformative 走点数，其余走百分比（与游戏 GetFormattedActualModifier 一致）
            string suffix = Suffix(cond);
            string signed = amount > 0 ? $"+{amount}{suffix}" : $"{amount}{suffix}";
            string text = Loc.T("condition.line", "Condition: {0} {1}", display, signed);

            lines.Add(new TooltipLine(text, amount < 0 ? Penalty : Bonus));
            Log.MarkOk("ConditionEffects." + (cond.identifier ?? "?"));
        }
        catch (Exception ex)
        {
            Log.Warn("ConditionEffects.One", ex);
        }
    }
}
