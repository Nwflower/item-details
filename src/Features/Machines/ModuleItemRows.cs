using System;
using System.Collections.Generic;
using HarmonyLib;
using ItemDetails.Integration;

namespace ItemDetails;

/// <summary>
/// 模块物品的「模组效果」块：与机器上的加成行同一种样式——
///   [性能] +15%   基础 +10%
///   [特殊] 移除梦帽菇的违禁品状态
/// 原版 <c>ModuleHelper.CreateModuleTooltip</c> 一口气写完这一块，这里整体换成色块写法；
/// 规范文本逐行等于原版文字，色块和列位置只在渲染层，色块不可用时退回原版写法。
/// 所有内容先算好再动笔，中途出错就让原方法照常运行，不会留下半截。
/// </summary>
internal static class ModuleItemRows
{
    internal static void Install(PatchScope scope)
    {
        scope.Patch(AccessTools.Method(typeof(ModuleHelper), "CreateModuleTooltip",
                new[] { typeof(RichTextBuilder), typeof(GameItem) }),
            // 排在最后：别的模组（物品详情美化）给这个方法记区间用的前缀要先跑，
            // 我们再跳过原方法，它的后缀才不会在没有前缀配对时收尾
            prefix: new HarmonyMethod(typeof(ModuleItemRows), nameof(Prefix)) { priority = Priority.Last });
    }

    private sealed class Row
    {
        internal readonly string Canonical, Render;
        internal readonly RenderHandler.ColorPalette Color;
        internal Row(string canonical, string render, RenderHandler.ColorPalette color, bool special = false)
        {
            Canonical = canonical;
            Render = render;
            Color = color;
        }
    }

    private static readonly HashSet<string> Descriptions = new(StringComparer.Ordinal);

    /// <summary>这段文字是否是正在写的模块特殊效果说明（管线据此按物品描述的样式处理）。</summary>
    internal static bool IsEffectDescription(string text) => Descriptions.Contains(text);

    private static bool Prefix(RichTextBuilder builder, GameItem item)
    {
        Descriptions.Clear();
        try
        {
            if (!ModSettings.ModuleEffects || builder == null || item == null) return true;
            if (!item.IsTag("MODULE_TAG")) return true;

            var rows = new List<Row>();
            string baseLabel = LocHelper.GetLocalizedMechanic("mech_base_stat") ?? "";
            string special = Loc.T("module.special_label", "Special");
            float col2 = Math.Max(ModuleEffectAnnotation.ColumnEm(),
                ModuleEffectAnnotation.ChipEm(special) + 0.7f);

            AddStat(rows, "mech_module_stat_performance", item, "PERFORMANCE", baseLabel, col2);
            AddStat(rows, "mech_module_stat_efficiency", item, "EFFICIENCY", baseLabel, col2);
            AddStat(rows, "mech_module_stat_quality", item, "QUALITY", baseLabel, col2);

            if (item.IsTag("ADDITIONAL_EFFECT_STRING"))
            {
                string raw = item.GetTagReadonly("ADDITIONAL_EFFECT_STRING").GetString() ?? "";
                string bulleted = LocHelper.BulletLines(raw);
                var plain = (raw ?? "").Replace("\r\n", "\n").Split('\n');
                var canon = (bulleted ?? "").Replace("\r\n", "\n").Split('\n');
                int k = 0;
                foreach (string line in plain)
                {
                    string text = line.Trim().TrimStart('-', '·', '•', ' ');
                    if (text.Length == 0) continue;
                    string c = k < canon.Length ? canon[k] : text;
                    k++;
                    rows.Add(new Row(c, SpecialRender(special, text, col2), RenderHandler.ColorPalette.LightCyan, true));
                }
            }

            string stuck = item.IsTag("MODULE_STUCK_TAG") ? LocHelper.GetLocalizedMechanic("mech_module_stuck") : "";

            // 特殊效果（MODULE_EFFECT1/2）：名字走色块，说明缩进在下面
            var effects = new List<(string Name, string Description)>();
            foreach (string tag in new[] { "MODULE_EFFECT1_TAG", "MODULE_EFFECT2_TAG" })
            {
                if (!item.IsTag(tag)) continue;
                string id = item.GetTagReadonly(tag).GetString();
                if (id != null && ModuleEffectHelper.moduleEffects.TryGetValue(id, out var effect))
                    effects.Add((effect.displayName, effect.description));
            }

            if (rows.Count == 0 && effects.Count == 0 && stuck.Length == 0) return false;   // 空块什么都不写；有内容时表头也不写（每项都带色块，表头多余）

            // ---- 以下才动笔 ----
            var output = new TooltipSections.Writer(builder, item, "module-effect", TooltipGroupIds.Module);
            foreach (var row in rows)
            {
                output.RequireWidth(ProgressBarStyle.WidthForLine(ProgressBarStyle.VisibleLength(row.Canonical)) + 4);
                output.AddRendered(row.Canonical, row.Color, row.Render);
                output.NextLine();
            }
            output.Emit();

            foreach (var (name, description) in effects)
            {
                var effectOut = new TooltipSections.Writer(builder, item, "module-effect-special", TooltipGroupIds.Module);
                effectOut.AddRendered(name, RenderHandler.ColorPalette.LightBlue, SpecialRender(special, name, col2));
                effectOut.NextLine();
                effectOut.Emit();
                // 说明按物品描述处理：灰色、缩小、不缩进，折行规则也一样（管线认得这段文字）
                if (!string.IsNullOrEmpty(description))
                {
                    Descriptions.Add(description);
                    builder.AddLine(description, true, RenderHandler.ColorPalette.Gray);
                }
            }
            if (stuck.Length > 0) builder.AddLine(stuck, true, RenderHandler.ColorPalette.LightRed);
            Descriptions.Clear();
            return false;
        }
        catch (Exception ex)
        {
            Descriptions.Clear();
            Log.Warn("ModuleItemRows", ex);
            return true;
        }
    }

