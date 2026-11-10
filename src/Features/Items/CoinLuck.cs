using System;
using UnityEngine;
using Palette = RenderHandler.ColorPalette;

namespace ItemDetails;

internal static class CoinLuck
{
    private static HealthData? _source;
    private static float _readAt = -1f;
    private static (int Total, int Base, int Coin, int Liquid, int Days) _snapshot;

    internal static bool Append(TooltipSections.Writer output, GameItem item)
    {
        if (!ModSettings.InfoEnhance || item.identifier != LotteryHelper.GAMBLERS_COIN_ID
            || !PlayerStore.HasInstance || PlayerStore.Instance.healthData is not { } health)
            return false;

        Write(output, Read(health, Time.unscaledTime));
        return true;
    }

    // GetLuck 和随身硬币加成都要遍历背包与档案。悬停期间节流，基础值和药效变化立即刷新。
    private static (int Total, int Base, int Coin, int Liquid, int Days) Read(HealthData health, float now)
    {
        if (!ReferenceEquals(_source, health) || _snapshot.Base != health.luck
            || _snapshot.Days != health.liquidLuckDaysLeft || now < _readAt || ContentsRefresh.Due(now, _readAt))
        {
            _snapshot = (HealthData.GetLuck(), health.luck, LotteryHelper.GetCarriedLuckBonus(),
                LiquidLuckHelper.GetActiveLuckBonus(health), health.liquidLuckDaysLeft);
            _source = health;
            _readAt = now;
        }
        return _snapshot;
    }

    private static void Write(TooltipSections.Writer output, (int Total, int Base, int Coin, int Liquid, int Days) luck)
    {
        output.AddLine(Loc.T("luck.current", "Current luck: {0}", Signed(luck.Total)), Tone(luck.Total));
        if (luck.Base != 0)
            output.AddLine(Loc.T("luck.base", "Base luck: {0}", Signed(luck.Base)), Tone(luck.Base));
        if (luck.Coin != 0)
            output.AddLine(Loc.T("luck.coin", "Carried coin: {0}", Signed(luck.Coin)), Tone(luck.Coin));
        if (luck.Liquid != 0)
        {
            string name = GameRead.LookupItemName(LiquidLuckHelper.IDENTIFIER);
            if (name.Length == 0) name = "Liquid Luck";
            output.AddLine(Loc.T("luck.liquid", "{0}: {1} ({2} {2|day|days} left)",
                name, Signed(luck.Liquid), luck.Days), Tone(luck.Liquid));
        }
    }

    internal static void Reset()
    {
        _source = null;
        _readAt = -1f;
    }

    private static string Signed(int value) => value.ToString("+0;-0;0");

    private static Palette Tone(int value) => value > 0 ? Palette.LightGreen
        : value < 0 ? Palette.LightRed : Palette.Gray;
}
