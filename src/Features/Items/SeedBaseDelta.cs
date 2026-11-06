using System;
using System.Text.RegularExpressions;
using HarmonyLib;
using ItemDetails.Integration;

namespace ItemDetails;

/// <summary>
/// 种子的「需求工作量 / 收获数量 / 耗水率」三行：原版写成「15 (Base: 23)」，
/// 这里学机器耗能行的写法——「需求工作量：23 → 15」，前面的基础值灰色，后面的当前值比基础好画绿、差画红，
/// 两者相同就只写当前值，不再带「(Base: …)」。
/// 规范文本保持原版两段拼起来的样子（第三方读到的不变），只有渲染层换成新写法。
/// </summary>
internal static class SeedBaseDelta
{
    private static readonly Regex Number = new(@"-?\d+", RegexOptions.Compiled);

    // 三行的出现顺序固定：工作量（越低越好）、收获（越高越好）、耗水（越低越好）
    private static readonly bool[] LowerIsBetter = { true, false, true };
    private static int _index;

    internal static void Install(PatchScope scope)
    {
        scope.Patch(AccessTools.Method(typeof(HydroponicHelper), "CreateSeedItemTooltip",
                new[] { typeof(RichTextBuilder), typeof(GameItem) }),
            prefix: new HarmonyMethod(typeof(SeedBaseDelta), nameof(Reset)));
        scope.Patch(AccessTools.Method(typeof(HydroponicHelper), "AddSeedStatLine"),
            prefix: new HarmonyMethod(typeof(SeedBaseDelta), nameof(Prefix)) { priority = Priority.Last });
    }

    private static void Reset() => _index = 0;

    private static bool Prefix(RichTextBuilder builder, string effectiveText, int baseValue, bool showBase)
    {
        int slot = _index++;
        try
        {
            if (!ModSettings.TooltipFormatting || !showBase || builder == null || effectiveText == null) return true;
            Match match = Number.Match(effectiveText);
            if (!match.Success || !int.TryParse(match.Value, out int current)) return true;

            var color = RenderHandler.ColorPalette.LightCyan;
            if (current == baseValue)
            {
                // 与基础值相同：只写当前值
                ModEntry.AddLine(builder, effectiveText, color);
                return false;
            }

            bool lower = slot >= 0 && slot < LowerIsBetter.Length && LowerIsBetter[slot];
            bool better = lower ? current < baseValue : current > baseValue;
            string render = effectiveText.Substring(0, match.Index)
                + RenderHandler.ApplyRichTextColorTag(baseValue + " → ", RenderHandler.ColorPalette.Gray)
                + RenderHandler.ApplyRichTextColorTag(match.Value,
                    better ? RenderHandler.ColorPalette.LightGreen : RenderHandler.ColorPalette.LightRed)
                + effectiveText.Substring(match.Index + match.Length);
            // 规范文本照原版：「效果文字 (Base: n)」
            RenderSlices.AddLine(builder, effectiveText + " (Base: " + baseValue + ")", color, render);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("SeedBaseDelta", ex);
            return true;
        }
    }
}
