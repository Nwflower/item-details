using System;
using System.Collections.Generic;
using HarmonyLib;
using ItemDetails.Integration;

namespace ItemDetails;

/// <summary>
/// 种子的基因读数：与机器加成行同样的三段式——
///   营养丰富 1   热量 +25%   固有
/// 原版 <c>PlantGeneticsHelper.CreateSeedGeneticsTooltip</c> 一口气写完这一块（表头、性状行、被移除的固有性状），
/// 这里整体换成两列写法：性状名（带等级）、效果、来源/状态。
/// 等级默认写阿拉伯数字，设置里打开「基因等级用罗马数字」就改用原版
/// <c>PlantGeneticsHelper.RankLabel</c>（I、II、III…）。
/// 规范文本逐行等于原版文字，只有一处合并：固有降级的性状，原版会另起一行写「X（已移除）」，
/// 这里并进本行的「固有(2→1)」，既不再出现「又在又不在」的两行，也不用把这段信息只挂在渲染层。
/// 列位置只在渲染层；出错时让原方法照常运行。
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
        /// <summary>固有性状已被完全移除（现有 0 级）：整行灰色加删除线，没有效果列。</summary>
        internal bool Removed;
    }

    /// <summary>基因读数的框宽上限：原版固定 60。效果文字再长也不撑过它，超出的在效果列内折行。</summary>
    private const int CapWidth = 60;

    /// <summary>原版给种子基因读数的框宽是固定 60，右边常空一大截；改成按最宽的一行算，但不超过上限。</summary>
    private static void WidthPostfix(GameItem item, int defaultWidth, ref int __result)
    {
        try
        {
            if (!Applies(item) || __result <= defaultWidth) return;
            Read(item, out string header, out var rows, out _);
            var plan = MakePlan(rows);
            int widest = Math.Max(ProgressBarStyle.VisibleLength(header), plan.LineChars);
            __result = Math.Max(defaultWidth, Math.Min(__result, ProgressBarStyle.WidthForLine(widest) + 4));
        }
        catch (Exception ex) { Log.Warn("SeedGenes.Width", ex); }
    }

    /// <summary>每行效果文字折成几段，以及整行（标签 + 效果 + 注记）最多占多少个可见字符。</summary>
    private sealed class Plan
    {
        internal readonly List<string[]> Segments = new();
        internal int LineChars;
    }

    /// <summary>
    /// 效果文字按框宽上限折行：多年生这类性状的效果是一整段话加三个百分比，不折行会把框撑出屏幕。
    /// 折行口径与原版 Build 一致（中日韩一个字符算 1，预算是 maxCharWidth×0.66），
    /// 效果列可用的字符数 = 预算 − 最长的标签 − 最长的注记 − 列间距。
    /// </summary>
    private static Plan MakePlan(List<Row> rows)
    {
        var plan = new Plan();
        float scale = UISettings.current != null ? UISettings.current.GetCharWidthScale() : 1f;
        int limit = scale > 1f ? Math.Max(8, (int)(CapWidth * 0.66f)) : CapWidth;
        int maxLabel = 0, maxNote = 0;
        foreach (var row in rows)
        {
            maxLabel = Math.Max(maxLabel, ProgressBarStyle.VisibleLength(row.Label));
            maxNote = Math.Max(maxNote, ProgressBarStyle.VisibleLength(row.Note));
        }
        int segLimit = Math.Max(8, limit - maxLabel - maxNote - 2);
        int maxSeg = 0;
        foreach (var row in rows)
        {
            string effect = row.Effect;
            // 效果文字带富文本标签时不折（按原始字符数切会把标签切断），保持一段
            bool plain = effect.IndexOf('<') < 0;
            string wrapped = plain && effect.Length > segLimit ? TextWrap.WrapPhrases(effect, segLimit) : effect;
            var segs = wrapped.Split('\n');
            plan.Segments.Add(segs);
            foreach (var s in segs) maxSeg = Math.Max(maxSeg, ProgressBarStyle.VisibleLength(s));
        }
        plan.LineChars = maxLabel + maxSeg + maxNote + 2;
        return plan;
    }

    private static bool Applies(GameItem? item)
        => ModSettings.TooltipFormatting && item != null && item.IsTag("SEED_ITEM_TAG")
           && HydroponicHelper.GetPlant(item) != null && PlantGeneticsHelper.IsScannerOn();

    private static void Read(GameItem item, out string header, out List<Row> rows, out int traitCount)
    {
            PlantDefinition plant = HydroponicHelper.GetPlant(item);
            var traits = PlantGeneticsHelper.GetTraits(item);
            int heat = PlantGeneticsHelper.GetHeat(item);
            int complexity = PlantGeneticsHelper.GetComplexity(item);
            header = heat > 0 ? PlantGeneticsText.ReadoutHeaderHeat(complexity, heat) : PlantGeneticsText.ReadoutHeader(complexity);

            rows = new List<Row>();
            foreach (var (trait, rank) in traits)
            {
                int innateRank = plant.GetInnateRank(trait.id);
                bool innate = innateRank >= rank;
                // 固有 2 级、现在只剩 1 级：原版在同一行标「固有」，再另起一行写「已移除」，
                // 读起来像又在又不在。这里把两者合并成注记「固有(2→1)」，并写进规范文本。
                bool lowered = !trait.locked && innateRank > rank && rank > 0;
                string? loweredNote = lowered
                    ? Loc.T("seed.innate_lowered", "Innate ({0}→{1})", RankText(innateRank), RankText(rank)) : null;
                bool usable = PlantGeneticsHelper.IsTraitUsable(item, trait, out _);
                string name = SeedInfo.LocalizeInfusion(trait, trait.displayName);
                if (trait.maxRank > 1) name += " " + RankText(rank);
                string effect = SeedInfo.LocalizeInfusion(trait, PlantGeneticsHelper.DescribeEffect(trait, rank));

                // 规范文本：原版同一行（标签带「（固有）」等后缀）；降级的用合并注记代替「（固有）」后缀
                string label = name;
                if (trait.locked) label = PlantGeneticsText.OriginLocked(label);
                else if (lowered) label += GroupNote(loweredNote!);
                else if (innate) label = PlantGeneticsText.OriginInnate(label);
                if (!usable) label = PlantGeneticsText.NoEffectSuffix(label);

                var notes = new List<string>();
                if (trait.locked) notes.Add(Tag(PlantGeneticsText.OriginLocked));
                else if (lowered) notes.Add(loweredNote!);
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
            traitCount = rows.Count;

            // 原版的「已移除」行：只有完全移除（现有 0 级）的还需要单列，画成删除线；
            // 只是降级的已并入上面那一行，任何环境下都不再多写一行（规范文本不随第三方是否在场变化）。
            foreach (var innateTrait in plant.innateTraits)
            {
                var plantTrait = PlantTraitRegistry.Get(innateTrait.Key);
                if (plantTrait == null) continue;
                int current = PlantGeneticsHelper.GetTraitRank(item, plantTrait.id);
                if (current >= innateTrait.Value || current > 0) continue;
                string name = SeedInfo.LocalizeInfusion(plantTrait, plantTrait.displayName);
                rows.Add(new Row
                {
                    Canonical = PlantGeneticsText.ReadoutInnateRemoved(name),
                    Label = name,
                    Note = Loc.T("seed.innate_removed", "Removed"),
                    Removed = true,
                });
            }
    }

    private static bool Prefix(RichTextBuilder builder, GameItem item)
    {
        try
        {
            if (builder == null || !Applies(item)) return true;
            Read(item, out string header, out var rows, out int traitCount);

            // ---- 以下才动笔 ----
            var plan = MakePlan(rows);
            float labelEm = 0f, effectEm = 0f, noteEm = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                labelEm = Math.Max(labelEm, WaterColumns.TextEm(rows[i].Label));
                foreach (var seg in plan.Segments[i]) effectEm = Math.Max(effectEm, WaterColumns.TextEm(seg));
                noteEm = Math.Max(noteEm, WaterColumns.TextEm(rows[i].Note));
            }
            float col2 = labelEm + 0.7f;
            float col3 = col2 + effectEm + 0.7f;
            float noteEnd = col3 + noteEm;   // 第三列右对齐到这一条线

            var output = new TooltipSections.Writer(builder, item, "seed-genes", TooltipGroupIds.Plant);
            string headerRender = header.TrimEnd().TrimEnd('：', ':');
            if (headerRender != header) output.AddLine(header, RenderHandler.ColorPalette.LightPurple, headerRender);
            else output.AddLine(header, RenderHandler.ColorPalette.LightPurple);

            // 原版在「没有任何性状」时才写这一行；被移除的固有性状是另外的行，不算在这里
            if (traitCount == 0)
                output.AddLine(PlantGeneticsText.ReadoutNoTraits(), RenderHandler.ColorPalette.Gray);
            // 整块按折行后的最宽一行留框宽；规范文本仍是原版一行，不参与宽度
            if (rows.Count > 0) output.RequireWidth(ProgressBarStyle.WidthForLine(plan.LineChars) + 4);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var segs = plan.Segments[i];
                if (row.Removed)
                {
                    // 整行（含列间空隙）一条灰色删除线。TMP 的 <s> 画在字体自带的高度，对中文偏低；
                    // 这里改成回到行首、用「─」（字高正中）铺满整行宽度压在字上。
                    string gone = RenderHandler.ApplyRichTextColorTag(row.Label, RenderHandler.ColorPalette.Gray)
                        + ModuleEffectAnnotation.Pos(noteEnd - WaterColumns.TextEm(row.Note))
                        + RenderHandler.ApplyRichTextColorTag(row.Note, RenderHandler.ColorPalette.Gray);
                    float dash = WaterColumns.TextEm("\u2500");
                    if (dash >= 0.3f)
                    {
                        int n = Math.Max(1, (int)Math.Round(noteEnd / dash));   // 取整不进位：进位会让线越过右端
                        gone += ModuleEffectAnnotation.Pos(0f)
                            + RenderHandler.ApplyRichTextColorTag(new string('\u2500', n), RenderHandler.ColorPalette.Gray);
                        // 画线的字符也算折行预算，不放宽会在行中间断开
                        output.RequireWidth(ProgressBarStyle.WidthForLine(
                            ProgressBarStyle.VisibleLength(row.Label) + ProgressBarStyle.VisibleLength(row.Note) + n) + 4);
                    }
                    else gone = "<s>" + gone + "</s>";
                    output.AddLine(row.Canonical, RenderHandler.ColorPalette.Gray, gone);
                    continue;
                }
                string fore = row.Negative ? ColumnLayout.NegativeText : ColumnLayout.PositiveText;
                // 首行：标签 + 效果第一段 + 注记；效果折出来的后续段各占一行，对齐到效果列
                string render = "<color=" + fore + ">" + row.Label + "</color>"
                    + ModuleEffectAnnotation.Pos(col2)
                    + "<color=" + fore + ">" + segs[0] + "</color>";
                if (row.Note.Length > 0)
                    render += ModuleEffectAnnotation.Pos(noteEnd - WaterColumns.TextEm(row.Note)) + RenderHandler.ApplyRichTextColorTag(row.Note, RenderHandler.ColorPalette.Gray);
                for (int s = 1; s < segs.Length; s++)
                    render += "\n" + ModuleEffectAnnotation.Pos(col2) + "<color=" + fore + ">" + segs[s] + "</color>";
                output.AddLine(row.Canonical, row.Negative ? RenderHandler.ColorPalette.LightRed : RenderHandler.ColorPalette.LightGreen, render);
            }
            output.Emit();
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("SeedGenes", ex);
            return true;
        }
    }

    /// <summary>
    /// 等级文字：默认阿拉伯数字，打开设置后改用原版
    /// <see cref="PlantGeneticsHelper.RankLabel"/> 的罗马数字（I、II、III…）。
    /// 罗马数字由 ASCII 的 I/V/X/L/C/D/M 拼成，不涉及字体缺字；
    /// 取不到或为空时仍回落阿拉伯数字，不让等级整格消失。
    /// </summary>
    private static string RankText(int rank)
    {
        rank = Math.Max(1, rank);
        if (!ModSettings.GeneRankRoman) return rank.ToString();
        try
        {
            string roman = PlantGeneticsHelper.RankLabel(rank);
            return string.IsNullOrEmpty(roman) ? rank.ToString() : roman;
        }
        catch { return rank.ToString(); }
    }

    /// <summary>
    /// 降级注记写成「括号尾组」。美化只把行尾那一组括号右对齐成一列（原版「（固有）」走的就是这条路），
    /// 注记散在名称中间会被它整条并进标签。本地化模板自带的括号去掉只留一组：
    /// 中文用全角括号并紧贴名称，西文用半角括号并隔一格。
    /// </summary>
    private static string GroupNote(string note)
    {
        string text = note.Replace("(", " ").Replace(")", " ").Replace("（", " ").Replace("）", " ");
        text = string.Join(" ", text.Split(new[] { ' ', CellSeparator }, StringSplitOptions.RemoveEmptyEntries));
        bool wide = false;
        foreach (char ch in text) if (ch >= '\u2E80') { wide = true; break; }
        return (wide ? "（" : " (") + text + (wide ? "）" : ")");
    }

    /// <summary>规范文本的格分隔符：全角空格。</summary>
    private const char CellSeparator = '\u3000';

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
