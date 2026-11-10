using System;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 一次 tooltip 构建内的估值记忆化，并跨构建短暂复用。
///
/// 原版 <c>GameItemElement.GetTooltipBasic</c> 对同一件物品调
/// <c>GetCurrentValue(withChild:true)</c> 三次、<c>GetBaseValue()</c> 一次；
/// 每次都会 <c>FindAllChildItems()</c> 展开整棵内容物树再逐件估值。容器越大越慢，
/// 而 tooltip 每帧重建 —— 2500 格容器相当于每帧 4 次全量递归估值。
///
/// 只在原版 tooltip 中替换「悬停物品自己、且参数与原版一致」的估值调用：
///   - 同一次构建里后续调用直接命中；
///   - 相邻构建在 <see cref="TtlSeconds"/> 内复用，市场/内容变化最迟这么久后刷新；
///   - 子物品估值（withChild:false）与其它参数组合一律放行，不改变结算口径。
///
/// 只作用于 <c>GetTooltipBasic</c> 中的估值调用，
/// 交易、寄存、顾客报价等非 tooltip 路径完全不受影响。
/// </summary>
internal static class TooltipValueMemo
{
    /// <summary>与 <see cref="ItemValueCache"/> 同一档；容器内容或市场变化最迟这段时间反映。</summary>
    private const float TtlSeconds = 0.35f;

    private static GameItem? _sellItem;
    private static long _sell;
    private static float _sellAt = float.MinValue;

    private static GameItem? _baseItem;
    private static long _base;
    private static float _baseAt = float.MinValue;

    /// <summary>构建开始时用 <see cref="ItemValueCache"/> 已有的快照预热，省掉本帧的重复估值。</summary>
    internal static void Seed(GameItem? item)
    {
        if (item == null) return;
        try
        {
            if (!ItemValueCache.TryPeek(item, out var snapshot)) return;
            // 沿用快照自己的估值时刻：预热不能把 TTL 重新拉长。
            _sellItem = item;
            _sell = snapshot.Sell;
            _sellAt = snapshot.At;
            _baseItem = item;
            _base = snapshot.List;   // GetBaseValue()：与 Snapshot.List 同一口径
            _baseAt = snapshot.At;
        }
        catch { }
    }

    internal static bool TrySell(GameItem item, out long value)
    {
        if (ReferenceEquals(_sellItem, item) && Fresh(_sellAt))
        {
            value = _sell;
            return true;
        }
        value = 0;
        return false;
    }

    internal static void SetSell(GameItem item, long value)
    {
        _sellItem = item;
        _sell = value;
        _sellAt = Now();
    }

    internal static bool TryBase(GameItem item, out long value)
    {
        if (ReferenceEquals(_baseItem, item) && Fresh(_baseAt))
        {
            value = _base;
            return true;
        }
        value = 0;
        return false;
    }

    internal static void SetBase(GameItem item, long value)
    {
        _baseItem = item;
        _base = value;
        _baseAt = Now();
    }

    internal static void Reset()
    {
        _sellItem = null;
        _sellAt = float.MinValue;
        _baseItem = null;
        _baseAt = float.MinValue;
    }

    private static bool Fresh(float at)
    {
        if (at < 0f) return false;
        try
        {
            // 时钟回退（重开一局后 unscaledTime 归零）时按过期处理，不能当成刚算过。
            float elapsed = UnityEngine.Time.unscaledTime - at;
            return elapsed >= 0f && elapsed <= TtlSeconds;
        }
        catch { return false; }
    }

    private static float Now()
    {
        try { return UnityEngine.Time.unscaledTime; } catch { return 0f; }
    }
}
