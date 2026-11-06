#if ITEMDETAILS_SELFTEST
using System;
using System.Linq;

namespace ItemDetails;

/// <summary>
/// 成分三列的自检：液体容器与固体化学品必须走同一套列构造。
/// 规范文本不得带 &lt;pos&gt;（第三方读的是它）；呈现写法每行是「名称 → &lt;pos&gt; → 用量 → &lt;pos&gt; → 百分比」：恰好两段 &lt;pos&gt;，
/// 且名称段里不夹带用量（旧 bug 是把 &lt;pos&gt; 追加在用量之后，名称和用量挤在行首）。
/// </summary>
internal static partial class SelfTest
{
    private static string CheckComponentColumns(out bool ok)
    {
        ok = false;
        try
        {
            var item = TryCreate("nutrifruit");
            if (item == null) return "component columns: 无法创建固体样本";
            var raw = WaterInfo.Analyze(item);
            if (!raw.HasData) return "component columns: 固体样本没有成分数据";
            var analysis = LiquidQueries.Snapshot(raw, item);

            var solid = WaterColumns.BuildSolid(analysis);
            if (solid.Count == 0) return "component columns: 固体成分表为空";

            if (solid.Any(l => l.Text.Contains("<pos="))) return "component columns: 规范文本里混进了 <pos>";
            // 折叠提示行（「按 Ctrl 展开杂质」）只有名称和百分比，没有用量，不算成分行。
            string foldHint = Loc.T("water.fold_expand", "Ctrl to expand impurities");
            var rows = solid.Where(l => l.Render != null && l.Render.Contains("<pos=")
                && !l.Render.StartsWith(foldHint, StringComparison.Ordinal)).ToList();
            if (rows.Count == 0) return "component columns: 固体行没有 <pos> 呈现";
            bool structured = rows.All(l =>
            {
                string[] parts = l.Render!.Split("<pos=");
                if (parts.Length != 3) return false;
                // 名称段不能带数字（用量），用量段不能为空。
                return !System.Text.RegularExpressions.Regex.IsMatch(parts[0], @"\d")
                    && parts[1].Substring(parts[1].IndexOf('>') + 1).Trim().Length > 0;
            });

            ok = structured;
            return $"component columns: rows={rows.Count} structured={structured} " +
                   $"all='{string.Join(" || ", rows.ConvertAll(r => r.Render))}' (期望 structured=True)";
        }
        catch (Exception ex) { return "component columns: " + ex; }
    }
}
#endif
