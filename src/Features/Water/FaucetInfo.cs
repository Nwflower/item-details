using System;
using System.Collections.Generic;
using HarmonyLib;
using ItemDetails.Integration;

namespace ItemDetails;

/// <summary>
/// 水龙头：显示出水的水质成分。
///
/// 依据（0.46D）：用容器对着水龙头使用时，原版走
/// <c>AddWater(容器, 1, 9999999, useRange: true, today*Purity, today*Purity)</c>——
/// 纯度取当天的 <c>StoreStation.todayRustwaterPurity</c>（已解锁锈水龙头）或
/// <c>todayGutterflowPurity</c>（已解锁沟流龙头），上下限相同，所以是确定值；
/// 其余部分按 <c>WaterPremadeHelper.waterContaminantDistribution</c> 的固定占比分给各类杂质。
/// 这里按同一规则折算成百分比，不创建容器、不改任何状态。容器自带的滤芯会在灌水时再滤掉一部分杂质，
/// 属于容器侧的变化，这里不计。
///
/// 水龙头物品平时带 IS_TOOLTIP_DISABLED_TAG；原版只在「解锁升级」那一刻摘掉这个标记，
/// 读档后（<c>AlwayCall</c> 只更新了描述）标记仍在，所以读档后龙头没有提示框。
/// 补丁只在龙头已解锁时让 <c>HasTooltip</c> 返回 true，其余物品不受影响。
/// </summary>
internal static class FaucetInfo
{
    private const string FaucetId = "faucet";
    private const int SampleParts = 1_000_000;   // 1 L 的样本，只取比例

    internal static bool IsFaucet(GameItem? item)
    {
        try { return item != null && FaucetId.Equals(item.identifier, StringComparison.Ordinal); }
        catch { return false; }
    }

    internal static void Install(PatchScope scope)
    {
        try
        {
            var target = AccessTools.Method(typeof(GameItemElement), "HasTooltip", Type.EmptyTypes);
            if (target == null)
            {
                Log.Warn("FaucetInfo", "未找到 GameItemElement.HasTooltip，龙头提示框跳过");
                return;
            }
            scope.Patch(target, postfix: new HarmonyMethod(typeof(FaucetInfo), nameof(AfterHasTooltip)));
            Log.Msg("FaucetInfo: patched GameItemElement.HasTooltip");
        }
        catch (Exception ex)
        {
            Log.Error("FaucetInfo.Apply", ex);
        }
    }

    private static void AfterHasTooltip(GameItemElement __instance, ref bool __result)
    {
        if (__result || !IsFaucet(__instance)) return;
        try
        {
            if (LiquidGate.Enabled && TapPurity() > 0) __result = true;
        }
        catch (Exception ex) { Log.Warn("FaucetInfo.HasTooltip", ex); }
    }

    /// <summary>当前龙头的出水纯度（万分比）；两种龙头都没解锁为 0。顺序与原版一致：锈水先于沟流。</summary>
    internal static int TapPurity()
    {
        var station = StoreStation.Instance;
        if (station == null) return 0;
        if (NetworkUpgrade.GetUpgradeById("RUSTWATER_TAP").IsUnlocked()) return station.todayRustwaterPurity;
        if (NetworkUpgrade.GetUpgradeById("GUTTERFLOW_TAP").IsUnlocked()) return station.todayGutterflowPurity;
        return 0;
    }

    /// <summary>按出水规则折算出一份成分快照；龙头未解锁或读取失败时为 null。</summary>
    internal static LiquidAnalysis? Analyze(GameItem item)
    {
        try
        {
            int purity = TapPurity();
            if (purity <= 0) return null;
            purity = Math.Min(purity, WaterInfo.PurityFull);

            var raw = new WaterInfo.Result { HasData = true, Purity = purity, TotalParts = SampleParts };
            raw.WaterParts = (int)(((long)SampleParts * purity + WaterInfo.PurityFull - 1) / WaterInfo.PurityFull);
            string waterPart = Liquid.GetCurrentPartFromId("water");
            var water = Liquid.Liquids.Find(l => l.currentPart == waterPart);
            raw.WaterName = water != null ? WaterInfo.DisplayName(water) : "";
            try { raw.Tier = WaterFeatureHelper.GetWaterDisplayName(purity) ?? ""; } catch { }

            int rest = SampleParts - raw.WaterParts;
            var shares = new List<(Liquid Liquid, int Parts)>();
            int assigned = 0;
            foreach (var pair in WaterPremadeHelper.waterContaminantDistribution)
            {
                var liquid = Liquid.Liquids.Find(l => l.currentPart == pair.Key);
                if (liquid == null) continue;
                int parts = (int)(rest * pair.Value);
                assigned += parts;
                shares.Add((liquid, parts));
            }
            if (shares.Count > 0) shares[0] = (shares[0].Liquid, shares[0].Parts + Math.Max(0, rest - assigned));
            foreach (var (liquid, parts) in shares)
                if (parts > 0) raw.Impurities.Add((WaterInfo.DisplayName(liquid), parts, liquid.identifier ?? ""));
            raw.Impurities.Sort((a, b) => b.Parts.CompareTo(a.Parts));
            return LiquidQueries.Snapshot(raw, item);
        }
        catch (Exception ex)
        {
            Log.Warn("FaucetInfo.Analyze", ex);
            return null;
        }
    }
}
