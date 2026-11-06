using System;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 接管液体容器的体积行：原版 WaterHelper.CreateWaterContainerTooltip 会把
/// `体积条 + 2000 ml / 2000 ml` 画在物品名与价格下面（[标签] 行之前），
/// 且条长 10 格、没有档位。液体组分分析开启时整段跳过原版行，
/// 由 <see cref="ModEntry.TryAppendWater"/> 在成分表顶端重画：
///
///     ██████ 2000/2000mL 优质水
///
/// 表头不留文字，和空瓶一样只画体积条；这样进度条落在 [标签] 行下面、条也更短，不再折行。
/// 关闭液体分析时原版行照旧，一点不影响。
/// </summary>
internal static class WaterContainerLine
{

    internal static void Install(PatchScope scope)
    {
        try
        {
            var target = AccessTools.Method(typeof(WaterHelper), "CreateWaterContainerTooltip",
                new[] { typeof(RichTextBuilder), typeof(GameItem) });
            if (target == null)
            {
                Log.Warn("WaterContainerLine", "未找到 WaterHelper.CreateWaterContainerTooltip，标签跳过");
                return;
            }
            scope.Patch(target, prefix: new HarmonyMethod(typeof(WaterContainerLine), nameof(Skip)));
            Log.Msg("WaterContainerLine: patched WaterHelper.CreateWaterContainerTooltip");
        }
        catch (Exception ex)
        {
            Log.Error("WaterContainerLine.Apply", ex);
        }
    }

    /// <summary>
    /// 与原版同一套前置条件：带 LIQUID_CONTAINER_TAG、不是酒容器。
    /// 满足时返回 false 跳过原版体积行，交给 <see cref="ModEntry.TryAppendWater"/> 重画。
    /// </summary>
    private static bool Skip(RichTextBuilder __0, GameItem __1)
    {
        try
        {
            if (__0 == null || __1 == null) return true;
            if (!__1.IsTag("LIQUID_CONTAINER_TAG") || __1.IsTag("WINE_CONTAINER_TAG")) return true;
            return LiquidQueries.Analyze(__1) == null;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterContainerLine.Skip", ex);
            return true;
        }
    }
}
