using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>未分析种子的性状区块：<see cref="SeedInfo"/> 给出性状，这里排成「名称靠左、等级靠右」的表。</summary>
internal static class SeedTraitSection
{
    // ---------------- 种子性状（透视） ----------------
    internal static bool Append(TooltipSections.Writer builder, GameItem item)
    {
        if (!PlantGate.SeedTraits) return false;

        var lines = SeedInfo.Collect(item);
        if (lines.Count == 0) return false;

        var traits = new List<(string Id, string Name, int Rank)>();
        foreach (var line in lines)
            if (line.Data is ValueTuple<string, string, int> trait) traits.Add(trait);
        if (builder.Capturing) builder.Field("traits", traits.AsReadOnly());

        // 表头 + 每个性状一行：名称靠左，等级靠右（与已分析的基因读数同一风格，只是没有效果说明）。
        // 规范文本里名称与等级用全角空格隔开，<pos> 只在原版窗口构建时换入。
        var color = RenderHandler.ColorPalette.LightTurquoise;
        var rows = new List<TooltipLine>(lines.Count + 1)
        {
            new TooltipLine(Loc.T("seed.header", "Genes (unanalyzed)"), color),
        };
        float nameEm = 0f, rankEm = 0f;
        var ranks = new string[traits.Count];
        for (int i = 0; i < traits.Count; i++)
        {
            ranks[i] = traits[i].Rank > 1 ? Loc.T("seed.rank", "Rank {0}", traits[i].Rank) : "";
            nameEm = Math.Max(nameEm, WaterColumns.TextEm(traits[i].Name));
            rankEm = Math.Max(rankEm, WaterColumns.TextEm(ranks[i]));
        }
        float rightEdge = nameEm + 0.9f + rankEm;
        for (int i = 0; i < traits.Count; i++)
        {
            string text = ranks[i].Length > 0 ? traits[i].Name + '　' + ranks[i] : traits[i].Name;
            var row = new TooltipLine(text, color);
            if (ranks[i].Length > 0)
                row = row.WithRender(traits[i].Name
                    + ModuleEffectAnnotation.Pos(rightEdge - WaterColumns.TextEm(ranks[i])) + ranks[i]);
            rows.Add(row);
        }
        foreach (var line in lines)
            if (line.Data is not ValueTuple<string, string, int>) rows.Add(line);   // 「…另有 N 项」
        return LineOutput.AppendLines(builder, rows);
    }
}
