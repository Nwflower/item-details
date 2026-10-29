using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// P1 隐藏特性揭示：物品的 ItemFeature 可能有"伪装条件(fakeCondition)"与"真实条件(realCondition)"。
/// 原版只在特性被揭露(IsExposed)后才显示真实值；本模块**无条件揭示**差异，
/// 让玩家在鉴定前就知道"这物品其实是假的"。
///
/// 覆盖：真假药品/香烟/彩票号码/纯度锭等一切用 ItemFeature 伪装的物品。
/// 只读；逐项 try/catch。
///
/// 依据：正式版 0.46D ItemFeature.GetBaseFakeDisplay()/GetBaseRealDisplay()/
/// GetActualValueModifier()/GetFakeValueModifier()/IsExposed()。
/// </summary>
internal static class FeatureReveal
{
    private const RenderHandler.ColorPalette Color = RenderHandler.ColorPalette.LightRed;

    internal static List<TooltipLine> Collect(GameItem? item, bool zh)
    {
        var lines = new List<TooltipLine>();
        if (item == null) return lines;

        try
        {
            foreach (var f in FeatureScan.All(item))
                TryOne(f, zh, lines);

            if (lines.Count > 0) Log.MarkOk("FeatureReveal");
        }
        catch (Exception ex)
        {
            Log.Warn("FeatureReveal", ex);
        }
        return lines;
    }

    private static void TryOne(ItemFeature f, bool zh, List<TooltipLine> lines)
    {
        try
        {
            if (!f.useCondition) return;   // 没有条件机制的特性跳过

            string fake = SafeCall(() => f.GetBaseFakeDisplay());
            string real = SafeCall(() => f.GetBaseRealDisplay());
            if (string.IsNullOrWhiteSpace(real)) return;

            int realMod = 0, fakeMod = 0;
            try { realMod = f.GetActualValueModifier(); } catch { }
            try { fakeMod = f.GetFakeValueModifier(); } catch { }

            bool realDiffers = !string.IsNullOrWhiteSpace(fake) && fake != real;
            bool modDiffers = realMod != fakeMod;

            // 伪装与真实一致 → 无揭示价值
            if (!realDiffers && !modDiffers) return;

            string text;
            if (realDiffers)
            {
                text = zh
                    ? $"真实属性：{real}{(modDiffers ? $"（{realMod:+0;-0;0}%）" : "")}（伪装为 {fake}）"
                    : $"Real: {real}{(modDiffers ? $" ({realMod:+0;-0;0}%)" : "")} (disguised as {fake})";
            }
            else
            {
                text = zh
                    ? $"真实数值修正：{realMod:+0;-0;0}%（伪装为 {fakeMod:+0;-0;0}%）"
                    : $"Real modifier: {realMod:+0;-0;0}% (disguised {fakeMod:+0;-0;0}%)";
            }

            lines.Add(new TooltipLine(text, Color));
        }
        catch (Exception ex)
        {
            Log.Warn("FeatureReveal.One", ex);
        }
    }

    private static string SafeCall(Func<string> fn)
    {
        try { return fn() ?? ""; } catch { return ""; }
    }
}
