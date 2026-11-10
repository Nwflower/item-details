using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 把各模块算好的行写进 tooltip：普通行、带进度条的行、成组的行，以及表格所需的折行预算。
/// 只管「怎么写出去」，不知道任何具体物品；逻辑层与区块回放（<see cref="TooltipSections"/>）共用这一处。
/// </summary>
internal static class LineOutput
{
    /// <summary>
    /// 成分表需要多宽的折行预算：取表里最长一行的可见字符数（跳过富文本标签），
    /// 超过当前预算才放宽，且只放到刚好够用。中日韩按原版 0.66 系数折算。
    /// </summary>
    internal static void RequireTableWidth(TooltipSections.Writer builder, List<TooltipLine> table)
    {
        try
        {
            int widest = 0;
            foreach (var line in table)
            {
                int visible = ProgressBarStyle.VisibleLength(line.Text);
                if (line.HasBar)
                    visible += line.BarLength + 1 + ProgressBarStyle.VisibleLength(line.Tail);
                widest = Math.Max(widest, visible);
            }
            if (widest <= 0) return;

            float scale = UISettings.current != null ? UISettings.current.GetCharWidthScale() : 1f;
            int limit = scale > 1f ? (int)(builder.MaxCharWidth * 0.66f) : builder.MaxCharWidth;
            if (widest <= limit) return;
            int need = scale > 1f ? (int)Math.Ceiling(widest / 0.66f) : widest;
            builder.RequireWidth(need + 1);
        }
        catch (Exception ex) { Log.Warn("RequireTableWidth", ex); }
    }


    /// <summary>
    /// 带条的行最多占多少个字符宽：窗口当前预算不够时，在这个范围内允许把窗口撑宽一点
    /// （下面输出时会按整行预留宽度），超过就宁可不画条。
    /// </summary>
    private const int MaxBarLineChars = 34;

    /// <summary>
    /// 充能行的进度条整组处理：每行先算自己放得下的最长条（最短 3 格），整组取最短的那个格数，
    /// 这样条一样长；只要有一行连 3 格也放不下，整组都不画条、只留文字。
    /// 没有充能条时返回 null。
    /// </summary>
    private static Func<TooltipLine, TooltipLine>? GroupBarFit(List<TooltipLine> lines, int budget)
    {
        int group = int.MaxValue;
        foreach (var ln in lines)
        {
            if (!ln.HasBar || ln.DataKind != "recharge") continue;
            int rest = ProgressBarStyle.VisibleLength(ln.Text) + 3 + ProgressBarStyle.VisibleLength(ln.Tail ?? "");
            int fit = 0;
            for (int length = ln.BarLength; length >= 3; length--)
                if (rest + length <= MaxBarLineChars || ProgressBarStyle.WidthForLine(rest + length) <= budget) { fit = length; break; }
            group = Math.Min(group, fit);
        }
        if (group == int.MaxValue) return null;
        return ln => group == 0 ? ln.WithoutBar() : ln.BarLength == group ? ln : ln.WithBarLength(group);
    }

    /// <summary>输出模块已经定好颜色的一组行；返回是否真的写入了内容。</summary>
    internal static bool AppendLines(TooltipSections.Writer builder, List<TooltipLine> lines)
    {
        List<(int Line, string Kind, object Value)>? rowData = null;
        Func<TooltipLine, TooltipLine>? barFit = GroupBarFit(lines, builder.MaxCharWidth);
        for (int i = 0; i < lines.Count; i++)
        {
            var ln = lines[i];
            if (ln.HasBar && ln.DataKind == "recharge") ln = barFit == null ? ln : barFit(ln);
            if (builder.Capturing && ln.DataKind != null && ln.Data != null)
                (rowData ??= new()).Add((i, ln.DataKind, ln.Data));
            if (ln.HasBar)
            {
                // 说明 + 条 + 尾注要排在一行：窗口里没有更宽的行时，尾注会被折到下一行且丢掉缩进
                if (ln.Render != null && ln.Render.Contains("<pos=", StringComparison.Ordinal))
                    builder.RequireWidth(ProgressBarStyle.WidthForLine(
                        ProgressBarStyle.VisibleLength(ln.Text) + ln.BarLength + 3
                        + ProgressBarStyle.VisibleLength(ln.Tail ?? "")) + 4);
                builder.Bar(ln);
            }
            else if (ln.Render != null)
            {
                // 两列行：规范文本比当前折行预算长时，原版会在规范文本里断行，渲染写法随之被切断，
                // 玩家就会看到半截 <color=…> 标签。窗口里没有更宽的行（如没装模组）时尤其常见，所以这里按需放宽预算
                if (ln.Render.Contains("<pos=", StringComparison.Ordinal))
                    builder.RequireWidth(ProgressBarStyle.WidthForLine(Math.Max(
                        ProgressBarStyle.VisibleLength(ln.Text), ProgressBarStyle.VisibleLength(ln.Render) + 6)) + 4);
                builder.AddLine(ln.Text, ln.Color, ln.Render);
            }
            else builder.AddLine(ln.Text, ln.Color);
        }
        if (rowData != null) builder.Field("rowData", rowData.AsReadOnly());
        return lines.Count > 0;
    }

