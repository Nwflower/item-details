using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// P2 隐藏信息：原版 tooltip 不显示（或只显示伪装值）的"真相"数据。
/// 全部只读，逐项 try/catch；某一项失效只损失该项。
/// 依据：正式版 0.46D Assembly-CSharp 反编译源码（DurabilityHelper / IngotPurityHelper / TagState）。
/// 原版已经展示的耐久/纯度不再重复输出；赃物数值由原版 StolenHelper 固定展示，也不在这里重复。
/// </summary>
internal static class HiddenInfo
{
    private const RenderHandler.ColorPalette Warn = RenderHandler.ColorPalette.LightRed;
    private const RenderHandler.ColorPalette Info = RenderHandler.ColorPalette.LightTurquoise;

    internal static List<TooltipLine> Collect(GameItem? item)
    {
        var lines = new List<TooltipLine>();
        if (item == null) return lines;

        TryDurability(item, lines);
        TryIngotPurity(item, lines);
        TryChemicalPurity(item, lines);

        return lines;
    }

    // ---------- 耐久：真实 vs 伪装 ----------
    private static void TryDurability(GameItem item, List<TooltipLine> lines)
    {
        try
        {
            if (!item.IsTag("DURABILITY_TAG")) return;

            int real = DurabilityHelper.GetRealDurability(item);
            int fake = DurabilityHelper.GetFakeDurability(item);
            int max = DurabilityHelper.GetMaxDurability(item);
            if (max <= 0) return;

            // 已揭露时原版 DurabilityHelper.CreateIngotTooltip 已经显示真实耐久与进度条。
            if (IsDiscovered(item, "DURABILITY_TAG")) return;

            Log.MarkOk("Hidden.Durability");

            if (fake != real)
            {
                // 有伪装：原版显示 fake，我们揭示 real
                lines.Add(new TooltipLine(
                    Loc.T("hidden.durability_faked",
                        "Real durability: {0}/{1} (shown {2}/{3}, likely faked)",
                        real, max, fake, max),
                    Warn));
            }
            else
            {
                lines.Add(new TooltipLine(
                    Loc.T("hidden.durability", "Durability: {0}/{1}", real, max),
                    Info));
            }
        }
        catch (Exception ex) { Log.Warn("Hidden.Durability", ex); }
    }

    // ---------- 锭纯度：真实 vs 伪装 ----------
    private static void TryIngotPurity(GameItem item, List<TooltipLine> lines)
    {
        try
        {
            if (!item.IsTag("INGOT_PURITY_TAG")) return;
            // 已揭露时原版显示 “[Tested] Ingot Purity: ...”，不重复。
            if (IsDiscovered(item, "INGOT_PURITY_TAG")) return;

            string real = GameRead.TagString(item, "INGOT_PURITY_TAG") ?? "";
            if (string.IsNullOrWhiteSpace(real)) return;

            string fake = GameRead.TagString(item, "INGOT_PURITY_FAKE_TAG") ?? "";

            Log.MarkOk("Hidden.IngotPurity");

            bool faked = !string.IsNullOrWhiteSpace(fake) && fake != real;
            string realText = PurityDisplay(real);
            lines.Add(new TooltipLine(
                faked
                    ? Loc.T("hidden.purity_faked",
                        "Real purity: {0} (shown {1}, likely faked)",
                        realText, PurityDisplay(fake))
                    : Loc.T("hidden.purity", "Purity: {0}", realText),
                faked ? Warn : Info));
        }
        catch (Exception ex) { Log.Warn("Hidden.IngotPurity", ex); }
    }

    /// <summary>
    /// 纯度常量 → 玩家文案。优先用游戏自己 ItemCondition.display（随语言本地化），
    /// 不把 INGOT_PURITY_* 这类内部键直接塞进 tooltip。
    /// </summary>
    private static string PurityDisplay(string purity)
    {
        try
        {
            if (IngotPurityHelper.ingotPurities.TryGetValue(purity, out var info)
                && info?.itemCondition != null)
            {
                string display = info.itemCondition.display ?? "";
                if (display.Length > 0) return display;
            }
        }
        catch { }

        // 未登记的纯度（例如第三方扩展）：只去掉内部前缀与下划线，不暴露枚举全名。
        string fallback = purity.StartsWith("INGOT_PURITY_", StringComparison.Ordinal)
            ? purity.Substring("INGOT_PURITY_".Length)
            : purity;
        return fallback.Replace('_', ' ');
    }

    // ---------- 化学品纯度 ----------
    private static void TryChemicalPurity(GameItem item, List<TooltipLine> lines)
    {
        try
        {
            if (!item.IsTag("CHEMICAL_PRODUCT_PURITY_TAG")) return;

            // 原版 ChemicalProductHelper.GradeProduct 用 SetDouble 写入百分比，
            // GetPurity 也用 GetDouble 读；valueInt 从未被赋值，用 GetInt 只会得到 0。
            double? purity = GameRead.TagDouble(item, "CHEMICAL_PRODUCT_PURITY_TAG");
            if (purity is not double v || v <= 0) return;

            Log.MarkOk("Hidden.ChemPurity");
            lines.Add(new TooltipLine(
                Loc.T("hidden.chem_purity", "Chemical purity: {0}%", v.ToString("0.#")),
                Info));
        }
        catch (Exception ex) { Log.Warn("Hidden.ChemPurity", ex); }
    }

    private static bool IsDiscovered(GameItem item, string featureId)
    {
        try
        {
            var feature = item.FindItemFeatureByID(featureId);
            return feature != null && feature.IsFeatureDiscovered();
        }
        catch { return false; }
    }
}
