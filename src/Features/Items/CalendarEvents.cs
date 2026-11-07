using System;
using System.Text;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 日历提示框追加「今日事件」。
///
/// 原版 <c>StoreCalendar.GetTooltip</c> 只返回一句「点击打开日历」。这里在后面接上今天的事件：
/// 事件名（<c>StoreEvent.GetDisplayName</c>，即日历格子里的标题）和每条价格影响
/// （<c>GetEventPriceEffect</c>，与日历详情同源）。事件列表用日历自己的
/// <c>GetActiveEventOnDay(今天)</c>：已排除未知事件与装饰事件，玩家在日历上看不到的这里也不会泄露。
/// 归「信息增强」开关；只读。
/// </summary>
internal static class CalendarEvents
{
    internal static void Install(PatchScope scope)
    {
        try
        {
            var target = AccessTools.Method(typeof(StoreCalendar), "GetTooltip", Type.EmptyTypes);
            if (target == null)
            {
                Log.Warn("CalendarEvents", "未找到 StoreCalendar.GetTooltip，日历事件提示跳过");
                return;
            }
            scope.Patch(target, postfix: new HarmonyMethod(typeof(CalendarEvents), nameof(After)));
            Log.Msg("CalendarEvents: patched StoreCalendar.GetTooltip");
        }
        catch (Exception ex)
        {
            Log.Error("CalendarEvents.Apply", ex);
        }
    }

    private static void After(ref string __result)
    {
        if (!ModSettings.InfoEnhance) return;
        try
        {
            var station = StoreStation.Instance;
            var events = station?.storeEventManager?.GetActiveEventOnDay(station.dayCounter);
            if (events == null || events.Count == 0) return;

            var sb = new StringBuilder(__result ?? "");
            sb.Append("\n\n").Append(RenderHandler.ApplyRichTextColorTag(
                Loc.T("calendar.today_events", "Today's events"), RenderHandler.ColorPalette.LightTurquoise));
            foreach (var ev in events)
            {
                if (ev == null) continue;
                string name = ev.GetDisplayName();
                if (string.IsNullOrEmpty(name)) continue;
                sb.Append('\n').Append(name);
                string effect = ev.negociationDatas != null && ev.negociationDatas.Count > 0
                    ? ev.GetEventPriceEffect().Trim() : "";
                foreach (string line in effect.Split('\n'))
                    if (line.Trim().Length > 0)
                        sb.Append("\n  ").Append(RenderHandler.ApplyRichTextColorTag(line.Trim(), RenderHandler.ColorPalette.Gray));
            }
            __result = sb.ToString();
        }
        catch (Exception ex)
        {
            Log.Warn("CalendarEvents.After", ex);
        }
    }
}
