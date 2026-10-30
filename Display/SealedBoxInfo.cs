using System;
using System.Collections.Generic;
using System.Text;

namespace ItemDetails;

/// <summary>
/// 密封箱预览：提前列出带锁容器（保险箱 / 枪柜等）的内容物与箱内总价值。
///
/// 判定：`LOCK_TYPE` 标签是密封容器的特征（`SecuredContainerHelper.InitSecuredContainer`
/// 一定会写它；钥匙只有 UNLOCK_TYPE / LOCK_DIFFICULTY，不会被误判）。
/// 注意这类物品**没有** CONTAINER_TAG，所以通用容器模块（StorageInfo）不覆盖它们。
///
/// 数据来源（均为游戏自己的公开成员）：
///   item.contentWindow.child as GameGridInventory → grid.childItems（内容物）
///   grid.GetTotalValue()                          → 箱内总价值
///
/// ⚠️ 这是**透视**功能：原版必须先开锁才能看到箱内。
/// 锁类型、难度和锁定状态已由原版 SecuredContainerHelper 显示，这里不重复。
/// 开关由调用方（ModEntry）判断，模块本身不认识配置。
/// </summary>
internal static class SealedBoxInfo
{
    private const RenderHandler.ColorPalette Color = RenderHandler.ColorPalette.LightTurquoise;

    private const int MaxItems = 8;

    /// <summary>异常数据上限：超过就不列明细、不给总价。</summary>
    private const int MaxChildren = 512;

    // 与普通容器一致：tooltip 高频重建时，内容物与价值最迟 1 秒刷新一次。
    private const float CacheSeconds = 1f;
    private static GameGridInventory? _cacheGrid;
    private static string _cacheLocale = "";
    private static float _cacheAt = float.MinValue;
    private static List<TooltipLine>? _cache;

    internal static void Stop()
    {
        _cacheGrid = null;
        _cache = null;
        _cacheAt = float.MinValue;
    }

    internal static List<TooltipLine> Collect(GameItem? item)
    {
        var lines = new List<TooltipLine>();
        try
        {
            if (item == null) return lines;

            if (!LockedContainer.IsLocked(item)) return lines;

            GameGridInventory? grid = null;
            try
            {
                var cw = item.contentWindow;
                if (cw != null) grid = cw.child as GameGridInventory;
            }
            catch (Exception ex) { Log.Warn("SealedBox.Window", ex); return lines; }
            if (grid == null) return lines;

            float now = UnityEngine.Time.unscaledTime;
            string locale = Loc.Code;
            if (_cache != null && ReferenceEquals(_cacheGrid, grid)
                && _cacheLocale == locale && now - _cacheAt <= CacheSeconds)
            {
                lines.AddRange(_cache);
                if (lines.Count > 0) Log.MarkOk("SealedBoxInfo");
                return lines;
            }
            void StoreCache()
            {
                _cacheGrid = grid;
                _cacheLocale = locale;
                _cacheAt = now;
                _cache = lines;
            }

            // ---------- 内容物 + 合计价值：同一次遍历、同一套价值口径 ----------
            // 旧版单件用 GetCurrentValue、总价用 grid.GetTotalValue，两者口径不同
            // （后者走 GetValue，不含同一套 ItemFeature/事件修正），带瑕疵时会自相矛盾。
            List<GameItem>? items = null;
            try { items = grid.childItems; }
            catch (Exception ex) { Log.Warn("SealedBox.Items", ex); }
            if (items == null || items.Count > MaxChildren)
            {
                // 读取失败或异常超限时保持无输出，不能误报成“箱内：空”。
                if (items != null)
                    Log.Warn("SealedBox.Items", $"内容物 {items.Count} 件，超过上限 {MaxChildren}，跳过");
                StoreCache();
                return lines;
            }

            var contents = new StringBuilder();
            int shown = 0, total = items.Count;
            long sum = 0;
            try
            {
                foreach (var child in items)
                {
                    if (child == null) continue;

                    long unit = 0;
                    int stack = 1;
                    try { if (child.unitCount > 1) stack = child.unitCount; } catch { }

                    bool nestedContainer = false;
                    try { nestedContainer = child.contentWindow != null; } catch { }
                    if (!nestedContainer)
                    {
                        try
                        {
                            unit = child.GetCurrentValue(useRetailMarkup: false, withChild: true,
                                                         includeEvents: true, forceMarkup: false);
                        }
                        catch { unit = 0; }
                    }
                    // 嵌套容器仍列名称，但不递归求值，避免箱套箱时成本不可控。

                    if (unit > 0 && StackValue.TryTotal(unit, stack, out long lineTotal)
                        && lineTotal <= long.MaxValue - sum)
                    {
                        sum += lineTotal;
                    }

                    if (shown >= MaxItems) continue;
                    if (shown > 0) contents.Append(Loc.T("list.sep", ", "));
                    contents.Append(GameRead.ItemName(child));
                    if (unit > 0) contents.Append(Loc.T("sealed.unit", " ({0})", unit));
                    shown++;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("SealedBox.Items", ex);
                StoreCache();
                return lines;
            }

            if (shown == 0)
            {
                lines.Add(new TooltipLine(Loc.T("sealed.empty", "Inside: empty"), Color));
            }
            else
            {
                string more = total > shown
                    ? Loc.T("sealed.more", " ... {0} items in total", total)
                    : "";
                lines.Add(new TooltipLine(
                    Loc.T("sealed.contents", "Inside: {0}{1}", contents.ToString(), more),
                    Color));
            }

            // ---------- 箱内总价值 ----------
            if (sum > 0)
                lines.Add(new TooltipLine(Loc.T("sealed.total", "Total value inside: {0}", sum), Color));

            StoreCache();
            if (lines.Count > 0) Log.MarkOk("SealedBoxInfo");
        }
        catch (Exception ex)
        {
            Log.Warn("SealedBoxInfo", ex);
        }
        return lines;
    }
}
