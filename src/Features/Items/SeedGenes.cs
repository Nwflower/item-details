using System;
using System.Collections.Generic;
using HarmonyLib;
using ItemDetails.Integration;

namespace ItemDetails;

/// <summary>
/// 种子的基因读数：与机器加成行同样的三段式——
///   营养丰富1   热量 +25%   固有
/// 原版 <c>PlantGeneticsHelper.CreateSeedGeneticsTooltip</c> 一口气写完这一块（表头、性状行、被移除的固有性状），
/// 这里整体换成两列写法：性状名（带阿拉伯数字等级）、效果、来源/状态。
/// 规范文本逐行等于原版文字（第三方读到的不变），列位置只在渲染层；出错时让原方法照常运行。
/// </summary>
internal static class SeedGenes
{
    private const string Probe = "\u0001";

    internal static void Install(PatchScope scope)
    {
        scope.Patch(AccessTools.Method(typeof(PlantGeneticsHelper), "CreateSeedGeneticsTooltip",
                new[] { typeof(RichTextBuilder), typeof(GameItem) }),
            // 与 ModuleItemRows 一样排在最后：别的模组的前缀先跑，我们再跳过原方法
            prefix: new HarmonyMethod(typeof(SeedGenes), nameof(Prefix)) { priority = Priority.Last });
        var width = AccessTools.Method(typeof(PlantGeneticsHelper), "GetTooltipWidth", new[] { typeof(GameItem), typeof(int) });
        if (width != null) scope.Patch(width, postfix: new HarmonyMethod(typeof(SeedGenes), nameof(WidthPostfix)));
    }

    private sealed class Row
    {
        internal string Canonical = "", Label = "", Effect = "", Note = "";
        internal bool Negative;
    }

    /// <summary>原版给种子基因读数的框宽是固定 60，右边常空一大截；改成按最宽的一行算。</summary>
    private static void WidthPostfix(GameItem item, int defaultWidth, ref int __result)
    {
        try
        {
            if (!Applies(item) || __result <= defaultWidth) return;
            Read(item, out string header, out var rows, out _);
            int widest = ProgressBarStyle.VisibleLength(header);
            foreach (var row in rows) widest = Math.Max(widest, ProgressBarStyle.VisibleLength(row.Canonical));
            __result = Math.Max(defaultWidth, ProgressBarStyle.WidthForLine(widest) + 4);
        }
        catch (Exception ex) { Log.Warn("SeedGenes.Width", ex); }
    }

    private static bool Applies(GameItem? item)
        => ModSettings.TooltipFormatting && item != null && item.IsTag("SEED_ITEM_TAG")
           && HydroponicHelper.GetPlant(item) != null && PlantGeneticsHelper.IsScannerOn();

    private static void Read(GameItem item, out string header, out List<Row> rows, out List<string> removed)
    {
            PlantDefinition plant = HydroponicHelper.GetPlant(item);
            var traits = PlantGeneticsHelper.GetTraits(item);
            int heat = PlantGeneticsHelper.GetHeat(item);
            int complexity = PlantGeneticsHelper.GetComplexity(item);
            header = heat > 0 ? PlantGeneticsText.ReadoutHeaderHeat(complexity, heat) : PlantGeneticsText.ReadoutHeader(complexity);

            rows = new List<Row>();
            foreach (var (trait, rank) in traits)
            {
                bool innate = plant.GetInnateRank(trait.id) >= rank;
                bool usable = PlantGeneticsHelper.IsTraitUsable(item, trait, out _);
                string name = trait.maxRank > 1 ? WithRank(trait.displayName, rank) : trait.displayName;
                string effect = PlantGeneticsHelper.DescribeEffect(trait, rank);

                // 规范文本：原版同一行（标签带「（固有）」等后缀）
                string label = name;
                if (trait.locked) label = PlantGeneticsText.OriginLocked(label);
                else if (innate) label = PlantGeneticsText.OriginInnate(label);
                if (!usable) label = PlantGeneticsText.NoEffectSuffix(label);

                var notes = new List<string>();
                if (trait.locked) notes.Add(Tag(PlantGeneticsText.OriginLocked));
                else if (innate) notes.Add(Tag(PlantGeneticsText.OriginInnate));
                if (!usable) notes.Add(Tag(PlantGeneticsText.NoEffectSuffix));

                rows.Add(new Row
                {
                    Canonical = PlantGeneticsText.ReadoutTraitLine(label, effect),
                    Label = name,
                    Effect = effect,
                    Note = string.Join(Loc.T("list.sep", ", "), notes),
                    Negative = trait.IsNegative,
                });
            }

            removed = new List<string>();
            foreach (var innateTrait in plant.innateTraits)
            {
                var plantTrait = PlantTraitRegistry.Get(innateTrait.Key);
                if (plantTrait != null && PlantGeneticsHelper.GetTraitRank(item, plantTrait.id) < innateTrait.Value)
                    removed.Add(PlantGeneticsText.ReadoutInnateRemoved(plantTrait.displayName));
            }
    }

