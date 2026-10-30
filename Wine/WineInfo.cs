using System;
using System.Collections.Generic;
using System.Text;

namespace ItemDetails;

/// <summary>
/// 酒类信息（透视）：原版对玩家隐藏的酿造数据。
///
/// 依据（0.46D）：WineHelper / AgableHelper 的公开成员与标签
///   WINE_CONTAINER_TAG / WINE_CONTAINER_STATE   0=空瓶 1=发酵中 2=已酿成
///   WINE_SCORE_INT                              原始分数（原版只在 devMode 显示）
///   WineHelper.GetWineQualityTier(item)         分数 → 档位 0..5
///   WineHelper.GetWineExtractPerMille(item)     糖度‰（赝品固定 8，否则 档位×4）
///   WineHelper.GetWineContaminantPerMille(item) 污染物‰（赝品固定 8，否则 0）
///   WINE_COUNTERFEIT_TAG                        使用非法酵母时置位
///   AGE_INT / AGE_VALUE_MOD_PER_DAY_INT          陈化天数与每日加成（默认 3）
///   FERMENTATION_TIME_REMAINING_INT             发酵剩余（次）
///   WINE_YEAST_POWER_INT                        酵母力度（每次推进的发酵量）
///
/// 只读，不写任何游戏对象。
/// </summary>
internal static class WineInfo
{
    private const RenderHandler.ColorPalette Detail = RenderHandler.ColorPalette.Gray;
    private const RenderHandler.ColorPalette Good = RenderHandler.ColorPalette.LightGreen;
    private const RenderHandler.ColorPalette Bad = RenderHandler.ColorPalette.LightRed;
    private const RenderHandler.ColorPalette Info = RenderHandler.ColorPalette.LightCyan;

    internal static List<TooltipLine> Collect(GameItem? item)
    {
        var lines = new List<TooltipLine>();
        try
        {
            if (item == null) return lines;
            if (!SafeIsTag(item, "WINE_CONTAINER_TAG")) return lines;

            int? stateRaw = GameRead.TagInt(item, "WINE_CONTAINER_STATE");
            if (stateRaw is not int state) return lines;   // 状态读不到：不猜
            bool counterfeit = SafeIsTag(item, "WINE_COUNTERFEIT_TAG");
            int score = TagInt(item, "WINE_SCORE_INT");
            int tier = 0;
            try { tier = WineHelper.GetWineQualityTier(item); } catch { }

            // 1) 状态
            string stateText = state switch
            {
                0 => Loc.T("wine.empty", "Empty"),
                1 => Loc.T("wine.fermenting", "Fermenting"),
                2 => Loc.T("wine.finished", "Finished"),
                _ => Loc.T("wine.unknown_state", "Unknown ({0})", state),
            };
            lines.Add(new TooltipLine(Loc.T("wine.state", "Wine: {0}", stateText), Info));

            // 2) 分数与档位（原版零售版不显示）
            if (state == 2 || score > 0)
            {
                string tierText = tier > 0 ? Loc.T("wine.tier", ", tier {0}", tier) : "";
                lines.Add(new TooltipLine(Loc.T("wine.score", "Wine score: {0}{1}", score, tierText),
                                          tier >= 4 ? Good : Info));
            }

            // 3) 成分（‰）：糖度与污染物
            if (state == 2)
            {
                int extract = 0, contaminant = 0;
                try { extract = WineHelper.GetWineExtractPerMille(item); } catch { }
                try { contaminant = WineHelper.GetWineContaminantPerMille(item); } catch { }
                lines.Add(new TooltipLine(
                    Loc.T("wine.composition",
                        "Composition: extract {0}‰, contaminants {1}‰", extract, contaminant),
                    Detail));
            }

            // 4) 赝品判定（非法酵母）
            if (counterfeit)
                lines.Add(new TooltipLine(Loc.T("wine.counterfeit", "Counterfeit: illegal yeast"), Bad));

            // 5) 陈化加成：原版 AgableHelper.AddAgeTooltipLine 已显示「陈化 N 天」，
            //    这里只补它没写的每日加成与累计，避免同一句出现两遍。
            int age = TagInt(item, "AGE_INT");
            if (age > 0)
            {
                int perDay = GameRead.TagInt(item, "AGE_VALUE_MOD_PER_DAY_INT") ?? 3;
                lines.Add(new TooltipLine(
                    Loc.T("wine.aging", "Aging bonus: +{0}/day (total +{1})", perDay, age * perDay),
                    Detail));
            }

            // 6) 酵母力度：原版 mech_wine_fermenting_remaining 已显示剩余次数，
            //    这里只补每次推进的发酵量。
            if (state == 1)
            {
                int? yeastTag = GameRead.TagInt(item, "WINE_YEAST_POWER_INT");
                if (yeastTag is int yeast && yeast > 0)
                {
                    lines.Add(new TooltipLine(
                        Loc.T("wine.yeast_power", "Yeast strength: {0}/tick", yeast),
                        Detail));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("WineInfo", ex);
        }
        return lines;
    }

    private static int TagInt(GameItem item, string tag) => GameRead.TagInt(item, tag) ?? 0;

    private static bool SafeIsTag(GameItem item, string tag)
    {
        try { return item.IsTag(tag); } catch { return false; }
    }
}
