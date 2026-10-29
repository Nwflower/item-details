using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 水质与杂质：液体容器里水的纯度百分比，以及稀释它的其他液体。
///
/// 原版 WaterHelper.CreateWaterContainerTooltip 只显示体积/容量，
/// 纯度必须靠试纸（STRIP_TESTED_TAG / LAST_SCAN_RESULT_TAG）才能得知，
/// 杂质构成则完全不显示。本模块把这两项直接列出来。
///
/// 数据来源（正式版 0.46D，均为 public API）：
///   WaterHelper.GetWaterPurity(item) —— 水 parts × 10000 / 总 parts（万分比）
///   WaterHelper.GetTotalVolume(item) —— 总 parts（1 ml = 1000 parts）
///   Liquid.Liquids / Liquid.currentPart —— 逐种液体取 parts
///   WaterFeatureHelper.GetWaterDisplayName(purity) —— 游戏自己的档位文案
///
/// 只读；逐项 try/catch。
/// </summary>
internal static class WaterInfo
{
    /// <summary>万分比 100%（游戏内部纯度单位）。</summary>
    internal const int PurityFull = 10000;

    /// <summary>纯度分档阈值（0..10000），用于给水质行着色。</summary>
    internal const int PurityGood = 9800;
    internal const int PurityFair = 9000;

    /// <summary>杂质显示下限：低于此值的量按 "0.#" 会显示成 0ml，直接不列。</summary>
    internal const int MinVisibleParts = 50;

    internal sealed class Result
    {
        internal bool HasData;
        internal int Purity;        // 0..10000
        internal int TotalParts;
        internal int WaterParts;
        internal string Tier = "";  // 游戏档位名，如「洁净水」
        /// <summary>标签上写的水质名（未揭露时是伪装值），用于判断与实测档位是否匹配。</summary>
        internal string Label = "";
        internal readonly List<(string Name, int Parts)> Impurities = new();
    }

    internal static Result Analyze(GameItem? item)
    {
        var r = new Result();
        try
        {
            if (item == null) return r;
            if (!SafeIsTag(item, "LIQUID_CONTAINER_TAG")) return r;
            if (SafeIsTag(item, "CHEMICAL_PRODUCT_TAG")) return r;   // 化学品另有纯度体系，避免混淆

            foreach (Liquid liquid in Liquid.Liquids)
            {
                int parts = Parts(item, liquid.currentPart);
                if (parts <= 0) continue;
                r.TotalParts += parts;
                if (liquid.identifier == "water")
                    r.WaterParts += parts;
                else
                    r.Impurities.Add((DisplayName(liquid), parts));
            }

            if (r.TotalParts <= 0) return r;
            if (r.WaterParts <= 0) return r;   // 不含水就不是水质问题

            r.Purity = (int)((long)r.WaterParts * PurityFull / r.TotalParts);
            try { r.Tier = WaterFeatureHelper.GetWaterDisplayName(r.Purity) ?? ""; } catch { }
            // 标签口径：未揭露时返回伪装值，正是玩家"被骗"的那一面；
            // 与实测档位不同即视为标签与水质不匹配。
            // 不能直接调 GetCurrentWaterDisplay：它内部的 GetWaterFeature 在物品缺
            // CATEGORY_WATER_PURITY 特性时会 AddItemFeature——等于悬停时给真实物品加特性。
            // 先做纯查找（FindItemFeatureByCategory 只遍历），确认特性已存在再取值；
            // 酒容器由 GetWaterFeature 返回临时特性、不落到物品上，保持原行为。
            try
            {
                bool hasFeature = SafeIsTag(item, "WINE_CONTAINER_TAG")
                    || item.FindItemFeatureByCategory("CATEGORY_WATER_PURITY") != null;
                if (hasFeature) r.Label = WaterFeatureHelper.GetCurrentWaterDisplay(item) ?? "";
            }
            catch { }
            r.HasData = true;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterInfo", ex);
        }
        return r;
    }

    /// <summary>杂质占比（万分比）。</summary>
    internal static int ImpurityPermille(Result r)
        => r.TotalParts <= 0 ? 0 : (int)((long)(r.TotalParts - r.WaterParts) * PurityFull / r.TotalParts);

    internal static string Ml(int parts) => (parts / 1000.0).ToString("0.#");

    private static int Parts(GameItem item, string tag) => GameRead.TagInt(item, tag) ?? 0;

    private static bool SafeIsTag(GameItem item, string tag)
    {
        try { return item.IsTag(tag); } catch { return false; }
    }

    /// <summary>
    /// 杂质显示名。
    ///
    /// 不能直接用 <c>liquid.displayName</c>：它是在 <c>Liquid</c> 的静态构造里一次性
    /// `LocHelper.GetLocalizedMechanic("liquid_" + identifier)` 固化的。只要这个静态构造
    /// 在本地化就绪前被触发（Harmony 打补丁会 JIT 目标方法，从而提前跑 cctor），
    /// 整局会话的液体名都会停在英文。这里按当前语言现查一次，查不到才回落到它。
    /// </summary>
    private static string DisplayName(Liquid liquid)
    {
        try
        {
            string id = liquid.identifier ?? "";
            if (id.Length > 0)
            {
                try
                {
                    string key = "liquid_" + id;
                    string live = LocHelper.GetLocalizedMechanic(key) ?? "";
                    if (live.Length > 0 && !string.Equals(live, key, StringComparison.Ordinal))
                        return live;
                }
                catch { }
            }

            string n = liquid.displayName ?? "";
            return n.Length > 0 ? n : (id.Length > 0 ? id : "?");
        }
        catch { return "?"; }
    }
}
