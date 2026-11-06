using System;
using System.Collections.Generic;
using System.Globalization;
using ItemDetails.Integration;
using TMPro;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 水质成分表排版。逻辑层写规范输出：单元格之间用全角空格分隔，第三方读到、重排的都是这一份；
/// 原版窗口构建时由 <see cref="RenderSlices"/> 换成 TMP 的 &lt;pos&gt; 三列，精确定位，不靠空格补位。
///
/// 依据：反编译正式版的 <c>TMP_Text.ValidateHtmlTag</c>，这版 TMP 支持
/// <c>&lt;pos&gt;</c>（像素 / em / 百分比三种单位，走 <c>m_xAdvance = …</c>）。
/// 用 em（字号倍数）定位与字体无关，中英文混排也不会错位：
///
///     ██████ 2000/2000mL 纯水
///     水       1998.0mL   99.90%
///     化学污染物  0.6mL    0.03%
///
/// 表头只有体积条与档位，与原版空瓶的写法一致；列宽按实测字宽算（名称列取最宽的名称），
/// 落点交给 &lt;pos&gt;，所以每条成分的体积、百分比都在同一条竖直线上。
///
/// 画哪些行由 <see cref="WaterTable"/> 决定，这里只负责落位：液体容器与固体化学品走同一份
/// 行数据，只是表头、用量单位和末尾说明不同。
/// </summary>
internal static class WaterColumns
{
    /// <summary>数字列与上一列之间的间距（em）。</summary>
    private const float ColumnGapEm = 0.9f;

    /// <summary>规范输出里格子之间的分隔：全角空格（U+3000），排版方按它切列。</summary>
    private const char CellSeparator = '\u3000';

    /// <summary>水行固定水蓝：整张表里只有它代表「水本身」。</summary>
    private const RenderHandler.ColorPalette WaterColor = RenderHandler.ColorPalette.LightBlue;

    /// <summary>
    /// 杂质一律白色，不按液体种类上色。它们只回答「水里还有什么」，
    /// 逐种配彩会让末位那几行比水本身更抢眼，也让人误以为颜色另有一层含义。
    /// 试剂、溶剂、固体化学品里的水同样走这一类。
    /// </summary>
    private const RenderHandler.ColorPalette ImpurityColor = RenderHandler.ColorPalette.White;

    /// <summary>药物活性成分是玩家真正要读的量，按出现顺序取色以示区分。</summary>
    private static readonly RenderHandler.ColorPalette[] ActiveColors =
    {
        RenderHandler.ColorPalette.LightOrange,
        RenderHandler.ColorPalette.LightTurquoise,
        RenderHandler.ColorPalette.Purple,
        RenderHandler.ColorPalette.ERed,
        RenderHandler.ColorPalette.Orange,
        RenderHandler.ColorPalette.LightCyan,
    };

    // ---- 字体测量 ----
    private static TMP_Text? _probe;
    private static readonly Dictionary<string, float> Cache = new(StringComparer.Ordinal);
    private const float ProbeFontSize = 16f;
    /// <summary>探针当前绑定的字体；tooltip 字体变了必须换过来，否则列宽是按别的字体算的。</summary>
    private static TMP_FontAsset? _probeFont;
    /// <summary>宽度缓存上限：键是具体文案（体积数字一直在变），不清会随会话无限增长。</summary>
    private const int CacheLimit = 512;

    /// <summary>最贴近 tooltip 正文的字体：内容物格子模板优先，其次是分类标签用的正文模板。</summary>
    private static TMP_FontAsset? PreferredFont()
    {
        try { return ContentsGrid.TemplateFont ?? TypeBadge.TooltipFont; }
        catch { return null; }
    }

    private static void Ensure()
    {
        if (_probe != null)
        {
            // 首次量宽可能早于 tooltip 模板就位，探针会落到场景里任意一个 TMP 字体上，且再也不刷新；
            // tooltip 字体一旦可知且与探针不同，就换过来并丢掉按旧字体算出的宽度。
            var preferred = PreferredFont();
            if (preferred != null && !ReferenceEquals(preferred, _probeFont)) Rebind(preferred);
            return;
        }
        try
        {
            var go = new GameObject("ItemDetails_WaterMeasure", typeof(RectTransform), typeof(TextMeshProUGUI));
            UnityEngine.Object.DontDestroyOnLoad(go);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.font = PreferredFont()
                       ?? UnityEngine.Object.FindObjectOfType<TMP_Text>()?.font
                       ?? TMP_Settings.defaultFontAsset;
            tmp.fontSize = ProbeFontSize;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;
            _probe = tmp;
            _probeFont = tmp.font;
        }
        catch (Exception ex) { Log.Warn("WaterColumns.Probe", ex); }
    }

