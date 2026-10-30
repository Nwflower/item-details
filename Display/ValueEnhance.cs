using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// P3 价值增强：原版已给单件/整组价值，这里补"拆解与比较"维度。
///  - 单价 × 数量 = 总价（原版是合并显示，这里显式拆开）
///  - 性价比 = 总价值 / 占用格数（帮玩家判断"占地方的值不值"）
/// 只读；逐项 try/catch。
///
/// 依据：GameItem.GetCurrentValue()（单件当前价值，不含数量）、unitCount、
/// GameRead.OccupiedCells()（实心占格）。整组价值必须在此基础上乘数量。
/// </summary>
internal static class ValueEnhance
{
    private const RenderHandler.ColorPalette Color = RenderHandler.ColorPalette.LightTurquoise;

    // tooltip 可能每帧重建；价值重算适度缓存，特性/子物/事件等变化最迟 2 秒反映。
    private const float CacheSeconds = 2f;
    private static GameItem? _cacheItem;
    private static string _cacheLocale = "";
    private static int _cacheCount;
    private static int _cacheCells;
    private static float _cacheAt = float.MinValue;
    private static List<TooltipLine>? _cache;

    internal static void Stop()
    {
        _cacheItem = null;
        _cache = null;
        _cacheAt = float.MinValue;
    }

    internal static List<TooltipLine> Collect(GameItem? item)
    {
        var lines = new List<TooltipLine>();
        if (item == null) return lines;

        try
        {
            int count = 1;
            try { count = item.unitCount; } catch { }
            if (count < 1) count = 1;

            // 占格：按实心格计算，非矩形物品不能用外接矩形宽×高。
            int cells = GameRead.OccupiedCells(item.modifiedShape);

            float now = UnityEngine.Time.unscaledTime;
            string locale = Loc.Code;
            if (_cache != null && ReferenceEquals(_cacheItem, item) && _cacheLocale == locale
                && _cacheCount == count && _cacheCells == cells && now - _cacheAt <= CacheSeconds)
            {
                if (_cache.Count > 0) Log.MarkOk("ValueEnhance");
                return _cache;
            }

            void StoreCache()
            {
                _cacheItem = item;
                _cacheLocale = locale;
                _cacheCount = count;
                _cacheCells = cells;
                _cacheAt = now;
                _cache = lines;
            }

            // GetCurrentValue() 是「单件当前价值」，原版不会在这里乘 unitCount；
            // 整组价值 = 单件当前价值 × 数量。不能用 GetValue() 回退，它已经乘过数量；
            // 也不能退回 unitValue——那会丢掉子物、特性与事件修正，口径完全不同，
            // 却仍然标成「总价/性价比」，属于给出错数。读不到就不显示。
            long unit;
            try
            {
                // 与价格占位/价格行共用快照，机器嵌套内容不再另行展开一次物品树。
                unit = ItemValueCache.Get(item).Sell;
            }
            catch (Exception ex)
            {
                Log.Warn("ValueEnhance.Value", ex);
                return lines;
            }
            if (unit <= 0)
            {
                StoreCache();             // 无价值物品也缓存，避免每帧重复估值
                return lines;
            }

            if (!StackValue.TryTotal(unit, count, out long total))
            {
                StoreCache();
                return lines;
            }

            Log.MarkOk("ValueEnhance");

            // 单价 × 数量 = 总价（数量>1 才有意义）
            if (count > 1)
            {
                lines.Add(new TooltipLine(
                    Loc.T("value.total", "Total: {0} × {1} = {2}", unit, count, total),
                    Color));
            }

            // 单格价值 = 总价值 / 占格
            if (cells > 0 && total > 0)
            {
                double perCell = StackValue.PerCell(total, cells);
                // 格式参照压缩率：比例 +（总额/格数格）
                lines.Add(new TooltipLine(
                    Loc.T("value.per_cell", "Value per cell: {0} ({1}/{2} cells)",
                        perCell.ToString("0.#"), total, cells),
                    Color));
            }

            StoreCache();
        }
        catch (Exception ex)
        {
            Log.Warn("ValueEnhance", ex);
        }

        return lines;
    }
}
