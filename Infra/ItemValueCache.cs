using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// Tooltip 专用的售价/标价快照。价格占位、价格行和性价比共用同一组值，
/// 避免每次重建都对机器、模块、电池等嵌套物品树重复估值。
///
/// 数值口径保持原版：withChild=true，事件与特性照常参与。短 TTL 覆盖连续重建，
/// 小容量 LRU 覆盖几台机器之间来回切换；内容物或市场变化最迟 0.35 秒后刷新。
/// 只在主线程使用，Mod 停用时清空，避免长期持有物品引用。
/// </summary>
internal static class ItemValueCache
{
    private const float CacheSeconds = 0.35f;
    private const int MaxEntries = 8;

    internal readonly struct Snapshot
    {
        internal readonly long Sell;
        internal readonly long List;

        internal Snapshot(long sell, long list)
        {
            Sell = sell;
            List = list;
        }
    }

    private sealed class Entry
    {
        internal GameItem? Item;
        internal Snapshot Value;
        internal float At;
    }

    private static readonly List<Entry> Entries = new();

    internal static Snapshot Get(GameItem item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));

        float now = UnityEngine.Time.unscaledTime;
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (now - entry.At > CacheSeconds)
            {
                Entries.RemoveAt(i);
                continue;
            }
            if (!ReferenceEquals(entry.Item, item)) continue;

            // 最近使用的条目移到末尾；A→B→A 的快速切换不会反复展开两棵物品树。
            if (i != Entries.Count - 1)
            {
                Entries.RemoveAt(i);
                Entries.Add(entry);
            }
#if ITEMDETAILS_DIAG
            Track(hit: true, now);
#endif
            return entry.Value;
        }

        // 保持原调用的参数与顺序；任一读取失败都不写缓存，交由原有调用边界降级。
        var value = new Snapshot(
            item.GetCurrentValue(useRetailMarkup: false, withChild: true,
                                 includeEvents: true, forceMarkup: false),
            item.GetBaseValue());
        Entries.Add(new Entry { Item = item, Value = value, At = now });
        if (Entries.Count > MaxEntries) Entries.RemoveAt(0);
#if ITEMDETAILS_DIAG
        Track(hit: false, now);
#endif
        return value;
    }

    internal static void Reset()
    {
        Entries.Clear();
#if ITEMDETAILS_DIAG
        _hits = 0;
        _misses = 0;
        _diagAt = 0f;
#endif
    }

#if ITEMDETAILS_DIAG
    private static int _hits;
    private static int _misses;
    private static float _diagAt;

    private static void Track(bool hit, float now)
    {
        if (hit) _hits++;
        else _misses++;
        if (_diagAt <= 0f) _diagAt = now + 2f;
        if (now < _diagAt) return;

        Log.Msg($"[perf] 价值快照（2秒）: 命中={_hits} 重算={_misses} 缓存={Entries.Count}");
        _hits = 0;
        _misses = 0;
        _diagAt = now + 2f;
    }
#endif
}
