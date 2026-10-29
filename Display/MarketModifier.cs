using System;
using System.Collections.Generic;
using System.Text;

namespace ItemDetails;

/// <summary>
/// 市场修正：把「当前生效的店铺事件」对这件物品的价格影响显示出来。
///
/// 依据（0.46D）：
///   StoreStation.Instance.storeEventManager.GetActiveEvents()  → 当前生效事件
///   StoreEvent.negociationDatas                                → 逐条价格修正
///   NegociationData.itemType / value（百分比）/ publicDisplay（短文案）
///   NegociationData.IsNegociationDataAffectItem(item)          → 是否作用于该物品
///
/// 游戏结算里这些 value 就是 `marketMult`（`GetCurrentValue` 中累加），
/// 所以显示的百分比与真实价格修正同源。
/// 用 publicDisplay（短文案）而不是事件长描述，避免撑长 tooltip。
/// </summary>
internal static class MarketModifier
{
    private const int MaxLines = 3;

    internal static List<string> Collect(GameItem? item, bool zh)
    {
        var lines = new List<string>();
        try
        {
            if (item == null) return lines;

            var station = StoreStation.Instance;
            if (station == null) return lines;
            var mgr = station.storeEventManager;
            if (mgr == null) return lines;

            List<StoreEvent>? events = null;
            try { events = mgr.GetActiveEvents(); } catch { }
            if (events == null || events.Count == 0) return lines;

            var sb = new StringBuilder();
            int shown = 0;

            foreach (var ev in events)
            {
                if (ev?.negociationDatas == null) continue;
                foreach (var nd in ev.negociationDatas)
                {
                    if (nd == null) continue;
                    bool affects;
                    try { affects = nd.IsNegociationDataAffectItem(item); }
                    catch { continue; }
                    if (!affects) continue;

                    if (shown >= MaxLines) break;
                    if (shown > 0) sb.Append(zh ? "、" : ", ");

                    string label = Safe(() => nd.publicDisplay);
                    if (string.IsNullOrEmpty(label)) label = Safe(() => ev.displayName);
                    if (string.IsNullOrEmpty(label)) label = nd.identifier ?? "?";

                    string pct = nd.value > 0 ? $"+{nd.value}%" : $"{nd.value}%";
                    sb.Append(label).Append(' ').Append(pct);
                    shown++;
                }
                if (shown >= MaxLines) break;
            }

            if (shown > 0)
                lines.Add(zh ? $"市场：{sb}" : $"Market: {sb}");
        }
        catch (Exception ex)
        {
            Log.Warn("MarketModifier", ex);
        }
        return lines;
    }

    private static string Safe(Func<string?> f)
    {
        try { return f() ?? ""; } catch { return ""; }
    }
}
