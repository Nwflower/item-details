using System;
using System.Collections.Generic;
using ItemDetails.Integration;

namespace ItemDetails;

/// <summary>
/// 液体相关的三个 tooltip 区块：组分分析（含固体化学品的成分表）、水龙头出水水质、净化建议。
/// 数据来自 <see cref="LiquidQueries"/> 与 <see cref="FaucetInfo"/>，列的排法在 <see cref="WaterColumns"/>；
/// 这里负责把它们接成区块：发字段、让专属显示方接管、按表宽预留折行预算。
/// </summary>
internal static class WaterSections
{
    // ---------------- 液体组分：水质 / 杂质 / 药物成分 ----------------
    internal static bool AppendAnalysis(TooltipSections.Writer builder, GameItem item)
    {
        var r = LiquidQueries.Analyze(item);
        if (r == null) return false;
        builder.Field("analysis", r.Data);
        // 专属的液体显示方也走区块接管：回调拿到同一份分段数据，返回 true 表示本次由它绘制。
        if (LiquidQueries.TryDisplay(ItemDetailsClient.AnalysisDisplay, item, r.Data)) return true;

        bool wrote = false;

        // 只有接管方在看的时候才留一份行数据，平时不付这份分配。
        var visible = builder.Capturing ? new List<WaterRow>() : null;

        if (!r.IsSolid)
        {
            // 液体容器：表头只画「体积条 + 2000/2000mL 档位」，下面每种成分一行；
            // 单元格按全角空格分隔，原版窗口构建时换成 TMP 的 <pos> 三列（见 WaterColumns）。
            // 净化建议开着时先量出它的三列，成分表据此把用量列与百分比列摆好，两段共用竖线
            WaterColumns.AdviceWidths? advice = null;
            if (ModSettings.WaterAdvice && LiquidQueries.GetAdvice(item) is { Count: > 0 } methods)
            {
                var deduped = DedupeByGain(methods);
                advice = WaterColumns.MeasureAdvice(deduped, ResultTexts(item, deduped));
            }
            var table = WaterColumns.Build(r, visible: visible, advice: advice);
            PublishWaterRows(builder, visible);
            // 折行预算按本表实际最宽的一行算：原版 40（中文折算 26）通常够用，
            // 只有名称特别长的行才放宽，且只放宽到够用。以前一律放到 64，
            // 连净化建议那条长行也不再折行，把整个 tooltip 撑宽。
            LineOutput.RequireTableWidth(builder, table);
            LineOutput.AppendLines(builder, table);
            wrote = true;
        }
        else
        {
            // 固体化学品：没有容器与体积，所以不画条；成分与液体容器共用同一份行数据。
            // 带化学纯度体系的固体，或含药物活性成分的作物才画表，判断见 ShowSolidTable。
            if (ShowSolidTable(item, r))
            {
                // 作物只留活性成分，按展开键展开其余；化学品（药片、粉末）仍全部列出
                var table = WaterColumns.BuildSolid(r, visible,
                    foldInactive: !GameRead.IsTag(item, "CHEMICAL_PRODUCT_PURITY_TAG"));
                PublishWaterRows(builder, visible);
                // 低于阈值那一行不是成分行，单发一个字段，读取方才能把它归到液体里。
                string? trace = WaterColumns.TraceLine(r);
                if (trace != null) builder.Field("trace", trace);
                LineOutput.AppendLines(builder, table);
                wrote = true;
            }
        }

        string effects = r.Effects.Text;
        if (effects.Length > 0)
        {
            builder.AddLine(Loc.T("chem.effects", "Effects: {0}", effects),
                r.Effects.LethalDose > 0 || r.Effects.Toxic ? RenderHandler.ColorPalette.LightRed : RenderHandler.ColorPalette.LightYellow);
            wrote = true;
        }

        return wrote;
    }

    /// <summary>
    /// 固体物品是否画成分表，满足任一条：
    /// 一、带化学纯度体系的化学品，即游戏用 <c>ChemicalProductHelper.GradeProduct</c>
    /// 写入 CHEMICAL_PRODUCT_PURITY_TAG 的物品（药片、粉末、诺莫注射器）；
    /// 二、成分里有药物活性成分（被植物性状注入成分的营养果等作物）：玩家要看的就是活性成分的量。
    /// 没有活性成分的作物、菌菇、粪便、净水片这些，成分表读到的只是内部随机数，
    /// 画出来既不解释价格也帮不上玩家，只留药效结论。
    /// </summary>
    private static bool ShowSolidTable(GameItem item, LiquidAnalysis analysis)
    {
        if (GameRead.IsTag(item, "CHEMICAL_PRODUCT_PURITY_TAG")) return true;
        foreach (var component in analysis.Components)
            if (component.Role == "active") return true;
        return false;
    }