    private static void Rebind(TMP_FontAsset font)
    {
        try
        {
            _probe!.font = font;
            _probeFont = font;
            Cache.Clear();
        }
        catch (Exception ex) { Log.Warn("WaterColumns.Probe", ex); }
    }

    /// <summary>字符串宽度（像素，按 16 号字）。</summary>
    internal static float Measure(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        Ensure();   // 先确认字体：换过字体的话缓存已经清空，命中旧值就会量错
        if (Cache.TryGetValue(text!, out float cached)) return cached;
        float width;
        try
        {
            width = _probe != null
                ? _probe.GetPreferredValues(text!, 5000f, 5000f).x
                : text!.Length * 8f;
        }
        catch { width = text!.Length * 8f; }
        if (Cache.Count >= CacheLimit) Cache.Clear();
        Cache[text!] = width;
        return width;
    }

    private static float Em(string? text) => Measure(text) / ProbeFontSize;

    private static string Pos(float em)
        => "<pos=" + em.ToString("0.##", CultureInfo.InvariantCulture) + "em>";

    /// <summary>表头以下三条列的起点，单位是 em。</summary>
    internal readonly struct Layout
    {
        internal readonly float VolumeStart;
        internal readonly float PercentStart;

        internal Layout(float volumeStart, float percentStart)
        {
            VolumeStart = volumeStart;
            PercentStart = percentStart;
        }
    }

    /// <summary>一行按列落位：名称 → 跳到用量列写用量 → 跳到百分比列写百分比。</summary>
    private static string Row(WaterRow row, Layout layout)
        => row.Name + Pos(layout.VolumeStart) + row.Amount + Pos(layout.PercentStart) + row.Percent;

    /// <summary>按所有可见行的名称/用量算出列落点，折叠提示行也参与。</summary>
    private static Layout Measure(IReadOnlyList<WaterRow> rows)
    {
        float nameEm = 0f;
        float volEm = 0f;
        for (int i = 0; i < rows.Count; i++)
        {
            nameEm = Math.Max(nameEm, Em(rows[i].Name));
            volEm = Math.Max(volEm, Em(rows[i].Amount));
        }
        float volStart = nameEm + ColumnGapEm;
        return new Layout(volStart, volStart + volEm + ColumnGapEm);
    }

    /// <summary>液体容器的一行体积，单位随 <see cref="Volume.UseLiters"/>。</summary>
    internal static string AmountOf(int parts, bool liters) => VolumeOf(parts, liters);

    /// <summary>
    /// 成分列表：名称、用量、配色。液体容器与固体化学品共用，两条路径只差
    /// 「水行是否追加标签不符提示」这一项。
    /// </summary>
    private static List<(string Name, int Parts, RenderHandler.ColorPalette Color)> Components(
        LiquidAnalysis analysis, bool waterMismatch)
    {
        var comps = new List<(string Name, int Parts, RenderHandler.ColorPalette Color)>();
        int activeIndex = 0;
        foreach (var component in analysis.Components)
        {
            string name = component.Name;
            if (waterMismatch && component.Role == "water" && analysis.LabelMismatch)
                name += Loc.T("water.mismatch", " (doesn't match label)");
            int index = component.Role == "active" ? activeIndex++ : 0;
            comps.Add((name, component.Microliters, ColorOf(component.Role, index)));
        }
        return comps;
    }

    /// <summary>折叠提示行的文案。固定只提杂质：净水建议也在展开时才画，但写进文案只会让它变长，
    /// 而这一行本身要短才好对齐。没有折叠的杂质时也会显示（建议被折叠），只是不带合计百分比。</summary>
    private static string FoldHint => Loc.T("water.fold_expand", "Ctrl to expand impurities");