    private static string SpecialRender(string label, string text, float col2)
        => ModuleEffectAnnotation.ChipMarkup(label, 'p', ColumnLayout.PurpleText)
           + ModuleEffectAnnotation.Pos(col2) + text;

    private static void AddStat(List<Row> rows, string key, GameItem item, string stat, string baseLabel, float col2)
    {
        int b = item.GetTagReadonly("BONUS_PERCENTAGE_" + stat + "_INT").GetInt();
        int t = item.GetTagReadonly("TEMP_PERCENTAGE_" + stat + "_INT").GetInt();
        if (b == 0 && t == 0) return;

        string name = LocHelper.GetLocalizedMechanic(key) ?? "";
        string sign = t > 0 ? "+" : "";
        string canonical = LocHelper.BulletLine($"{name} {sign}{t}%");
        bool positive = t >= 0;
        string value = $"{sign}{t}%";
        string note = "";
        if (b != t)
        {
            string signB = b > 0 ? "+" : "";
            canonical += $" ({baseLabel}: {signB}{b}%)";
            note = $"{baseLabel} {signB}{b}%";
        }

        string fore = positive ? ColumnLayout.PositiveText : ColumnLayout.NegativeText;
        string valueColor = positive ? ColumnLayout.ValuePositive : ColumnLayout.ValueNegative;
        string render = ModuleEffectAnnotation.ChipMarkup(name, positive ? '+' : '-', fore)
            + ModuleEffectAnnotation.Pos(col2) + "<color=" + valueColor + ">" + value + "</color>";
        if (note.Length > 0)
            render += ModuleEffectAnnotation.Pos(col2 + WaterColumns.TextEm(value) + 0.7f)
                + RenderHandler.ApplyRichTextColorTag(note, RenderHandler.ColorPalette.Gray);
        rows.Add(new Row(canonical, render, positive ? RenderHandler.ColorPalette.LightCyan : RenderHandler.ColorPalette.LightOrange, false));
    }
}
