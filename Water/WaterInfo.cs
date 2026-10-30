using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 水质与成分：水的纯度百分比、药物活性成分的精确用量、其他液体的占比。
///
/// 原版 WaterHelper.CreateWaterContainerTooltip 只显示体积/容量，
/// 纯度必须靠试纸（STRIP_TESTED_TAG / LAST_SCAN_RESULT_TAG）才能得知，
/// 成分构成则完全不显示。本模块把这些直接列出来。
///
/// 显示口径（0.46D）：游戏判定药物效果用的是绝对量（parts × 系数对比固定阈值，
/// 见 ChemicalFeedbackHelper），百分比无法表达 600 µl 与 602 µl 的致死线差异。
/// 因此药物活性成分给精确用量，杂质/试剂仍给百分比；分类规则见
/// <see cref="LiquidRoleClassifier"/>。
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

    /// <summary>杂质显示下限：低于此值的量在两位小数下通常只剩 0.00%，直接不列。</summary>
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
        /// <summary>药物活性成分：显示精确用量（µl / ml）。</summary>
        internal readonly List<(string Name, int Parts)> Actives = new();
        /// <summary>杂质与试剂：仍按占总体积的百分比显示。</summary>
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

                switch (LiquidRoleClassifier.Classify(liquid))
                {
                    case LiquidRole.Water:
                        r.WaterParts += parts;
                        break;
                    case LiquidRole.Active:
                        r.Actives.Add((DisplayName(liquid), parts));
                        break;
                    default:
                        r.Impurities.Add((DisplayName(liquid), parts));
                        break;
                }
            }

            if (r.TotalParts <= 0) return r;
            // 无水的纯药物混合物同样要展示（玩家把药溶进酒精/提取液的常见形态）；
            // 只有溶剂、没有水也没有活性成分时仍不属于本模块。
            if (r.WaterParts <= 0 && r.Actives.Count == 0) return r;

            if (r.WaterParts > 0)
            {
                r.Purity = (int)((long)r.WaterParts * PurityFull / r.TotalParts);
                try { r.Tier = WaterFeatureHelper.GetWaterDisplayName(r.Purity) ?? ""; } catch { }
                // 标签口径：未揭露时读伪装值，已揭露时读实测值。
                // 普通水容器完全绕开 GetCurrentWaterDisplay / GetWaterFeature：后者在缺特性时
                // 会 AddItemFeature。这里只读已有特性，显示路径不具备修改标签判定的入口。
                // 酒容器由 GetWaterFeature 返回临时特性、不落到物品上，保持原行为。
                try
                {
                    if (SafeIsTag(item, "WINE_CONTAINER_TAG"))
                    {
                        r.Label = WaterFeatureHelper.GetCurrentWaterDisplay(item) ?? "";
                    }
                    else
                    {
                        var feature = item.FindItemFeatureByCategory("CATEGORY_WATER_PURITY");
                        if (feature != null)
                        {
                            int labelPurity = feature.IsExposed()
                                ? r.Purity
                                : feature.GetCurrentItemCondition().customValue;
                            r.Label = WaterFeatureHelper.GetWaterDisplayName(labelPurity) ?? "";
                        }
                    }
                }
                catch { }
            }

            SortByParts(r.Actives);
            SortByParts(r.Impurities);
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

    /// <summary>
    /// 沿用扫描仪的单位，保留 1 part = 1 µl 的精度，避免毫升舍入掩盖效果阈值。
    /// </summary>
    internal static string Amount(int parts)
    {
        if (parts >= 1000000) return (parts / 1000000m).ToString("0.######") + " L";
        if (parts >= 1000) return (parts / 1000m).ToString("0.###") + " mL";
        return parts + " uL";
    }

    private static void SortByParts(List<(string Name, int Parts)> list)
        => list.Sort((a, b) => b.Parts.CompareTo(a.Parts));

    private static int Parts(GameItem item, string tag) => GameRead.TagInt(item, tag) ?? 0;

    private static bool SafeIsTag(GameItem item, string tag)
    {
        try { return item.IsTag(tag); } catch { return false; }
    }

    /// <summary>
    /// 杂质显示名。
    ///
    /// 不能直接用 <c>liquid.displayName</c>：它是在 <c>Liquid</c> 的静态构造里一次性
    /// `LocHelper.GetLocalizedMechanic(key)` 固化的。只要这个静态构造在本地化就绪前被触发
    /// （Harmony 打补丁会 JIT 目标方法，从而提前跑 cctor），整局会话的液体名都会停在英文。
    /// 这里按当前语言现查一次。
    ///
    /// 但**不能**用 `"liquid_" + identifier` 硬拼 key：游戏里 36 种液体有 6 种的词条键与
    /// identifier 不同（见 <see cref="KeyAliases"/>），拼错时 Unity Localization 返回的是
    /// 「No translation found for 'liquid_microbe'」这类提示串而不是空值，直接显示出去就成了
    /// Translation Error。因此先查别名表，再用 <see cref="Lookup"/> 校验结果。
    /// </summary>
    private static string DisplayName(Liquid liquid)
    {
        try
        {
            string id = liquid.identifier ?? "";
            if (id.Length > 0)
            {
                if (KeyAliases.TryGetValue(id, out var alias))
                {
                    string aliased = Lookup(alias);
                    if (aliased.Length > 0) return aliased;
                }

                string live = Lookup("liquid_" + id);
                if (live.Length > 0) return live;
            }

            // 现查失败（缺词条 / 本地化未就绪）才回落到游戏已经固化好的名字
            string n = liquid.displayName ?? "";
            return n.Length > 0 ? n : (id.Length > 0 ? id : "?");
        }
        catch { return "?"; }
    }

    /// <summary>
    /// identifier 与 Mechanic 词条键不一致的 6 种液体（对照 `Liquid.InitLiquid()`：
    /// 词条键以 <c>GetLocalizedMechanic("...")</c> 的实参为准，identifier 是另一套命名）。
    /// </summary>
    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.Ordinal)
    {
        ["microbe"] = "liquid_microbes",
        ["fat"] = "liquid_fat_oil",
        ["coffee_mush"] = "liquid_coffee_bean_mush",
        ["contraceptive"] = "liquid_contraceptive_hormones",
        ["methylol"] = "liquid_n_methylol_base",
        ["debris"] = "liquid_insoluble_debris",
    };

    /// <summary>
    /// 查一条液体词条。查不到、返回键本身、或返回的是「缺翻译」提示串时一律返回空串——
    /// 缺翻译提示绝不能当名字显示。
    /// </summary>
    private static string Lookup(string key)
    {
        try
        {
            string text = LocHelper.GetLocalizedMechanic(key) ?? "";
            if (text.Length == 0) return "";
            if (string.Equals(text, key, StringComparison.Ordinal)) return "";
            if (LocHelper.TryParseMissingTranslation(text, out _, out _)) return "";
            return text;
        }
        catch { return ""; }
    }
}
