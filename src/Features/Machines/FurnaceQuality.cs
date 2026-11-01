using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 熔炉品质提示：仅读取原版的机器栏位和游戏纯度表，不执行 Furnace 夜间回调。
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

        internal string Signature => string.Join(":", Ore, Scrap, Junk,
            Blast, JunkModule, BasicFlux, AdvancedFlux);
    }

    /// <summary>参与主缓存键；插拔模块、投料或切换助熔剂后立即更新注释。</summary>
    internal static string CacheSignature(GameItem machine)
        => TryRead(machine, out var contents) ? contents!.Signature : "unavailable";

    internal static string? Note(GameItem machine)
    {
        int? quality = GameRead.TagInt(machine, QualityTag);
        if (quality == null || !TryRead(machine, out var found)) return null;
        var contents = found!;

        // 废物转换分支没有任何金属锭，也不运行 SetPurityFromInput。
        if (!contents.CanSmelt && contents.JunkModule && contents.Junk >= 6)
            return Loc.T("module.furnace_junk", "no quality effect on junk");

        int q = quality.Value;
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
        // +0 is technically correct, but repeats the visible percentage without explaining anything.
        if (difference != 0)
        {
            string change = difference >= 0 ? "+" + difference : difference.ToString();
            parts.Add(Loc.T("module.furnace_gain", "purity {0} tier(s)", change));
        }

        if (contents.CanSmelt)
        {
            int count = FurnacePurity.OutputCount(contents.Blast);
            string purityKey = IngotPurityHelper.GetPurityConst(LowPurity,
                FurnacePurity.MaterialOffset(contents.Ore, contents.Blast)
                + FurnacePurity.Offset(q, contents.Blast, contents.HasFlux));
            if (!IngotPurityHelper.ingotPurities.TryGetValue(purityKey, out var purity)) return null;
            parts.Add(Loc.T("module.furnace_result", "ingot ×{0}: {1}/10",
                count, purity.PurityLevel));
        }

        int next = FurnacePurity.NextImprovement(q, Level);
        if (next > 0)
            parts.Add(Loc.T("module.furnace_next", "+{0}% to next tier", next));
        else if (contents.CanSmelt)
        {
            // 只在确实触及游戏纯度表上限时显示“封顶”，不把搜索范围不足误报为封顶。
            string maxKey = IngotPurityHelper.GetPurityConst(LowPurity, 1000);
            if (IngotPurityHelper.ingotPurities.TryGetValue(maxKey, out var maximum)
                && current >= maximum.PurityLevel)
                parts.Add(Loc.T("module.furnace_cap", "maximum purity"));
        }

        if (contents.CanSmelt && FurnacePurity.ConsumesBasicFlux(q, contents.Blast,
                contents.BasicFlux, contents.AdvancedFlux))
            parts.Add(Loc.T("module.furnace_basic", "basic flux consumed"));
        // Advanced flux is already visible in the input list; don't restate that it is reusable.
        return parts.Count > 0 ? string.Join(Loc.T("list.sep", ", "), parts) : null;
    }

    private static bool TryRead(GameItem machine, out Contents? result)
    {
        result = null;
        try
        {
            var input = MachineHelper.GetBodyStorage(machine, 2);
            var modules = MachineHelper.GetBodyStorage(machine, 1);
            if (input == null || modules == null) return false;

            var contents = new Contents();
            foreach (var item in GraphUtils.FindAllChildrenType<GameItem>(input))
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
            foreach (var item in GraphUtils.FindAllChildrenType<GameItem>(modules))
            {
                if (item.identifier == "furnace_module_blast") contents.Blast = true;
                else if (item.identifier == "furnace_module_junk") contents.JunkModule = true;
            }
            result = contents;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("FurnaceQuality.Read", ex);
            return false;
        }
    }
}
