using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>液体在容器里的显示角色：水、药物活性成分、其他（杂质/试剂/溶剂）。</summary>
internal enum LiquidRole
{
    Water,
    Active,
    Other,
}

/// <summary>
/// 按“游戏是否用绝对量判定效果”区分活性成分与其他液体。
///
/// 判定依据（0.46D 源码）：
///   - 效果字段：<c>euphoria</c>（ChemicalFeedbackHelper.IsNarcotic）、
///     <c>metabolismRatePerDay</c>（IngesterHelper 的代谢）、<c>addictiveness</c>；
///   - 致死字段：<c>lethality</c> 参与 ChemicalFeedbackHelper.IsLethal 的绝对量求和。
///     但 acid/base 的 0.1 只用于腐蚀/中和，属于试剂而非剂量，因此只把
///     lethality ≥ 1.0 视为药物（zyanide 1.66、fanta 2.0、nightmare_extract 4.0）。
///   - 配方活性成分：ChemFinisherRecipe.chemFinisherRecipes 的 activeIngredientId，
///     读取活列表，第三方新增配方会自动纳入；
///   - 游戏写死绝对阈值、但没有效果字段的两种：synaprest（≥1000 parts）、
///     contraceptive（≥50 parts），见 ChemicalFeedbackHelper.IsSynaprest / IsBirthControl。
///
/// 其余液体一律归 Other，按百分比显示。这样第三方模组新增的未知液体不会被
/// 猜成“药物剂量”，只会退回原来的占比展示。
/// </summary>
internal static class LiquidRoleClassifier
{
    /// <summary>lethality 达到这个值才当作药物剂量；acid/base 的 0.1 留在试剂。</summary>
    private const double LethalDoseThreshold = 1.0;

    /// <summary>游戏源码写死绝对阈值、但 Liquid 上没有效果字段的液体。</summary>
    private static readonly HashSet<string> ThresholdActives = new(StringComparer.Ordinal)
    {
        "synaprest",
        "contraceptive",
    };

    /// <summary>配方列表读取失败时的兜底；正常情况下以活列表为准。</summary>
    private static readonly HashSet<string> FallbackRecipeActives = new(StringComparer.Ordinal)
    {
        "zyanide",
        "fanta",
        "caffeine",
        "contraceptive",
        "nomore",
        "dream_cap_extract",
        "nightmare_extract",
    };

    private static HashSet<string>? _recipeActives;
    private static int _recipeCount = -1;

    internal static LiquidRole Classify(Liquid? liquid)
    {
        try
        {
            if (liquid == null) return LiquidRole.Other;

            string id = liquid.identifier ?? "";
            if (string.Equals(id, "water", StringComparison.Ordinal)) return LiquidRole.Water;
            if (id.Length == 0) return LiquidRole.Other;

            if (ThresholdActives.Contains(id)) return LiquidRole.Active;
            if (liquid.euphoria > 0 || liquid.addictiveness > 0
                || liquid.metabolismRatePerDay > 0
                || liquid.lethality >= LethalDoseThreshold)
                return LiquidRole.Active;

            return RecipeActives().Contains(id) ? LiquidRole.Active : LiquidRole.Other;
        }
        catch
        {
            return LiquidRole.Other;
        }
    }

    /// <summary>
    /// 读游戏自己的配方活性成分。列表允许第三方追加，所以数量变化时重建；
    /// 读取异常时退回固定集合，调用方永远不会因此拿不到分类。
    /// 首次调用发生在 tooltip 构建期（本地化已就绪），不会像启动期 JIT 那样把
    /// ChemFinisherRecipe 的显示名钉死在英文。
    /// </summary>
    private static HashSet<string> RecipeActives()
    {
        try
        {
            var recipes = ChemFinisherRecipe.chemFinisherRecipes;
            if (recipes == null) return FallbackRecipeActives;
            if (_recipeActives != null && _recipeCount == recipes.Count) return _recipeActives;

            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var recipe in recipes)
            {
                if (recipe == null) continue;
                if (!string.IsNullOrEmpty(recipe.activeIngredientId)) set.Add(recipe.activeIngredientId);
            }
            foreach (string id in FallbackRecipeActives) set.Add(id);

            _recipeActives = set;
            _recipeCount = recipes.Count;
            return set;
        }
        catch
        {
            return FallbackRecipeActives;
        }
    }
}