    /// <summary>
    /// 是否列出全部成分：关掉「折叠水质成分」时始终为 true；
    /// 否则按住左右 Ctrl 才展开，读不到输入设备时按未按住处理。
    /// </summary>
    internal static bool Expanded()
    {
        if (!ModSettings.FoldWaterComponents) return true;
        try { return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl); }
        catch { return false; }
    }

    /// <summary>
    /// 固体化学品的成分行：名称 / 用量 / 百分比三列，与液体容器同一份行数据。
    /// 没有容器与体积，所以这里不写表头，直接从第一行成分开始；末尾补一行低于显示阈值的数量。
    /// </summary>
    /// <param name="visible">非 null 时填入这一帧实际画出的行，供区块协议发布。</param>
    internal static List<TooltipLine> BuildSolid(LiquidAnalysis analysis, List<WaterRow>? visible = null)
    {
        var comps = Components(analysis, waterMismatch: false);
        var plan = WaterTable.Build(Pairs(comps), analysis.TotalMicroliters, Expanded(),
            FoldHint, WaterInfo.Amount, Loc.Culture);
        visible?.AddRange(plan.Rows);

        var lines = RowLines(plan.Rows, comps);
        string? trace = TraceLine(analysis);
        if (trace != null)
            lines.Add(new TooltipLine(trace, RenderHandler.ColorPalette.Gray));
        return lines;
    }

    /// <summary>
    /// 低于显示阈值的成分数量那一行（“（另 3 项低于显示阈值）”）；没有隐藏成分时为 null。
    /// 绘制和区块发布共用这一份文本，免得两边各写一次、翻错一处。
    /// </summary>
    internal static string? TraceLine(LiquidAnalysis analysis)
    {
        return analysis.HiddenComponents > 0
            ? Loc.T("water.trace_count", "(+{0} more below display threshold)", analysis.HiddenComponents)
            : null;
    }

    /// <summary>
    /// 液体容器的成分表：体积条表头 + 每种成分一行。
    /// 低于 1% 的成分折起，按住 Ctrl 全部展开——默认只留水和大项，行数少、
    /// tooltip 也就不被那条长建议撑宽。净化建议不受折叠影响，始终单独一行
    /// （见 ModEntry.TryAppendWaterAdvice），所以提示行只在真的折了成分时出现。
    /// </summary>
    /// <param name="visible">非 null 时填入这一帧实际画出的行，供区块协议发布。</param>
    internal static List<TooltipLine> Build(LiquidAnalysis analysis, List<WaterRow>? visible = null)
    {
        int totalMl = analysis.TotalMicroliters / 1000;
        int capacityMl = analysis.CapacityMicroliters / 1000;
        bool liters = Volume.UseLiters(totalMl);
        float ratio = analysis.CapacityMicroliters > 0
            ? Math.Min(1f, (float)analysis.TotalMicroliters / analysis.CapacityMicroliters) : 0f;
        bool expand = Expanded();

        var comps = Components(analysis, waterMismatch: true);
        var plan = WaterTable.Build(Pairs(comps), analysis.TotalMicroliters, expand,
            FoldHint, parts => VolumeOf(parts, liters), Loc.Culture);
        visible?.AddRange(plan.Rows);

        string tail = " " + WaterLayout.Capacity(totalMl, capacityMl, liters, Loc.Culture)
                    + (analysis.Tier.Length > 0 ? " " + analysis.Tier : "");

        // 表头与原版液体容器同一顺序：先条，后容量与档位。条长 6，原版 10 格加上文字会折行。
        var lines = new List<TooltipLine>(plan.Rows.Count + 1)
        {
            new("", ratio, 6, tail,
                RenderHandler.ColorPalette.LightCyan, RenderHandler.ColorPalette.VeryDarkCyan),
        };
        lines.AddRange(RowLines(plan.Rows, comps));
        return lines;
    }

    /// <summary>
    /// 把行数据画成 tooltip 行：文字是全角空格分隔的规范输出，呈现写法是 &lt;pos&gt; 三列。
    /// 液体与固体都走这里，两条路径不会再各写一套。
    /// </summary>
    private static List<TooltipLine> RowLines(IReadOnlyList<WaterRow> rows,
        List<(string Name, int Parts, RenderHandler.ColorPalette Color)> comps)
    {
        var lines = new List<TooltipLine>(rows.Count);
        var layout = Measure(rows);
        foreach (var row in rows)
            lines.Add(new TooltipLine(WaterTable.JoinCells(row, CellSeparator), ColorOf(comps, row))
                .WithRender(Row(row, layout)));
        return lines;
    }

    private static List<(string Name, int Parts)> Pairs(
        List<(string Name, int Parts, RenderHandler.ColorPalette Color)> comps)
    {
        var pairs = new List<(string Name, int Parts)>(comps.Count);
        foreach (var c in comps) pairs.Add((c.Name, c.Parts));
        return pairs;
    }

    /// <summary>行的颜色：成分看自己，折叠提示行用灰色。</summary>
    private static RenderHandler.ColorPalette ColorOf(
        List<(string Name, int Parts, RenderHandler.ColorPalette Color)> comps, WaterRow row)
        => row.Kind == WaterRowKind.Component
            ? comps[row.Index].Color
            : RenderHandler.ColorPalette.Gray;

    /// <summary>行的颜色：水蓝、药物活性成分按顺序取色、其余（杂质/试剂/溶剂）白色。</summary>
    private static RenderHandler.ColorPalette ColorOf(string role, int fallbackIndex)
        => role == "water" ? WaterColor
         : role == "active" ? ActiveColors[fallbackIndex % ActiveColors.Length]
         : ImpurityColor;

    private static string VolumeOf(int parts, bool liters)
        => WaterLayout.VolumeText(parts, liters, Loc.Culture)
           + (liters ? WaterLayout.LiterUnit : WaterLayout.MilliliterUnit);
}
