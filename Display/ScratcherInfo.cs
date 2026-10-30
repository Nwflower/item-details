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
///   不调用 `UpdateValue`，不会改动物品或写入标签（它还会触发成就检查，探针上也不能调）。
///
/// 规则按目标游戏版本实现。已刮开至少一格时，游戏已经按已刮符号 `SetValue` 过；
/// 若照抄规则与当前 unitValue 不一致，就不预测这张卡。无需扫描其它模组的补丁。
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
            var scratchedSymbols = new List<int>(3);
            int scratched = 0;
            for (int i = 1; i <= 3; i++)
            {
                // 缺任何一个符号都不下"中奖/不中奖"的确定结论：
                // 数据不全时的猜测比不显示更糟。
                int? sym = GameRead.TagInt(item, SymbolPrefix + i);
                if (sym is not int s || s <= 0) return lines;
                symbols.Add(s);

                // 缺状态标签与游戏 GetTagReadonly 读到的默认值一样，按未刮开处理
                if (GameRead.TagInt(item, StatePrefix + i) == 1) { scratched++; scratchedSymbols.Add(s); }
            }
            if (symbols.Count != 3) return lines;

            if (!RuleHolds(item, scratchedSymbols)) return lines;

            // 三个符号全算，含未刮开的
            int prize = Prize(symbols, out int bestCount);

            string text = prize > 0
                ? Loc.T("scratcher.win", "Scratcher: wins {0} ({1} matching)", prize, bestCount)
                : Loc.T("scratcher.no_prize", "Scratcher: no prize");
            // 刮开进度是同一张卡的状态，接在结论后面
            if (scratched < 3)
                text += Loc.T("scratcher.progress_part", ", scratched {0}/3", scratched);
            lines.Add(new TooltipLine(text,
                prize > 0 ? RenderHandler.ColorPalette.LightGreen : RenderHandler.ColorPalette.LightRed));

            Log.MarkOk("ScratcherInfo");
        }
        catch (Exception ex)
        {
            Log.Warn("ScratcherInfo", ex);
        }
        return lines;
    }
    /// <summary>照抄 ScratcherHelper.UpdateValue 的奖金规则：同符号 ≥3 次或恰好 2 次。</summary>
    private static int Prize(List<int> symbols, out int bestCount)
    {
        var counts = new Dictionary<int, int>();
        foreach (int s in symbols)
        {
            counts.TryGetValue(s, out int n);
            counts[s] = n + 1;
        }

        int prize = 0;
        bestCount = 0;
        foreach (var kv in counts)
        {
            if (kv.Value >= 3) { prize = kv.Key == NutrifruitSymbol ? 350 : 75; bestCount = kv.Value; break; }
            if (kv.Value == 2 && prize == 0) { prize = kv.Key == NutrifruitSymbol ? 30 : 10; bestCount = 2; }
        }
        return prize;
    }

    /// <summary>照抄的规则在当前游戏里是否仍然成立（见类注释）。</summary>
    private static bool RuleHolds(GameItem item, List<int> scratchedSymbols)
    {
        if (scratchedSymbols.Count == 0) return true;   // 还没刮过，游戏尚未按规则写值，无从核对
        long expected = Prize(scratchedSymbols, out _);
        long actual;
        try { actual = item.unitValue; } catch { return false; }
        if (actual == expected) return true;

        Log.Warn("ScratcherInfo.Rule",
            $"已刮符号 [{string.Join(",", scratchedSymbols)}] 按内置规则应为 {expected}，游戏为 {actual}；不预测该卡");
        return false;
    }
}
