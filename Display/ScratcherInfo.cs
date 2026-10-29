using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 刮刮卡透视：在没刮开之前就告诉你结果与奖金。
///
/// 依据（0.46D `ScratcherHelper`）：
///   三张符号存在 `SCRATCHER_SYMBOL1..3`，刮开状态在 `SCRATCHER_STATE1..3`（1=已刮）。
///   原版 `UpdateValue` 只统计**已刮开**的符号，中奖后写 `LOTTERY_IS_WINNING_TAG` 并设价值：
///     任一符号出现 ≥3 次 → 营养果符号(1) 350，其它 75
///     出现恰好 2 次      → 营养果符号(1) 30，其它 10
///   这里**只读**地把三个符号全算上（不论是否刮开），因此能提前得到结果；
///   不调用 `UpdateValue`，不会改动物品或写入标签。
/// </summary>
internal static class ScratcherInfo
{
    private const string SymbolPrefix = "SCRATCHER_SYMBOL";
    private const string StatePrefix = "SCRATCHER_STATE";
    private const int NutrifruitSymbol = 1;

    internal static List<TooltipLine> Collect(GameItem? item)
    {
        var lines = new List<TooltipLine>();
        try
        {
            if (item == null) return lines;

            bool isScratcher;
            try { isScratcher = item.IsTag("SCRATCHER_TAG"); }
            catch { return lines; }
            if (!isScratcher) return lines;

            var symbols = new List<int>(3);
            int scratched = 0;
            bool statesReadable = true;
            for (int i = 1; i <= 3; i++)
            {
                // 缺任何一个符号都不下"中奖/不中奖"的确定结论：
                // 数据不全时的猜测比不显示更糟。
                int? sym = GameRead.TagInt(item, SymbolPrefix + i);
                if (sym is not int s || s <= 0) return lines;
                symbols.Add(s);

                int? state = GameRead.TagInt(item, StatePrefix + i);
                if (state is not int st) statesReadable = false;
                else if (st == 1) scratched++;
            }
            if (symbols.Count != 3) return lines;

            // 统计出现次数（三个符号全算，含未刮开的）
            var counts = new Dictionary<int, int>();
            foreach (int s in symbols)
            {
                counts.TryGetValue(s, out int n);
                counts[s] = n + 1;
            }

            int prize = 0, bestCount = 0;
            foreach (var kv in counts)
            {
                if (kv.Value >= 3) { prize = kv.Key == NutrifruitSymbol ? 350 : 75; bestCount = kv.Value; break; }
                if (kv.Value == 2 && prize == 0) { prize = kv.Key == NutrifruitSymbol ? 30 : 10; bestCount = 2; }
            }

            lines.Add(prize > 0
                ? new TooltipLine(
                    Loc.T("scratcher.win", "Scratcher: wins {0} ({1} matching)", prize, bestCount),
                    RenderHandler.ColorPalette.LightGreen)
                : new TooltipLine(
                    Loc.T("scratcher.no_prize", "Scratcher: no prize"),
                    RenderHandler.ColorPalette.LightRed));

            if (statesReadable && scratched < 3)
            {
                lines.Add(new TooltipLine(
                    Loc.T("scratcher.progress", "Scratched {0}/3", scratched),
                    RenderHandler.ColorPalette.LightCyan));
            }

            Log.MarkOk("ScratcherInfo");
        }
        catch (Exception ex)
        {
            Log.Warn("ScratcherInfo", ex);
        }
        return lines;
    }
}
