using System;

namespace ItemDetails;

/// <summary>
/// 性价比 = 整组价值 / 占用格数，写在价格块下一行：「性价比：55（110/2格）」。
///
/// 原版对堆叠物品已经分两行写单价和总价（mech_tooltip_unit_barter / total_barter），
/// 这里不再另写「单价 × 数量 = 总价」。
///
/// 输出位置由 TooltipPipeline 决定（价格行或原版价格块之后）；价格行没改写时
/// （InfoEnhance 关闭、模板反查失败），由 Late 钩子的渲染器补在底部，同一次构建只写一次。
///
/// 依据：ItemValueCache 的单件当前价值（不含数量）、unitCount、
/// GameRead.OccupiedCells()（实心占格）。整组价值必须在此基础上乘数量。
/// </summary>
internal static class ValueEnhance
{
    private const RenderHandler.ColorPalette Color = RenderHandler.ColorPalette.LightTurquoise;

    internal readonly struct PerCell
    {
        internal readonly double Value;
        internal readonly long Total;
        internal readonly int Cells;

        internal PerCell(double value, long total, int cells)
        {
            Value = value;
            Total = total;
            Cells = cells;
        }
    }

    // tooltip 可能每帧重建；价值重算适度缓存，特性/子物/事件等变化最迟 2 秒反映。
    private const float CacheSeconds = 2f;
    private static GameItem? _cacheItem;
    private static int _cacheCount;
    private static int _cacheCells;
    private static float _cacheAt = float.MinValue;
    private static PerCell? _cache;

    private static int _writtenBuild = -1;

    internal static void Stop()
    {
        _cacheItem = null;
        _cache = null;
        _cacheAt = float.MinValue;
        _writtenBuild = -1;
    }

    /// <summary>
    /// 这件物品要不要显示性价比。鉴定物品的价值是按顾客报价算的，含水容器意义不大，都不显示。
    /// </summary>
    internal static bool Applies(GameItem? item)
    {
        if (item == null || !ModSettings.ValueEnhance) return false;
        if (GameRead.IsTag(item, "APPRAISAL_ITEM")) return false;
        try { if (item.IsTag("LIQUID_CONTAINER_TAG")) return false; }
        catch { }
        return true;
    }

    /// <summary>写一行性价比；本次 tooltip 已写过、不适用或读不到价值时返回 false。</summary>
    internal static bool TryWrite(RichTextBuilder builder, GameItem? item)
    {
        if (_writtenBuild == TooltipPipeline.BuildId && TooltipPipeline.IsBuilding) return false;
        if (!Applies(item)) return false;

        var r = Compute(item!);
        if (r == null) return false;

        var v = r.Value;
        builder.Add(Loc.T("value.cost_perf", "Value per cell: {0}", v.Value.ToString("0.#")), Color);
        builder.Add(Loc.T("value.cost_perf_detail", " ({0}/{1} cells)", v.Total, v.Cells),
            RenderHandler.ColorPalette.Gray);
        builder.NextLine();
        if (TooltipPipeline.IsBuilding) _writtenBuild = TooltipPipeline.BuildId;
        return true;
    }

    internal static PerCell? Compute(GameItem item)
    {
        try
        {
            int count = 1;
            try { count = item.unitCount; } catch { }
            if (count < 1) count = 1;

            // 占格：按实心格计算，非矩形物品不能用外接矩形宽×高。
            int cells = GameRead.OccupiedCells(item.modifiedShape);

            float now = UnityEngine.Time.unscaledTime;
            if (ReferenceEquals(_cacheItem, item) && _cacheCount == count && _cacheCells == cells
                && now - _cacheAt <= CacheSeconds)
            {
                if (_cache != null) Log.MarkOk("ValueEnhance");
                return _cache;
            }

            PerCell? result = null;

            // GetCurrentValue() 是「单件当前价值」，原版不会在这里乘 unitCount；
            // 整组价值 = 单件当前价值 × 数量。不能用 GetValue() 回退，它已经乘过数量；
            // 也不能退回 unitValue——那会丢掉子物、特性与事件修正，口径完全不同。读不到就不显示。
            long unit;
            try
            {
                // 与价格占位/价格行共用快照，机器嵌套内容不再另行展开一次物品树。
                unit = ItemValueCache.Get(item).Sell;
            }
            catch (Exception ex)
            {
                Log.Warn("ValueEnhance.Value", ex);
                return null;
            }

            if (cells > 0 && StackValue.TryTotal(unit, count, out long total) && total > 0)
            {
                result = new PerCell(StackValue.PerCell(total, cells), total, cells);
                Log.MarkOk("ValueEnhance");
            }

            // 无价值物品也缓存，避免每帧重复估值
            _cacheItem = item;
            _cacheCount = count;
            _cacheCells = cells;
            _cacheAt = now;
            _cache = result;
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn("ValueEnhance", ex);
            return null;
        }
    }
}
