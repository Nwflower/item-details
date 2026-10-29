using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 赝品肋明治的具体原因（tooltip 追加）。
///
/// 触发条件（全部来自游戏）：
///   RibwichHelper.IsOpened(item)      —— 已开封不是赝品，直接跳过
///   RibwichHelper.IsCounterfeit(item) —— 只有游戏判定为赝品才列原因
///
/// 原因条目由纯逻辑 <see cref="RibwichReasons"/> 判定，这里只负责读标签与本地化。
/// 读取标签全部 try/catch，逐项失败不影响其它行。
/// </summary>
internal static class RibwichCounterfeit
{
    internal const int MaxLines = 6;

    internal static List<string> Collect(GameItem? item, bool zh)
    {
        var result = new List<string>();
        try
        {
            if (item == null) return result;
            if (!GameRead.IsTag(item, "RIBWICH_TAG")) return result;

            // 已开封按固定 40 计价，但不是"赝品"，不列原因
            bool opened;
            try { opened = RibwichHelper.IsOpened(item); }
            catch (Exception ex) { Log.Warn("RibwichCounterfeit.IsOpened", ex); return result; }
            if (opened) return result;

            bool counterfeit;
            try { counterfeit = RibwichHelper.IsCounterfeit(item); }
            catch (Exception ex) { Log.Warn("RibwichCounterfeit.IsCounterfeit", ex); return result; }
            if (!counterfeit) return result;

            int month = Tag(item, "RIBWICH_PRODUCTION_MONTH_TAG");
            int year = Tag(item, "RIBWICH_PRODUCTION_YEAR_TAG");
            int facility = Tag(item, "RIBWICH_FACILITY_NUMBER_TAG");
            int davre = Tag(item, "RIBWICH_DAVRE_LOGO_TAG");
            int logo = Tag(item, "RIBWICH_RIBWICH_LOGO_TAG");
            int productionMark = Tag(item, "RIBWICH_PRODUCTION_TAG");
            int facilityMark = Tag(item, "RIBWICH_FACILITY_TAG");
            int bun = Tag(item, "RIBWICH_BUN_TAG");
            int pickles = Tag(item, "RIBWICH_PICKLES_TAG");

            var reasons = RibwichReasons.Evaluate(month, year, facility,
                davre, logo, productionMark, facilityMark, bun, pickles);

            for (int i = 0; i < reasons.Count && i < MaxLines; i++)
                result.Add(RibwichReasons.Text(reasons[i], zh, month, year, facility));

            if (reasons.Count > MaxLines)
                result.Add(zh ? $"…另有 {reasons.Count - MaxLines} 项" : $"... and {reasons.Count - MaxLines} more");

            if (result.Count > 0) Log.MarkOk("RibwichCounterfeit");
        }
        catch (Exception ex)
        {
            Log.Warn("RibwichCounterfeit", ex);
        }
        return result;
    }

    private static int Tag(GameItem item, string tag)
    {
        try { return GameRead.TagInt(item, tag) ?? 0; }
        catch { return 0; }
    }
}
