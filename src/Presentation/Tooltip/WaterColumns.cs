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

    /// <summary>开启「不同液体组分以不同颜色显示」时杂质的固定配色（按液体 identifier）；其余按顺序取 <see cref="ActiveColors"/>。</summary>
    private static readonly Dictionary<string, RenderHandler.ColorPalette> TypeColors = new(StringComparer.Ordinal)
    {
        ["chemical_contaminant"] = RenderHandler.ColorPalette.LightPurple,
        ["organic_waste"] = RenderHandler.ColorPalette.LightGreen,
        ["microbe"] = RenderHandler.ColorPalette.LightYellow,
        ["mineral"] = RenderHandler.ColorPalette.White,
        ["heavy_metal"] = RenderHandler.ColorPalette.Gray,
        ["physical_contaminant"] = RenderHandler.ColorPalette.LightRed,
    };

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

    /// <summary>字符串宽度（em）；其它按列排版的行用它量字宽。</summary>
    internal static float TextEm(string? text) => Em(text);

    private static string Pos(float em)
        => "<pos=" + em.ToString("0.##", CultureInfo.InvariantCulture) + "em>";

    /// <summary>表头以下三条列的起点，单位是 em。</summary>
    internal readonly struct Layout
    {
        internal readonly float VolumeStart;
        /// <summary>用量列的右端；用量右对齐到这里，小数点才落在同一条竖线上。</summary>
        internal readonly float VolumeRight;
        internal readonly float PercentStart;
        /// <summary>百分比列的右端；百分比与净化建议的增益都右对齐到这里。</summary>
        internal readonly float RightEdge;

        internal Layout(float volumeStart, float volumeRight, float percentStart, float rightEdge)
        {
            VolumeStart = volumeStart;
            VolumeRight = volumeRight;
            PercentStart = percentStart;
            RightEdge = rightEdge;
        }
    }

    /// <summary>一行按列落位：名称 → 跳到用量列写用量 → 跳到百分比列写百分比。</summary>
    private static string Row(WaterRow row, Layout layout)
        => row.Kind == WaterRowKind.FoldHint
            // 折叠提示：名称合并占住名称列与用量列（不经过用量列起点），只把合计落在百分比列
            ? row.Name + Pos(layout.RightEdge - Em(row.Percent)) + row.Percent
            : row.Name + Pos(layout.VolumeRight - Em(row.Amount)) + row.Amount + Pos(layout.RightEdge - Em(row.Percent)) + row.Percent;

    /// <summary>
    /// 按所有可见行的名称/用量算出列落点，折叠提示行也参与。
    /// 净化建议在场时它的三列也算进来，成分表与建议共用同一套竖线（见 <see cref="AdviceWidths"/>）。
    /// </summary>
    private static Layout BuildLayout(IReadOnlyList<WaterRow> rows, AdviceWidths? advice)
    {
        float nameEm = 0f;
        float volEm = 0f;
        float hintEm = 0f;
        float pctEm = 0f;
        for (int i = 0; i < rows.Count; i++)
        {
            // 折叠提示比名称长得多，单独量：它横跨名称列与用量列，不能把用量列往右推
            pctEm = Math.Max(pctEm, Em(rows[i].Percent));
            if (rows[i].Kind == WaterRowKind.FoldHint) { hintEm = Math.Max(hintEm, Em(rows[i].Name)); continue; }
            nameEm = Math.Max(nameEm, Em(rows[i].Name));
            volEm = Math.Max(volEm, Em(rows[i].Amount));
        }
        float volStart = nameEm + ColumnGapEm;
        // 建议里的纯度落在这条列上，名称比用量列还宽时整列一起右移，两者才不会叠字；
        // 没有纯度可写（读不到这瓶水的纯度）时这条列不必为建议让位
        float volumeRight = volStart + volEm;
        if (advice.HasValue && advice.Value.Purity > 0f && advice.Value.VolumeRight > volumeRight)
            volumeRight = advice.Value.VolumeRight;
        // 提示盖不住两列时，只顺延百分比列，用量列位置不变
        float percentStart = Math.Max(volumeRight + ColumnGapEm, hintEm + ColumnGapEm);
        // 建议的增益落在这条列上，比成分表自己的百分比更靠右时跟着它走
        float rightEdge = percentStart + pctEm;
        if (advice.HasValue) rightEdge = Math.Max(rightEdge, advice.Value.RightEdge(volumeRight));
        return new Layout(volStart, volumeRight, percentStart, rightEdge);
    }

    /// <summary>液体容器的一行成分用量（mL）。</summary>
    internal static string AmountOf(int parts) => VolumeOf(parts);

    /// <summary>
    /// 成分列表：名称、用量、配色。液体容器与固体化学品共用，两条路径只差
    /// 「水行是否追加标签不符提示」这一项。
    /// </summary>
    private static List<(string Name, int Parts, RenderHandler.ColorPalette Color)> Components(
        LiquidAnalysis analysis, bool waterMismatch)
    {
        var comps = new List<(string Name, int Parts, RenderHandler.ColorPalette Color)>();
        int activeIndex = 0, otherIndex = 0;
        bool byType = ModSettings.ColorLiquidComponents;
        foreach (var component in analysis.Components)
        {
            string name = component.Name;
            if (waterMismatch && component.Role == "water" && analysis.LabelMismatch && WaterIsMain(analysis))
                name += Loc.T("water.mismatch", " (doesn't match label)");
            int index = component.Role == "active" ? activeIndex++ : byType && component.Role == "other" ? otherIndex++ : 0;
            comps.Add((name, component.Microliters, ColorOf(component.Role, component.Id, index, byType)));
        }
        return comps;
    }

    /// <summary>
    /// 固体化学品的成分行：名称 / 用量 / 百分比三列，与液体容器同一份行数据。
    /// 没有容器与体积，所以这里不写表头，直接从第一行成分开始；末尾补一行低于显示阈值的数量。
    /// </summary>
    /// <param name="visible">非 null 时填入这一帧实际画出的行，供区块协议发布。</param>
    /// <param name="foldInactive">只列活性成分，其余折成一行提示，按住展开键全部展开（作物用）；化学品全部列出。</param>
    internal static List<TooltipLine> BuildSolid(LiquidAnalysis analysis, List<WaterRow>? visible = null,
        bool foldInactive = false)
    {
        var comps = Components(analysis, waterMismatch: false);
        var components = analysis.Components;
        var plan = WaterTable.Build(Pairs(comps), analysis.TotalMicroliters, expanded: !foldInactive || Expanded(),
            Loc.T("water.fold_inactive", "{0} to expand inactive components", ModSettings.ExpandKeyName),
            WaterInfo.Amount, Loc.Culture,
            foldInactive ? i => components[i].Role != "active" : null,
            optimizedPercent: ModSettings.ContainerUnits);
        visible?.AddRange(plan.Rows);

        var lines = RowLines(plan.Rows, comps);
        string? trace = TraceLine(analysis);
        if (trace != null)
            lines.Add(new TooltipLine(trace, RenderHandler.ColorPalette.Gray));
        return lines;
    }

    /// <summary>只有名称与百分比的成分表（水龙头出水用）：没有容器与体积，用量格留空。</summary>
    internal static List<TooltipLine> BuildPercentOnly(LiquidAnalysis analysis)
    {
        var comps = Components(analysis, waterMismatch: false);
        var plan = WaterTable.Build(Pairs(comps), analysis.TotalMicroliters, expanded: true,
            "", _ => "", Loc.Culture, optimizedPercent: ModSettings.ContainerUnits);
        return RowLines(plan.Rows, comps);
    }

    /// <summary>按住设置里选的展开键（左右两侧都认）时展开被折叠的成分；读不到输入设备时按未按住处理。</summary>
    internal static bool Expanded()
    {
        try
        {
            switch (ModSettings.ExpandKey)
            {
                case ModSettings.ExpandKeyMode.Shift: return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                case ModSettings.ExpandKeyMode.Alt: return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                default: return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            }
        }
        catch { return false; }
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
    /// 成分一律全部列出，不折叠；净化建议另起一行（见 WaterSections.AppendAdvice）。
    /// </summary>
    /// <param name="visible">非 null 时填入这一帧实际画出的行，供区块协议发布。</param>
    /// <param name="advice">净化建议的三列宽度（见 <see cref="MeasureAdvice"/>）；null 表示没有建议行。</param>
    internal static List<TooltipLine> Build(LiquidAnalysis analysis, List<WaterRow>? visible = null,
        AdviceWidths? advice = null)
    {
        int totalMl = analysis.TotalMicroliters / 1000;
        int capacityMl = analysis.CapacityMicroliters / 1000;
        bool liters = ModSettings.ContainerUnits && Volume.UseLiters(totalMl);
        float ratio = analysis.CapacityMicroliters > 0
            ? Math.Min(1f, (float)analysis.TotalMicroliters / analysis.CapacityMicroliters) : 0f;

        var comps = Components(analysis, waterMismatch: true);
        var plan = WaterTable.Build(Pairs(comps), analysis.TotalMicroliters, expanded: true,
            "", VolumeOf, Loc.Culture, optimizedPercent: ModSettings.ContainerUnits);
        visible?.AddRange(plan.Rows);

        string tail = " " + WaterLayout.Capacity(totalMl, capacityMl, liters, Loc.Culture)
                    + (analysis.Tier.Length > 0 ? " " + analysis.Tier : "");

        // 表头与原版液体容器同一顺序：先条，后容量与档位。条长 6，原版 10 格加上文字会折行。
        var lines = new List<TooltipLine>(plan.Rows.Count + 1)
        {
            new("", ratio, 6, tail,
                RenderHandler.ColorPalette.LightCyan, RenderHandler.ColorPalette.VeryDarkCyan),
        };
        lines.AddRange(RowLines(plan.Rows, comps, advice));
        return lines;
    }

    /// <summary>
    /// 把行数据画成 tooltip 行：文字是全角空格分隔的规范输出，呈现写法是 &lt;pos&gt; 三列。
    /// 液体与固体都走这里，两条路径不会再各写一套。
    /// </summary>
    private static List<TooltipLine> RowLines(IReadOnlyList<WaterRow> rows,
        List<(string Name, int Parts, RenderHandler.ColorPalette Color)> comps, AdviceWidths? advice = null)
    {
        var lines = new List<TooltipLine>(rows.Count);
        var layout = BuildLayout(rows, advice);
        _tableFrame = Time.frameCount;
        _tableRightEdge = layout.RightEdge;
        _tableVolumeRight = layout.VolumeRight;
        foreach (var row in rows)
            lines.Add(new TooltipLine(WaterTable.JoinCells(row, CellSeparator), ColorOf(comps, row))
                .WithRender(Row(row, layout)));
        return lines;
    }

    /// <summary>本帧成分表的百分比列右端与用量列右端；净水建议据此对齐。跨帧的值不可信，不使用。</summary>
    private static int _tableFrame = -1;
    private static float _tableRightEdge;
    private static float _tableVolumeRight;

    /// <summary>
    /// 净化建议的三列宽度（em）：最长名称、最宽纯度、最宽增益。
    /// 成分表先按它把用量列与百分比列摆好，建议行随后落在同一套竖线上。
    /// </summary>
    internal readonly struct AdviceWidths
    {
        internal readonly float Name;
        /// <summary>最宽纯度；0 表示没有「净化后」结果。</summary>
        internal readonly float Purity;
        internal readonly float Gain;

        internal AdviceWidths(float name, float purity, float gain)
        {
            Name = name;
            Purity = purity;
            Gain = gain;
        }

        /// <summary>纯度列右端：右对齐到用量列的右端，与用量数字落在同一条竖线上。</summary>
        internal float VolumeRight => Name + ColumnGapEm + Purity;

        /// <summary>增益列右端：有纯度列时隔一格接在它后面，没有则直接接名称。</summary>
        internal float RightEdge(float volumeRight) => (Purity > 0f ? volumeRight : Name) + ColumnGapEm + Gain;
    }

    /// <summary>量出建议三列各要占多宽（em）；没有「净化后」结果时纯度列为 0。</summary>
    internal static AdviceWidths MeasureAdvice(
        IReadOnlyList<(string Name, double GainPercentagePoints)> methods,
        IReadOnlyList<string>? results = null)
    {
        float nameEm = 0f, purityEm = 0f, gainEm = 0f;
        for (int i = 0; i < methods.Count; i++)
        {
            nameEm = Math.Max(nameEm, Em(methods[i].Name));
            gainEm = Math.Max(gainEm, Em(GainText(methods[i].GainPercentagePoints)));
            if (results != null && i < results.Count) purityEm = Math.Max(purityEm, Em(results[i]));
        }
        return new AdviceWidths(nameEm, purityEm, gainEm);
    }

    private static string GainText(double gain) => "+" + gain.ToString("0.00", Loc.Culture) + "%";

    /// <summary>
    /// 净水建议：一种手段一行，三列——名称靠左、净化后的纯度落在成分表的用量列、增益落在百分比列。
    /// 纯度只是参考值，用灰色压一档；档位名不显示，玩家看增益与纯度就够了。
    /// 本帧没有成分表（这块没画）时，三列按建议自己的宽度另起一套。
    /// 文本是全角空格分隔的规范输出，渲染写法是 &lt;pos&gt; 加内联灰色。
    /// </summary>
    internal static List<TooltipLine> AdviceLines(
        IReadOnlyList<(string Name, double GainPercentagePoints)> methods, RenderHandler.ColorPalette color,
        IReadOnlyList<string>? results = null)
    {
        var widths = MeasureAdvice(methods, results);
        float volumeRight = widths.VolumeRight;
        float right = widths.RightEdge(volumeRight);
        if (_tableFrame == Time.frameCount)
        {
            // 与成分表共用竖线：名字比用量列宽、增益比百分比列宽时由成分表那边已经顺延过，这里取两者的较右者
            volumeRight = Math.Max(volumeRight, _tableVolumeRight);
            right = Math.Max(right, _tableRightEdge);
        }

        var lines = new List<TooltipLine>(methods.Count);
        for (int i = 0; i < methods.Count; i++)
        {
            string name = methods[i].Name;
            string purity = widths.Purity > 0f && i < results!.Count ? results[i] : "";
            string gain = GainText(methods[i].GainPercentagePoints);
            string render = name
                + (purity.Length > 0
                    ? Pos(volumeRight - Em(purity)) + "<color=" + ColumnLayout.GrayText + ">" + purity + "</color>"
                    : "")
                + Pos(right - Em(gain)) + gain;
            string canonical = name + CellSeparator + (purity.Length > 0 ? purity + CellSeparator : "") + gain;
            lines.Add(new TooltipLine(canonical, color).WithRender(render));
        }
        return lines;
    }

    /// <summary>水是否为主要成分（超过一半）：净化建议与「与标签不符」提示只对这类容器有意义。</summary>
    internal static bool WaterIsMain(LiquidAnalysis analysis)
    {
        long water = 0, total = 0;
        foreach (var c in analysis.Components)
        {
            total += c.Microliters;
            if (c.Role == "water") water += c.Microliters;
        }
        return total > 0 && water * 2 > total;
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

    /// <summary>行的颜色：水蓝、药物活性成分按顺序取色、其余（杂质/试剂/溶剂）白色；开启「按种类上色」时杂质各有固定颜色。</summary>
    private static RenderHandler.ColorPalette ColorOf(string role, string? id, int fallbackIndex, bool byType)
        => role == "water" ? WaterColor
         : role == "active" ? ActiveColors[fallbackIndex % ActiveColors.Length]
         : byType ? (id != null && TypeColors.TryGetValue(id, out var color) ? color : ActiveColors[fallbackIndex % ActiveColors.Length])
         : ImpurityColor;

    /// <summary>成分用量永远用 mL（玩家习惯，也方便横向比较）；「液体单位优化」开启时按有效数字取位。</summary>
    private static string VolumeOf(int parts)
        => WaterLayout.AmountText(parts, Loc.Culture, ModSettings.ContainerUnits) + WaterLayout.MilliliterUnit;
}
