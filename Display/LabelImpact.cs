using System;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 功能：在「[标签] xxx」后面追加该标签对价格的影响百分比。
///
/// 原版 LabelerHelper.HandleLine 只写标签名：
///     builder.Add("[标签] "); builder.AddLine(itemFeature.GetBaseCurrentDisplay());
/// 我们用 Prefix 拦截，把「显示名 + 价格影响」一起写进去，并跳过原方法。
///
/// 价格影响来源（ItemFeature）：
///   - 已揭露(IsExposed)  → GetActualValueModifier() → realCondition.modValue / valueModifier
///   - 未揭露            → GetFakeValueModifier()   → fakeCondition.modValue / preExposeValueModifier
/// 两者皆为百分比整数。
///
/// 安全性：补丁内任何异常都返回 true（走原版逻辑），不会让 tooltip 变空。
///
/// 注意：**不要**在类上加 [HarmonyPatch]。
/// 官方 ModLoader 加载时会自动对程序集执行 Harmony.PatchAll()，若类上有该特性而方法缺
/// [HarmonyPrefix]，会抛 "Undefined target method" 并导致整个模组 Init 失败（实测踩过）。
/// 这里改为在 Apply() 中显式 Patch 指定方法。
/// </summary>
internal static class LabelImpact
{
    private const string HarmonyId = "item_details.label_impact";

    /// <summary>色块背景（深蓝）与文字（白）。TMP mark 用 RRGGBBAA。</summary>
    private const string ChipBg = "#1F3A6EFF";
    private const string ChipFg = "#FFFFFFFF";

    private static Harmony? _harmony;
    internal static bool Patched { get; private set; }

    internal static void Apply()
    {
        if (Patched) return;
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
                prefix: new HarmonyMethod(typeof(LabelImpact), nameof(Prefix)));
            Patched = true;
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
        Patched = false;
    }

    /// <summary>返回 false 表示跳过原方法（我们自己写了）。</summary>
    private static bool Prefix(RichTextBuilder builder, ItemFeature itemFeature)
    {
        try
        {
            if (builder == null || itemFeature == null) return true;
            if (!ModSettings.PriceImpact) return true;   // 开关关闭 → 走原版
            // 含水容器不再隐藏标签行：隐藏要靠 Harmony 干预全局 AddLine，
            // 容易与其它模组的补丁互相影响，改为照常输出。

            // 复刻原版配色逻辑
            var color = RenderHandler.ColorPalette.White;
            try
            {
                if (itemFeature.IsFeatureDiscovered())
                {
                    color = itemFeature.IsFeatureMatch()
                        ? RenderHandler.ColorPalette.LightGreen
                        : RenderHandler.ColorPalette.LightRed;
                }
            }
            catch { }

            // 显示名：优先用游戏自己的「短名称」（ItemCondition.display），
            // 没有短名时才回落到完整显示串；过长再做一次截断。
            // 原因：部分标签（如情报类）的完整显示串是一整段文案，
            // 直接塞进 tooltip 会很长，而游戏内部本来就有更短的档位名。
            string name = "";
            try
            {
                var cond = itemFeature.GetCurrentItemCondition();
                if (cond != null && !string.IsNullOrEmpty(cond.display))
                    name = cond.display;
            }
            catch { }

            if (name.Length == 0)
            {
                try { name = itemFeature.GetBaseCurrentDisplay() ?? ""; } catch { }
            }

            // 仍然过长则截断，保证一行放得下百分比
            const int MaxNameChars = 24;
            if (name.Length > MaxNameChars)
                name = name.Substring(0, MaxNameChars - 1) + "…";

            if (name.Length == 0) return true;

            // 价格影响：必须用「展示值」口径（modValueDisplay 优先），否则
            // 转换型条件（赝品香烟 -50、正品邮票 +100…）modValue 为 0，影响永远不显示。
            int mod = 0;
            string suffix = "%";
            try
            {
                var cond = itemFeature.GetCurrentItemCondition();
                if (cond != null)
                {
                    mod = ConditionEffects.DisplayAmount(cond);
                    suffix = ConditionEffects.Suffix(cond);
                }
                else
                {
                    mod = itemFeature.IsExposed()
                        ? itemFeature.GetActualValueModifier()
                        : itemFeature.GetFakeValueModifier();
                }
            }
            catch { mod = 0; }

            // 标签行保持纯文本：色块改由「物品类别」承担（见 ItemTypeInline）。
            string prefix = "";
            try { prefix = LocHelper.GetLocalizedMechanic("mech_label_tooltip_prefix") ?? ""; } catch { }

            string detail = mod != 0 ? $"{name} ({mod:+0;-0;0}{suffix})" : name;

            // 前缀为空时不要拼空格，否则整行会多出一个前导空格（视觉缩进错位）
            if (prefix.Length > 0) builder.Add(prefix.TrimEnd() + " ", color);
            builder.AddLine(detail, true, color);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("LabelImpact.Prefix", ex);
            return true;   // 出问题就退回原版
        }
    }
}
