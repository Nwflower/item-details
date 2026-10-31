using System;

namespace ItemDetails;

/// <summary>
/// 违禁等级 + 黑市溢价。
///
/// 原版 CreateContrabandItemTooltip 已经会写一行「违禁品」警告（按等级变色），
/// 但没有告诉玩家**卖去黑市能多拿多少**。本模块补上溢价百分比。
///
/// 依据（正式版 0.46D 源码）：
///   item.IsTag("CONTRABAND_ITEM_TAG")            —— 是否违禁品
///   item.GetTagReadonly("CONTRABAND_LEVEL")
///        .GetString() == "CONTRABAND_LEVEL_LOW" / _MID / _HIGH / _CRITICAL
///   PlayerStore.contrabandMarkupLow    = 35
///   PlayerStore.contrabandMarkupMid    = 70
///   PlayerStore.contrabandMarkupHigh   = 100
///   PlayerStore.contrabandMarkupCritial = 150   （原版拼写如此）
///
/// 只读；逐项 try/catch。
/// </summary>
internal static class ContrabandInfo
{
    internal enum Level { None, Low, Mid, High, Critical }

    internal readonly struct Result
    {
        internal readonly Level Level;
        internal readonly int MarkupPercent;   // -1 表示读不到
        internal Result(Level level, int markup) { Level = level; MarkupPercent = markup; }
        internal bool HasData => Level != Level.None;
    }

    internal static Result Analyze(GameItem? item)
    {
        if (item == null) return new Result(Level.None, -1);

        try
        {
            Level level = ReadLevel(item);
            if (level == Level.None) return new Result(Level.None, -1);

            int markup = ReadMarkup(level);
            Log.MarkOk("ContrabandInfo." + level);
            return new Result(level, markup);
        }
        catch (Exception ex)
        {
            Log.Warn("ContrabandInfo", ex);
            return new Result(Level.None, -1);
        }
    }

    private static Level ReadLevel(GameItem item)
    {
        try
        {
            // 没有违禁标记直接跳过
            if (!item.IsTag("CONTRABAND_ITEM_TAG")) return Level.None;
        }
        catch { return Level.None; }

        string raw = GameRead.TagString(item, "CONTRABAND_LEVEL") ?? "";
        if (raw.Length == 0) return Level.None;

        return raw switch
        {
            "CONTRABAND_LEVEL_LOW" => Level.Low,
            "CONTRABAND_LEVEL_MID" => Level.Mid,
            "CONTRABAND_LEVEL_HIGH" => Level.High,
            "CONTRABAND_LEVEL_CRITICAL" => Level.Critical,
            _ => Level.None,
        };
    }

    /// <summary>读 PlayerStore 上的溢价百分比。存档未加载或读取失败时返回 -1。</summary>
    private static int ReadMarkup(Level level)
    {
        try
        {
            var store = PlayerStore.Instance;
            if (store == null) return -1;

            return level switch
            {
                Level.Low => store.contrabandMarkupLow,
                Level.Mid => store.contrabandMarkupMid,
                Level.High => store.contrabandMarkupHigh,
                Level.Critical => store.contrabandMarkupCritial,
                _ => -1,
            };
        }
        catch (Exception ex)
        {
            Log.Warn("ContrabandInfo.Markup", ex);
            return -1;
        }
    }
}
