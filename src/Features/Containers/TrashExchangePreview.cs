using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ItemDetails;

/// <summary>按当前垃圾桶内容预览地下交易所的夜间兑换，不执行成交回调。</summary>
internal static class TrashExchangePreview
{
    private static GameItem? _trashcan;
    private static ExchangeManager? _exchange;
    private static string _locale = "";
    private static float _scannedAt = -1f;
    private static List<string> _rewards = new();

    internal static bool Append(TooltipSections.Writer output, GameItem item)
    {
        if (!ModSettings.InfoEnhance || !ReferenceEquals(item, EmporiumEntry.Instance?.trashcan)
            || !PlayerStore.HasInstance)
            return false;

        var exchange = PlayerStore.Instance.exchangeManager;
        if (exchange?.isExchangeUnlocked != true) return false;

        var janitorial = StoreService.GetJanitorialService();
        if (janitorial?.IsActive() != true || StoreReputation.IsPerkUnlocked("BM_DISTRUSTED"))
            return false;

        float now = Time.unscaledTime;
        string locale = Loc.Code;
        if (!ReferenceEquals(item, _trashcan) || !ReferenceEquals(exchange, _exchange)
            || locale != _locale || now < _scannedAt || ContentsRefresh.Due(now, _scannedAt))
        {
            _rewards = Preview(exchange, GraphUtils.FindAllChildrenType<GameItem>(item));
            _trashcan = item;
            _exchange = exchange;
            _locale = locale;
            _scannedAt = now;
        }

        if (_rewards.Count == 0) return false;
        foreach (string reward in _rewards)
            output.AddLine(Loc.T("exchange.tonight", "Expected tonight: {0}", reward),
                RenderHandler.ColorPalette.LightCyan);
        return true;
    }

    internal static void Reset()
    {
        _trashcan = null;
        _exchange = null;
        _scannedAt = -1f;
        _rewards.Clear();
    }

    // PlayerStore resets the nightly counters, then ExchangeManager visits each eligible offer
    // once in descending priority. Reserve matched objects on a copy of the inventory.
    internal static List<string> Preview(ExchangeManager exchange, List<GameItem> items)
    {
        var rewards = new List<string>();
        if (items.Count == 0) return rewards;

        // 有补丁重写配方匹配或结算顺序时，按原版模拟可能连成交与否都不准。
        if (Harmony.GetPatchInfo(AccessTools.Method(typeof(ExchangeManager), nameof(ExchangeManager.HandleExchange))) != null
            || Harmony.GetPatchInfo(AccessTools.Method(typeof(ExchangeBarter), nameof(ExchangeBarter.GetRequiredItems))) != null
            || Harmony.GetPatchInfo(AccessTools.Method(typeof(ExchangeBarter), nameof(ExchangeBarter.CanAccept))) != null)
            return rewards;

        bool nativeAccept = Harmony.GetPatchInfo(AccessTools.Method(typeof(ExchangeBarter), nameof(ExchangeBarter.Accept))) == null;
        var stock = new List<GameItem>(items);
        var offers = new List<ExchangeBarter>(exchange.activeBarter);
        foreach (var offer in exchange.promotionalBarter)
            if (exchange.activePromotionalBarterIds.Contains(offer.identifier))
                offers.Add(offer);
        offers.Sort((left, right) => right.priority.CompareTo(left.priority));

        var selected = new List<GameItem>();
        foreach (var offer in offers)
        {
            if (offer.barterCounterPerNight <= 0 || offer.conditions.Count == 0) continue;
            selected.Clear();
            foreach (var condition in offer.conditions)
            {
                var match = stock.Find(item => !selected.Contains(item) && condition.IsItemValid(item));
                if (match == null) break;
                selected.Add(match);
            }
            if (selected.Count != offer.conditions.Count) continue;

            string? reward = TrashExchangeRewards.Describe(offer, selected, nativeAccept);
            if (reward != null) rewards.Add(reward);
            foreach (var consumed in selected)
            {
                stock.Remove(consumed);
                // 成交会连同容器内容一起销毁，后续配方不能再拿这些内容物。
                if (consumed.contentWindow == null) continue;
                foreach (var child in GraphUtils.FindAllChildrenType<GameItem>(consumed))
                    stock.Remove(child);
            }
            if (stock.Count == 0) break;
        }
        return rewards;
    }
}
