using System;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 功能：在「[标签] xxx」后面追加该标签对价格的影响百分比。
///
/// 原版 LabelerHelper.HandleLine 只写标签名：
///     builder.Add("[标签] "); builder.AddLine(itemFeature.GetBaseCurrentDisplay());
/// 原方法照常执行：前缀/终结器只记下"当前正在写哪个特性"，真正的改写发生在
/// TooltipPipeline 的 AddLine 前缀里——把标签名换成短名并补上影响。
/// 不跳过原方法，其它模组对 HandleLine 的补丁和原版后续改动都会保留。
///
/// 价格影响来源（ItemFeature）：
///   - 已揭露(IsExposed)  → GetActualValueModifier() → realCondition.modValue / valueModifier
///   - 未揭露            → GetFakeValueModifier()   → fakeCondition.modValue / preExposeValueModifier
/// 两者皆为百分比整数。
///
/// 注意：**不要**在类上加 [HarmonyPatch]。
/// 官方 ModLoader 加载时会自动对程序集执行 Harmony.PatchAll()，若类上有该特性而方法缺
/// [HarmonyPrefix]，会抛 "Undefined target method" 并导致整个模组 Init 失败（实测踩过）。
/// 这里改为在 Apply() 中显式 Patch 指定方法。
/// </summary>
internal static class LabelImpact
{
    private const string HarmonyId = "item_details.label_impact";

    /// <summary>短名仍然过长时截断，保证一行放得下百分比。</summary>
    private const int MaxNameChars = 24;

    private static Harmony? _harmony;
    private static bool _patched;

    /// <summary>HandleLine 正在写的特性；它的唯一一条 AddLine 被改写后立即清空。</summary>
    private static ItemFeature? _feature;

    internal static void Apply()
    {
        if (_patched) return;
        try
        {
            var target = AccessTools.Method(typeof(LabelerHelper), "HandleLine",
                new[] { typeof(RichTextBuilder), typeof(ItemFeature) });
            if (target == null)
            {
                Log.Warn("LabelImpact", "未找到 LabelerHelper.HandleLine，功能跳过");
                return;
            }
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(LabelImpact), nameof(Enter)),
                finalizer: new HarmonyMethod(typeof(LabelImpact), nameof(Exit)));
            _patched = true;
            Log.Msg("LabelImpact patched LabelerHelper.HandleLine");
        }
        catch (Exception ex)
        {
            Log.Error("LabelImpact.Apply", ex);
        }
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _patched = false;
        _feature = null;
    }

    private static void Enter(ItemFeature itemFeature)
    {
        _feature = ModSettings.PriceImpact ? itemFeature : null;
    }

    private static void Exit()
    {
        _feature = null;
    }

    /// <summary>
    /// 由 TooltipPipeline 的 AddLine 前缀调用。只有原版原文才换成短名；
    /// 其它模组已改过的文本保留原样，只在后面补价格影响。
    /// </summary>
    internal static void TryRewrite(ref string text)
    {
        var feature = _feature;
        if (feature == null || text == null) return;
        _feature = null;

        try
        {
            string name = text;
            string original = "";
            try { original = feature.GetBaseCurrentDisplay() ?? ""; } catch { }
            if (string.Equals(text, original, StringComparison.Ordinal))
                name = ShortName(feature, text);

            string impact = Impact(feature);
            text = impact.Length > 0 ? $"{name} ({impact})" : name;
        }
        catch (Exception ex)
        {
            Log.Warn("LabelImpact.Rewrite", ex);
        }
    }

    /// <summary>
    /// 优先用游戏自己的「短名称」（ItemCondition.display）：部分标签（如情报类）的完整
    /// 显示串是一整段文案，直接塞进 tooltip 会很长，而游戏内部本来就有更短的档位名。
    /// </summary>
    private static string ShortName(ItemFeature feature, string fallback)
    {
        string name = fallback;
        try
        {
            var cond = feature.GetCurrentItemCondition();
            if (cond != null && !string.IsNullOrEmpty(cond.display)) name = cond.display;
        }
        catch { }

        if (name.Length > MaxNameChars)
            name = name.Substring(0, MaxNameChars - 1) + "…";
        return name;
    }

    /// <summary>
    /// 必须用「展示值」口径（modValueDisplay 优先），否则转换型条件
    /// （赝品香烟 -50、正品邮票 +100…）modValue 为 0，影响永远不显示。
    /// </summary>
    private static string Impact(ItemFeature feature)
    {
        int mod;
        string suffix = "%";
        try
        {
            var cond = feature.GetCurrentItemCondition();
            if (cond != null)
            {
                mod = ConditionEffects.DisplayAmount(cond);
                suffix = ConditionEffects.Suffix(cond);
            }
            else
            {
                mod = feature.IsExposed()
                    ? feature.GetActualValueModifier()
                    : feature.GetFakeValueModifier();
            }
        }
        catch { return ""; }

        return mod != 0 ? $"{mod:+0;-0;0}{suffix}" : "";
    }
}
