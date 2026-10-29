using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 彩票：直接显示开奖结果（开奖号码 + 命中数 + 奖金）。
///
/// 原版 CreateLotteryTicketTooltip 只做「命中的号码变绿、未中变红」，
/// 不写命中几个、也不写奖金；未开奖的票则完全不提结果。
///
/// 开奖号码的取法（按可靠性排序）：
///   1. 票自身的 LOTTERY_WINNING_NUMBER_TAG
///   2. 同期的**已拥有**彩票上的开奖号码（游戏只给"拥有的票"写 tag，
///      所以店里隔期未买的票自己没有，但同期的票能提供）
///   3. 上一期开奖结果 PlayerStore.currentWinningLotteryNumber
///   4. 都没有 → 视为尚未开奖
///
/// 命中数与奖金直接复用游戏函数 LotteryHelper.CountMatches / GetPrizeForMatches，
/// 不自己实现，避免与官方规则漂移。
/// </summary>
internal static class LotteryInfo
{
    private const string TicketTag = "LOTTERY_TICKET_TAG";
    private const string EditionTag = "LOTTERY_EDITION_TAG";
    private const string NumberTag = "LOTTERY_NUMBER_TAG";
    private const string WinningTag = "LOTTERY_WINNING_NUMBER_TAG";

    internal static List<TooltipLine> Collect(GameItem? item, bool zh)
    {
        var lines = new List<TooltipLine>();
        if (item == null) return lines;

        try
        {
            if (!item.IsTag(TicketTag)) return lines;

            int? editionRaw = GameRead.TagInt(item, EditionTag);
            if (editionRaw is not int edition) return lines;   // 期号读不到：不猜

            string own = GameRead.TagString(item, WinningTag) ?? "";

            string winning = !string.IsNullOrWhiteSpace(own)
                ? own
                : ResolveWinning(edition, item);

            Log.MarkOk("LotteryInfo");

            if (string.IsNullOrWhiteSpace(winning))
            {
                lines.Add(new TooltipLine(
                    zh ? $"第{edition}期尚未开奖（每周六公布）"
                       : $"Edition {edition}: not drawn yet (draws every Saturday)",
                    RenderHandler.ColorPalette.LightYellow));
                return lines;
            }

            int matches = 0;
            try { matches = LotteryHelper.CountMatches(item, winning); } catch { }
            int prize = 0;
            try { prize = LotteryHelper.GetPrizeForMatches(matches); } catch { }

            bool won = prize > 0;
            var color = won ? RenderHandler.ColorPalette.LightGreen : RenderHandler.ColorPalette.LightRed;

            lines.Add(new TooltipLine(
                zh ? $"开奖号码：{winning}" : $"Winning: {winning}",
                RenderHandler.ColorPalette.LightCyan));
            lines.Add(new TooltipLine(
                won
                    ? (zh ? $"命中 {matches}/6 → 奖金 {prize}" : $"Matched {matches}/6 -> prize {prize}")
                    : (zh ? $"命中 {matches}/6 → 未中奖" : $"Matched {matches}/6 -> no prize"),
                color));
        }
        catch (Exception ex)
        {
            Log.Warn("LotteryInfo", ex);
        }

        return lines;
    }

    /// <summary>
    /// 已拥有彩票扫描缓存：GetAllOwnedItems 会递归遍历所有库存与子物品，
    /// 大存档下每次悬停都全扫代价很高。按「期号 + 缓存时长」复用结果。
    /// </summary>
    private static int _ownedEdition = int.MinValue;
    private static string _ownedWinning = "";
    private static float _ownedAt = float.MinValue;
    private const float OwnedCacheSeconds = 5f;

    /// <summary>找出该期开奖号码；找不到返回空串。</summary>
    private static string ResolveWinning(int edition, GameItem self)
    {
        // 2. 同期且已拥有的彩票（游戏只给拥有的票写开奖号）
        string owned = ResolveFromOwned(edition, self);
        if (!string.IsNullOrWhiteSpace(owned)) return owned;

        // 3. 上一期刚开出的结果
        try
        {
            var store = PlayerStore.Instance;
            if (store != null && !string.IsNullOrWhiteSpace(store.currentWinningLotteryNumber)
                && edition == store.currentLotteryEdition - 1)
            {
                return store.currentWinningLotteryNumber;
            }
        }
        catch (Exception ex) { Log.Warn("LotteryInfo.LastDraw", ex); }

        return "";
    }

    private static string ResolveFromOwned(int edition, GameItem self)
    {
        float now = UnityEngine.Time.unscaledTime;
        if (edition == _ownedEdition && now - _ownedAt <= OwnedCacheSeconds)
            return _ownedWinning;

        string found = ScanOwned(edition, self);
        _ownedEdition = edition;
        _ownedWinning = found;
        _ownedAt = now;
        return found;
    }

    private static string ScanOwned(int edition, GameItem self)
    {
        try
        {
            var emporium = EmporiumEntry.Instance;
            var owned = emporium?.GetAllOwnedItems();
            if (owned == null) return "";
            foreach (var other in owned)
            {
                if (other == null || ReferenceEquals(other, self)) continue;
                if (!GameRead.IsTag(other, TicketTag)) continue;
                if (GameRead.TagInt(other, EditionTag) != edition) continue;
                string w = GameRead.TagString(other, WinningTag) ?? "";
                if (!string.IsNullOrWhiteSpace(w)) return w;
            }
        }
        catch (Exception ex) { Log.Warn("LotteryInfo.Owned", ex); }
        return "";
    }
}
