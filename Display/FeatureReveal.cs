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

    internal static List<TooltipLine> Collect(GameItem? item)
    {
        var lines = new List<TooltipLine>();
        if (item == null) return lines;

        try
        {
            foreach (var f in FeatureScan.All(item))
                TryOne(f, lines);

            if (lines.Count > 0) Log.MarkOk("FeatureReveal");
        }
        catch (Exception ex)
        {
            Log.Warn("FeatureReveal", ex);
        }
        return lines;
    }

    /// <summary>
    /// 这个特性的伪装是否与真实不同（本模块会为它写一行）。
    /// ConditionEffects 用它避免同一个瑕疵再写一行「状态：…」。
    /// </summary>
    internal static bool Reveals(ItemFeature f)
    {
        try
        {
            if (!f.useCondition) return false;
            string fake = SafeCall(() => f.GetBaseFakeDisplay());
            string real = SafeCall(() => f.GetBaseRealDisplay());
            if (string.IsNullOrWhiteSpace(real)) return false;
            return (!string.IsNullOrWhiteSpace(fake) && fake != real) || RealMod(f) != FakeMod(f);
        }
        catch { return false; }
    }

    private static int RealMod(ItemFeature f)
    {
        try { return f.GetActualValueModifier(); } catch { return 0; }
    }

    private static int FakeMod(ItemFeature f)
    {
        try { return f.GetFakeValueModifier(); } catch { return 0; }
    }

    private static void TryOne(ItemFeature f, List<TooltipLine> lines)
    {
        try
        {
            if (!Reveals(f)) return;   // 没有条件机制，或伪装与真实一致

            string fake = SafeCall(() => f.GetBaseFakeDisplay());
            string real = SafeCall(() => f.GetBaseRealDisplay());
            int realMod = RealMod(f), fakeMod = FakeMod(f);
            bool realDiffers = !string.IsNullOrWhiteSpace(fake) && fake != real;

            string text;
            if (realDiffers)
            {
                // 真实状态的价格影响写在同一行，ConditionEffects 不再另起一行
                string signed = RealSigned(f, realMod);
                text = signed.Length > 0
                    ? Loc.T("reveal.real_with_mod", "True: {0} {1} (disguised as {2})", real, signed, fake)
                    : Loc.T("reveal.real", "True: {0} (disguised as {1})", real, fake);
            }
            else
            {
                text = Loc.T("reveal.modifier", "True modifier: {0}% (disguised as {1}%)",
                    realMod.ToString("+0;-0;0"), fakeMod.ToString("+0;-0;0"));
            }

            lines.Add(new TooltipLine(text, Color));
        }
        catch (Exception ex)
        {
            Log.Warn("FeatureReveal.One", ex);
        }
    }

    /// <summary>与 ConditionEffects 同一写法：转换型条件按点数，其余按百分比；0 不写。</summary>
    private static string RealSigned(ItemFeature f, int realMod)
    {
        var cond = f.realCondition;
        int amount = cond != null ? ConditionEffects.DisplayAmount(cond) : realMod;
        if (amount == 0) return "";
        string suffix = cond != null ? ConditionEffects.Suffix(cond) : "%";
        return amount > 0 ? $"+{amount}{suffix}" : $"{amount}{suffix}";
    }

    private static string SafeCall(Func<string> fn)
    {
        try { return fn() ?? ""; } catch { return ""; }
    }
}