    private static bool Prefix(RichTextBuilder builder, GameItem item)
    {
        try
        {
            if (builder == null || !Applies(item)) return true;
            Read(item, out string header, out var rows, out var removed);

            // ---- 以下才动笔 ----
            float labelEm = 0f, effectEm = 0f, noteEm = 0f;
            foreach (var row in rows)
            {
                labelEm = Math.Max(labelEm, WaterColumns.TextEm(row.Label));
                effectEm = Math.Max(effectEm, WaterColumns.TextEm(row.Effect));
                noteEm = Math.Max(noteEm, WaterColumns.TextEm(row.Note));
            }
            float col2 = labelEm + 0.7f;
            float col3 = col2 + effectEm + 0.7f;
            float noteEnd = col3 + noteEm;   // 第三列右对齐到这一条线

            var output = new TooltipSections.Writer(builder, item, "seed-genes", TooltipGroupIds.Plant);
            string headerRender = header.TrimEnd().TrimEnd('：', ':');
            if (headerRender != header) output.AddLine(header, RenderHandler.ColorPalette.LightPurple, headerRender);
            else output.AddLine(header, RenderHandler.ColorPalette.LightPurple);

            if (rows.Count == 0)
                output.AddLine(PlantGeneticsText.ReadoutNoTraits(), RenderHandler.ColorPalette.Gray);
            foreach (var row in rows)
            {
                string fore = row.Negative ? ColumnLayout.NegativeText : ColumnLayout.PositiveText;
                string render = "<color=" + fore + ">" + row.Label + "</color>"
                    + ModuleEffectAnnotation.Pos(col2)
                    + "<color=" + fore + ">" + row.Effect + "</color>";
                if (row.Note.Length > 0)
                    render += ModuleEffectAnnotation.Pos(noteEnd - WaterColumns.TextEm(row.Note)) + RenderHandler.ApplyRichTextColorTag(row.Note, RenderHandler.ColorPalette.Gray);
                output.RequireWidth(ProgressBarStyle.WidthForLine(ProgressBarStyle.VisibleLength(row.Canonical)) + 4);
                output.AddLine(row.Canonical, row.Negative ? RenderHandler.ColorPalette.LightRed : RenderHandler.ColorPalette.LightGreen, render);
            }
            foreach (string text in removed)
                output.AddLine(text, RenderHandler.ColorPalette.Gray);
            output.Emit();
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("SeedGenes", ex);
            return true;
        }
    }

    /// <summary>性状名 + 阿拉伯数字等级；名字以字母或数字结尾（西文）时隔一格，中日韩直接相连。</summary>
    private static string WithRank(string name, int rank)
    {
        string number = Math.Max(1, rank).ToString();
        char last = name.Length > 0 ? name[name.Length - 1] : ' ';
        return last < 128 && char.IsLetterOrDigit(last) ? name + " " + number : name + number;
    }

    /// <summary>从「X（固有）」「X (Innate)」这类本地化包装里取出括号里的词：用探针字符定位 X。</summary>
    private static string Tag(Func<string, string> wrap)
    {
        try
        {
            string wrapped = wrap(Probe);
            int at = wrapped.IndexOf(Probe, StringComparison.Ordinal);
            if (at < 0) return wrapped;
            string text = wrapped.Remove(at, Probe.Length);
            return text.Trim(' ', '（', '）', '(', ')', ':', '：', '-');
        }
        catch { return ""; }
    }
}