    /// <summary>
    /// 输出进度条行：说明 → 条 → 尾注，一条一行。
    /// 条一律交给游戏自己的 <c>AddProgressBar</c>：它写出原版 <c>[=====     ]</c> 并把这条标成不折行片段。
    /// 逻辑层因此不必知道谁来画——原版窗口用渲染层还原成高清条，排版方从同一个入口读比例。
    /// 条的高精度比例与底色由 <see cref="ProgressBarStyle"/> 在同一个入口记录，原版会丢掉底色。
    /// </summary>
    internal static void AddBarLine(RichTextBuilder builder, TooltipLine ln)
    {
        try
        {
            string? headRender = ln.Render;
            if (ln.Text.Length > 0)
            {
                // 行首文字的呈现写法（例如缩进）：规范文本不变，原版窗口构建时才换入
                int before = headRender != null ? RenderSlices.Count(builder) : -1;
                builder.Add(ln.Text, ln.Color);
                if (before >= 0)
                    RenderSlices.Annotate(builder, before, ln.Text, new RenderSlices.Style(ln.Color), headRender);
            }
            builder.AddProgressBar(ln.Bar, ln.BarLength, ln.Color, ln.BarDark, end: " ");
            ProgressBarStyle.MarkOwnBar(builder);
            // 尾注的呈现写法由产生这一行的模块给出（充能的灰色「+N」、学习条的「当前/上限」），这里只负责换入
            string tail = ln.Tail ?? "";
            if (ln.TailRender != null && tail.Length > 0 && ln.TailRender != tail)
                RenderSlices.AddLine(builder, tail, ln.Color, ln.TailRender);
            else
                AddLine(builder, tail, ln.Color);
        }
        catch (Exception ex)
        {
            Log.Warn("AddBarLine", ex);
        }
    }

    /// <summary>
    /// 原地改写行的进度条：条直接放在文字后面，不再另起一行说明。
    /// 调用方负责为完整行（含先前写出的头部）预留宽度；本方法负责收行，不能再追加 NextLine。
    /// </summary>
    internal static void AddInlineBarLine(RichTextBuilder builder, TooltipLine ln)
    {
        try
        {
            if (ln.Text.Length > 0) builder.Add(ln.Text, ln.Color);
            builder.AddProgressBar(ln.Bar, ln.BarLength, ln.Color, ln.BarDark, end: " ");
            ProgressBarStyle.MarkOwnBar(builder);
            if (!string.IsNullOrEmpty(ln.Tail)) builder.Add(ln.Tail, ln.Color);
            builder.NextLine();
        }
        catch (Exception ex)
        {
            Log.Warn("AddInlineBarLine", ex);
        }
    }

    /// <summary>输出同色的一组纯文本行（未做本地化的模块用）。</summary>
    internal static bool AppendLines(TooltipSections.Writer builder, List<string> lines, RenderHandler.ColorPalette color)
    {
        foreach (string s in lines)
            builder.AddLine(s, color);
        return lines.Count > 0;
    }

    internal static void AddLine(RichTextBuilder builder, string text, RenderHandler.ColorPalette color)
    {
        try
        {
            builder.AddLine(text, false, color, false, false, false, false,
                RenderHandler.ColorPalette.None, RenderHandler.ColorPalette.None, RenderHandler.ColorPalette.None);
            Log.MarkOk("AddLine");   // 恢复正常后清掉失败计数，下次再坏会重新报第一次
        }
        catch (Exception ex)
        {
            Log.Warn("AddLine", ex);
        }
    }
}
