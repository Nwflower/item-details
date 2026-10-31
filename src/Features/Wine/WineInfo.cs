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
/// 酵母物品（WINE_YEAST_TAG）另写一行它对果酿的作用，依据 WineHelper.StartFermentation：
///   瓶里有任意酵母评分 +1（多放不叠加，与力度无关）；力度只决定发酵快慢；
///   非法酵母（WINE_YEAST_ILLEGAL_TAG）会让成品成为赝品。
///
/// 只读，不写任何游戏对象。
/// </summary>
internal static class WineInfo
{
    private const RenderHandler.ColorPalette Detail = RenderHandler.ColorPalette.Gray;
    private const RenderHandler.ColorPalette Good = RenderHandler.ColorPalette.LightGreen;
    private const RenderHandler.ColorPalette Bad = RenderHandler.ColorPalette.LightRed;
    private const RenderHandler.ColorPalette Info = RenderHandler.ColorPalette.LightCyan;

    /// <summary>StartFermentation 写入的发酵总量（原版字面量 4，无常量可读），每次推进扣掉酵母力度。</summary>
    private const int FermentationTotal = 4;
    /// <summary>StartFermentation：瓶里有酵母时评分固定 +1。</summary>
    private const int YeastScoreBonus = 1;

    internal static List<TooltipLine> Collect(GameItem? item)
    {
        var lines = new List<TooltipLine>();
        try
        {
            if (item == null) return lines;
            if (SafeIsTag(item, "WINE_YEAST_TAG"))
            {
                CollectYeast(item, lines);
                return lines;
            }
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
            // 2) 分数与档位（原版零售版不显示）：评分是酿成的结果，与状态写在同一行
            string head = Loc.T("wine.state", "Wine: {0}", stateText);
            var headColor = Info;
            if (state == 2 || score > 0)
            {
                string tierText = tier > 0 ? Loc.T("wine.tier", ", tier {0}", tier) : "";
                head += Loc.T("wine.score_part", ", score {0}", score) + tierText;
                if (tier >= 4) headColor = Good;
            }
            lines.Add(new TooltipLine(head, headColor));

            // 2b) 空瓶已放原料/酵母：按原版开酿公式预估评分
            if (state == 0) AddPreview(item, lines);

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

    /// <summary>
    /// 还没点发酵的瓶子：照 WineHelper.StartFermentation 算开酿后的评分。
    ///   每个原料 + BERRY_CONDITION_INT（负数先 +1）；原料 ≥2 个 +1；有酵母 +1；再加水质分（GetWaterQualityScore）。
    /// 第三方果实只要沿用原版 WINE_INGREDIENT_TAG / BERRY_CONDITION_INT 就一并算进去。
    /// </summary>
    private static void AddPreview(GameItem bottle, List<TooltipLine> lines)
    {
        int score = TagInt(bottle, "WINE_SCORE_INT");
        int ingredients = 0;
        bool yeast = false, illegal = false;
        foreach (var child in GraphUtils.FindAllChildrenType<GameItem>(bottle))
        {
            if (SafeIsTag(child, "WINE_INGREDIENT_TAG"))
            {
                int cond = TagInt(child, "BERRY_CONDITION_INT");
                if (cond < 0) cond++;
                score += cond;
                ingredients++;
            }
            if (SafeIsTag(child, "WINE_YEAST_TAG")) yeast = true;
            if (SafeIsTag(child, "WINE_YEAST_ILLEGAL_TAG")) illegal = true;
        }
        if (ingredients == 0 && !yeast) return;   // 什么都没放，没有可预估的

        if (ingredients >= 2) score++;
        if (yeast) score += YeastScoreBonus;
        score += WineHelper.GetWaterQualityScore(bottle);

        int tier = Tier(score);
        string text = Loc.T("wine.preview", "Score after fermenting: {0}", score)
                    + (tier > 0 ? Loc.T("wine.tier", ", tier {0}", tier) : "");
        lines.Add(new TooltipLine(text, tier >= 4 ? Good : Info));
        if (illegal)
            lines.Add(new TooltipLine(Loc.T("wine.yeast_illegal", "The wine will be a counterfeit"), Bad));
    }

    /// <summary>
    /// 评分 → 档位，与 WineHelper.GetWineQualityTier 相同（≥6 五档、5 四档、3–4 三档、2、1、其余 0）。
    /// 原版函数只读瓶上的标签，预估时分数还没写进去，只能照抄。
    /// </summary>
    private static int Tier(int score)
        => score >= 6 ? 5 : score == 5 ? 4 : score >= 3 ? 3 : score >= 1 ? score : 0;

    /// <summary>酵母放进果酿瓶的效果：评分 +1、发酵几次完成；非法酵母另提示会成赝品。</summary>
    private static void CollectYeast(GameItem item, List<TooltipLine> lines)
    {
        int power = Math.Max(1, TagInt(item, "WINE_YEAST_POWER_INT"));
        int ticks = (FermentationTotal + power - 1) / power;
        lines.Add(new TooltipLine(
            Loc.T("wine.yeast_effect", "In wine: score +{0} (does not stack), ferments in {1} tick(s)",
                YeastScoreBonus, ticks),
            Good));
        if (SafeIsTag(item, "WINE_YEAST_ILLEGAL_TAG"))
            lines.Add(new TooltipLine(Loc.T("wine.yeast_illegal", "The wine will be a counterfeit"), Bad));
    }

    private static int TagInt(GameItem item, string tag) => GameRead.TagInt(item, tag) ?? 0;

    private static bool SafeIsTag(GameItem item, string tag)
    {
        try { return item.IsTag(tag); } catch { return false; }
    }
}
