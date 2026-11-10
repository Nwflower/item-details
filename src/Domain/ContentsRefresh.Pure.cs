using System;

namespace ItemDetails;

/// <summary>
/// 内容物快照的刷新节流：tooltip 每帧重建，但库存不会每帧变化。
/// 两次扫描之间复用快照，把每帧的内容物开销从 O(件数) 降到 O(1)。
///
/// 纯逻辑、无游戏依赖；离线压测（tests/TooltipStress）按 60 fps 模拟这个门限，
/// 统计 10 秒里真正扫描的次数。
/// </summary>
internal static class ContentsRefresh
{
    /// <summary>两次扫描的最短间隔（秒）。鼠标移入后的第一帧必定扫描。</summary>
    internal const float IntervalSeconds = 0.2f;

    /// <summary>是否到了重新读库存的时间；从未扫过（负值）也算到期。</summary>
    internal static bool Due(float now, float lastScanAt)
        => lastScanAt < 0f || now - lastScanAt >= IntervalSeconds;
}
