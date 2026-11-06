using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 熔炉品质提示：只读机器摘要里已按 role 归好的投料与模块，不执行 Furnace 夜间回调。
/// 采用原版优先消耗矿石的选择规则，预测的只是「下次成功冶炼时」的产量与品质。
/// </summary>
internal static class FurnaceQuality
{
    private const string QualityTag = "TOTAL_PERCENTAGE_QUALITY_BONUS_INT";
    private const string LowPurity = "INGOT_PURITY_LOW";

    private sealed class Contents
    {
        internal int Ore;
        internal int Scrap;
        internal int Junk;
        internal bool Blast;
        internal bool JunkModule;
        internal bool BasicFlux;
        internal bool AdvancedFlux;

        internal bool HasFlux => BasicFlux || AdvancedFlux;
        internal int Needed => FurnacePurity.InputCount(Blast);
        internal bool CanSmelt => Ore + Scrap >= Needed;
    }

    /// <summary>文字取哪一部分：纯度变化挂在质量行第三列，出锭预测独立成行。</summary>
    internal enum Part { All, Gain, Rest }

    internal static string? Note(MachineInterior.Context ctx,
        out (int? Ingots, int? Purity, int Gain, int? NextBonus)? data, Part part = Part.All)
    {
        data = null;
        try
        {
            var contents = Read(ctx);
            if (contents == null) return null;

            // 质量标签缺失应视为没有质量加成，不应阻止纯度预览。
            int quality = GameRead.TagInt(ctx.Machine, QualityTag) ?? 0;

            // 废物转换分支没有任何金属锭，也不运行 SetPurityFromInput。
            if (!contents.CanSmelt && contents.JunkModule && contents.Junk >= 6)
                return part == Part.Gain ? null : Loc.T("module.furnace_junk", "no quality effect on junk");

            int q = quality;
            int Level(int bonus)
            {
                int offset = FurnacePurity.Offset(bonus, contents.Blast, contents.HasFlux);
                if (!contents.CanSmelt) return offset;
                string key = IngotPurityHelper.GetPurityConst(LowPurity,
                    FurnacePurity.MaterialOffset(contents.Ore, contents.Blast) + offset);
                return IngotPurityHelper.ingotPurities.TryGetValue(key, out var purity)
                    ? purity.PurityLevel : int.MinValue;
            }

            int current = Level(q);
            int baseline = Level(0);
            if (current == int.MinValue || baseline == int.MinValue) return null;

            int difference = current - baseline;
            var parts = new List<string>();
            // 现在加了多少纯度（+0 也写出来），后面补「再 +X% 到下一档」
            int? nextBonus = null;
            for (int extra = 1; extra <= 400; extra++)
            {
                int next = Level(q + extra);
                if (next != int.MinValue && next > current) { nextBonus = extra; break; }
            }
            if (part != Part.Rest)
            {
                string change = difference >= 0 ? "+" + difference : difference.ToString();
                string gain = Loc.T("module.furnace_gain", "purity {0}", change);
                if (nextBonus is int more)
                    gain += " " + Loc.T("module.furnace_next", "(+{0}% more for next tier)", more);
                parts.Add(gain);
            }

            if (part != Part.Gain && contents.CanSmelt)
            {
                int count = FurnacePurity.OutputCount(contents.Blast);
                string purityKey = IngotPurityHelper.GetPurityConst(LowPurity,
                    FurnacePurity.MaterialOffset(contents.Ore, contents.Blast)
                    + FurnacePurity.Offset(q, contents.Blast, contents.HasFlux));
                if (!IngotPurityHelper.ingotPurities.TryGetValue(purityKey, out var purity)) return null;
                parts.Add(Loc.T("module.furnace_result", "ingot ×{0}: {1}/10",
                    count, purity.PurityLevel));
            }

            if (part != Part.Gain && contents.CanSmelt)
            {
                // 只在确实触及游戏纯度表上限时显示“封顶”，不把搜索范围不足误报为封顶。
                string maxKey = IngotPurityHelper.GetPurityConst(LowPurity, 1000);
                if (IngotPurityHelper.ingotPurities.TryGetValue(maxKey, out var maximum)
                    && current >= maximum.PurityLevel)
                    parts.Add(Loc.T("module.furnace_cap", "maximum purity"));
            }

            if (part != Part.Gain && contents.CanSmelt && FurnacePurity.ConsumesBasicFlux(q, contents.Blast,
                    contents.BasicFlux, contents.AdvancedFlux))
                parts.Add(Loc.T("module.furnace_basic", "basic flux consumed"));
            // Advanced flux is already visible in the input list; don't restate that it is reusable.
            data = (contents.CanSmelt ? FurnacePurity.OutputCount(contents.Blast) : null,
                contents.CanSmelt ? current : null, difference, nextBonus);
            return parts.Count > 0 ? string.Join(Loc.T("list.sep", ", "), parts) : null;
        }
        catch (Exception ex)
        {
            Log.Warn("FurnaceQuality", ex);
            return null;
        }
    }

    /// <summary>
    /// 投料与模块由机器摘要按 role 归好（模块面板不进 <c>Panels</c>），不再按下标取槽位。
    /// 没有输入面板时不猜内容，宁可不出这一行。
    /// </summary>
    private static Contents? Read(MachineInterior.Context ctx)
    {
        var input = ctx.FindPanel("input");
        if (input == null) return null;

        var contents = new Contents();
        foreach (var item in input.Items)
        {
            switch (item.identifier)
            {
                case "common_ore": contents.Ore++; break;
                case "scrap_metal": contents.Scrap++; break;
                case "junk": contents.Junk++; break;
                case "flux_agent": contents.BasicFlux = true; break;
                case "advanced_flux_agent": contents.AdvancedFlux = true; break;
            }
        }
        foreach (var item in ctx.Modules)
        {
            if (item.identifier == "furnace_module_blast") contents.Blast = true;
            else if (item.identifier == "furnace_module_junk") contents.JunkModule = true;
        }
        return contents;
    }
}