    /// <summary>
    /// 把这一帧实际画出的成分行交给接管方：名称、用量、百分比已经是分好的格子，
    /// 不必再从全角空格或 &lt;pos&gt; 里反推。折叠提示行的用量为空，百分比是折起来的合计。
    /// 只在观察或接管方在场时写入，字段缺失时读取方按「没有表格」处理。
    /// </summary>
    private static void PublishWaterRows(TooltipSections.Writer builder, List<WaterRow>? rows)
    {
        if (rows == null || rows.Count == 0) return;
        var payload = new List<(string Kind, string Name, string Amount, string Percent)>(rows.Count);
        foreach (var row in rows)
            payload.Add((row.Kind == WaterRowKind.Component
                ? WaterRowKinds.Component
                : WaterRowKinds.FoldHint, row.Name, row.Amount, row.Percent));
        IReadOnlyList<(string Kind, string Name, string Amount, string Percent)> published = payload.AsReadOnly();
        builder.Field("rows", published);
    }

    // ---------------- 水龙头：出水水质 ----------------
    internal static bool AppendFaucet(TooltipSections.Writer builder, GameItem item)
    {
        if (!FaucetInfo.IsFaucet(item) || !LiquidGate.Enabled) return false;
        var analysis = FaucetInfo.Analyze(item);
        if (analysis == null) return false;
        builder.Field("analysis", analysis.Data);
        var lines = new List<TooltipLine>
        {
            new(Loc.T("faucet.header", "Tap water: {0}", analysis.Tier), RenderHandler.ColorPalette.LightCyan),
        };
        lines.AddRange(WaterColumns.BuildPercentOnly(analysis));
        LineOutput.RequireTableWidth(builder, lines);
        return LineOutput.AppendLines(builder, lines);
    }

    // ---------------- 含水容器：净化收益（探测版） ----------------
    internal static bool AppendAdvice(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.WaterAdvice) return false;
        var methods = LiquidQueries.GetAdvice(item);
        if (methods == null) return false;
        // 过滤结果完全相同的手段只留第一个：滤水器与高级滤水器在这瓶水上收益一样时，
        // 写两遍只是重复同一个数字。接管方拿到的也是去重后的列表。
        var shown = DedupeByGain(methods);
        builder.Field("methods", shown);
        if (LiquidQueries.TryDisplay(ItemDetailsClient.AdviceDisplay, item, shown)) return true;
        if (shown.Count == 0) return builder.Capturing;

        // 建议行不参与「折叠水质成分」：那条折叠只收 1% 以下的小成分，
        // 建议本身是玩家操作水时的依据，始终画出来，不再要求按展开键。
        // 一种手段一行：纯度落在成分表的用量列（灰色），增益右对齐到百分比列
        // 折行预算按最长的一行留：名称里常有空格，预算不够时原版会在名称中间折行，右对齐随之错位
        var results = ResultTexts(item, shown);
        for (int i = 0; i < shown.Count; i++)
            builder.RequireWidth(ProgressBarStyle.WidthForLine(ProgressBarStyle.VisibleLength(
                shown[i].Name + (results[i].Length > 0 ? "　" + results[i] : "") + "　+00.00%")));
        return LineOutput.AppendLines(builder, WaterColumns.AdviceLines(shown, RenderHandler.ColorPalette.LightTurquoise, results));
    }

    /// <summary>
    /// 净化后的纯度，如「99.29%」；读不到纯度时为空。
    /// 只写数字：档位名（幽灵水、优质水……）与上一行「2000/2000mL 幽灵水」重复，
    /// 玩家读建议时看的是能涨多少、涨到多少，多三个字只会把这一列挤宽。
    /// </summary>
    private static string[] ResultTexts(GameItem item, IReadOnlyList<(string Name, double GainPercentagePoints)> methods)
    {
        var texts = new string[methods.Count];
        int purity = WaterAdvice.SafePurity(item);
        if (purity < 0) return texts;
        for (int i = 0; i < texts.Length; i++)
        {
            int after = Math.Min(WaterInfo.PurityFull, purity + (int)Math.Round(methods[i].GainPercentagePoints * 100.0));
            texts[i] = (after / (WaterInfo.PurityFull / 100.0)).ToString("0.00", Loc.Culture) + "%";
        }
        return texts;
    }

    /// <summary>按提升幅度去重：收益相同的手段只留第一个（顺序即扫描顺序）。</summary>
    private static List<(string Name, double GainPercentagePoints)> DedupeByGain(
        IReadOnlyList<(string Name, double GainPercentagePoints)> methods)
    {
        var seen = new HashSet<double>();
        var result = new List<(string Name, double GainPercentagePoints)>(methods.Count);
        foreach (var m in methods)
            if (seen.Add(m.GainPercentagePoints)) result.Add(m);
        return result;
    }
}
