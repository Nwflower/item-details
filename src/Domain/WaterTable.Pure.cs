using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>成分表里的一行是哪种行：真实成分，还是「按展开键展开」的折叠提示。</summary>
internal enum WaterRowKind
{
    Component,
    FoldHint,
}

/// <summary>
/// 成分表的一行：三个格子（名称、用量、百分比）。纯数据，不带颜色和定位：
/// 颜色按 <see cref="Index"/> 找原始成分，落点由 <see cref="WaterColumns"/> 决定。
/// 折叠提示行的用量格是空的，百分比格放被折起来那几项的合计。
/// </summary>
internal readonly struct WaterRow
{
    internal readonly WaterRowKind Kind;

    /// <summary>成分在输入里的下标；折叠提示行为 -1。</summary>
    internal readonly int Index;

    internal readonly string Name;
    internal readonly string Amount;
    internal readonly string Percent;

    internal WaterRow(WaterRowKind kind, int index, string name, string amount, string percent)
    {
        Kind = kind;
        Index = index;
        Name = name;
        Amount = amount;
        Percent = percent;
    }
}

/// <summary>
/// 成分表这一帧要画哪些行（纯逻辑，不依赖 Unity 或游戏类型）：按 1% 阈值折叠，
/// 决定要不要补一行折叠提示，并把折起来那几项的合计放进百分比格。
///
/// 液体容器与固体化学品共用这一份决定，只差「用量文字由谁来格式化」：
/// 液体按容器体积走 mL / L，固体用扫描仪的量。以前两条路径各写一遍，
/// 液体把全部成分都列出来再补提示、固体才真的折叠，同一个美化布局下水瓶和药片表现不一致。
/// 现在折叠状态只读一次、只算一次，输出层只决定怎么落位。
/// </summary>
internal static class WaterTable
{
    /// <summary>一次折叠的结果：可见行与合计。</summary>
    internal readonly struct Plan
    {
        internal readonly IReadOnlyList<WaterRow> Rows;
        internal readonly int FoldedParts;
        internal readonly int FoldedCount;

        internal Plan(IReadOnlyList<WaterRow> rows, int foldedParts, int foldedCount)
        {
            Rows = rows;
            FoldedParts = foldedParts;
            FoldedCount = foldedCount;
        }
    }

    /// <param name="components">按含量降序的成分；下标用于回查颜色。</param>
    /// <param name="totalParts">占比的分母，含低于显示阈值的微量成分。</param>
    /// <param name="expanded">按住展开键：全部列出，不画提示行。</param>
    /// <param name="hint">折叠提示的文案，已本地化。</param>
    /// <param name="amountOf">用量格的格式化：液体用容器体积单位，固体用扫描仪的量。</param>
    /// <param name="foldable">哪些成分在未展开时折起（按 <paramref name="components"/> 下标）；
    /// 缺省按「低于 1%」。作物只留活性成分时，传「非活性」。</param>
    internal static Plan Build(
        IReadOnlyList<(string Name, int Parts)> components,
        int totalParts,
        bool expanded,
        string hint,
        Func<int, string> amountOf,
        IFormatProvider? culture,
        Func<int, bool>? foldable = null)
    {
        var rows = new List<WaterRow>(components.Count + 1);
        int foldedParts = 0, foldedCount = 0;
        for (int i = 0; i < components.Count; i++)
        {
            var component = components[i];
            if (!expanded && (foldable != null ? foldable(i) : WaterLayout.BelowOnePercent(component.Parts, totalParts)))
            {
                foldedParts += component.Parts;
                foldedCount++;
                continue;
            }
            rows.Add(new WaterRow(WaterRowKind.Component, i, component.Name,
                amountOf(component.Parts), Percent(component.Parts, totalParts, culture)));
        }

        // 只有真的折起了成分才提示：净化建议始终显示，不再藏在展开键后面。
        if (!expanded && foldedCount > 0)
        {
            rows.Add(new WaterRow(WaterRowKind.FoldHint, -1, hint, "",
                Percent(foldedParts, totalParts, culture)));
        }

        return new Plan(rows.AsReadOnly(), foldedParts, foldedCount);
    }

    /// <summary>百分比列的文案（含 %）。</summary>
    internal static string Percent(int parts, int total, IFormatProvider? culture)
        => WaterLayout.PercentText(parts, total, culture) + "%";

    /// <summary>
    /// 空格子的占位。流式布局按分隔符切列，空格子如果什么都不放，相邻两个分隔符会并成一段，
    /// 消费方就把「提示 + 百分比」数成两格、当成普通标签对，百分比落进数值列而不是末列。
    /// 零宽空格不占宽度，也不参与切列。
    /// </summary>
    internal const string EmptyCell = "\u200B";

    /// <summary>流式布局的一行：三格用分隔符连起来，空格子放 <see cref="EmptyCell"/>。</summary>
    internal static string JoinCells(WaterRow row, char separator)
        => row.Name + separator + (row.Amount.Length > 0 ? row.Amount : EmptyCell) + separator + row.Percent;
}
