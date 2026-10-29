using System;
using System.Collections.Generic;
using System.Text;

namespace ItemDetails;

/// <summary>
/// 储存容器信息：压缩率 + 内容物清单 + 合计价值。
///
/// 数据来源（已在正式版 0.46D 源码核实）：
///   - 容器内部库存：DirectoryUtils.CreateInventoryWindow(w,h) 返回 (PixelWindow, GameGridInventory)
///     并 pixelWindow.Attach(grid)，随后 GameItem.SetContentWindow(pixelWindow)；
///     因此 item.contentWindow.child as GameGridInventory 即内部库存。
///   - 内部格数 = grid.inventoryShape 的**可用格**数（0 = 可用，与物品外形的 1 = 实心相反）
///   - 内容物   = grid.childItems（GameGridInventory 覆写，返回 items 的副本）
///   - 自身占格 = item.modifiedShape 的实心格数
/// 原版 ContainerHelper.CreateContainerTooltip 只输出一句通用提示，不列内容物，更无压缩率。
///
/// 只读；仅对带网格内部库存的容器显示；槽位型容器（无宽高）跳过。
/// 压缩率与内容物由同一个开关控制（ModSettings.Container），这里不再分两个参数。
/// </summary>
internal static class StorageInfo
{
    private const RenderHandler.ColorPalette Color = RenderHandler.ColorPalette.LightCyan;

    // 内容物最多列出的种类数，超出折叠，避免 tooltip 过宽
    private const int MaxKinds = 5;

    // tooltip 可能每帧重建；内容物聚合适度缓存，变更最迟 1 秒后反映。
    private const float ContentsCacheSeconds = 1f;
    private static GameGridInventory? _contentsGrid;
    private static bool _contentsZh;
    private static float _contentsAt = float.MinValue;
    private static List<TooltipLine>? _contentsCache;

    internal static void Stop()
    {
        _contentsGrid = null;
        _contentsCache = null;
        _contentsAt = float.MinValue;
    }

    internal static List<TooltipLine> Collect(GameItem? item, bool zh)
    {
        var lines = new List<TooltipLine>();
        if (item == null) return lines;

        try
        {
            // 结构性判定：有内部网格库存 = 容器。
            // 不再依赖 CONTAINER_TAG 等标签名——标签改名会让模块静默失效，
            // 而"有没有 contentWindow"是数据模型本身的性质。
            GameGridInventory? grid = null;
            try
            {
                var cw = item.contentWindow;
                if (cw != null) grid = cw.child as GameGridInventory;
            }
            catch (Exception ex) { Log.Warn("StorageInfo.Window", ex); return lines; }
            if (grid == null) return lines;

            // 机器也带 contentWindow，但显示压缩率没意义，跳过（用游戏自己的类型判断）
            bool isMachine = false;
            try { isMachine = item.IsGameItemType("MACHINE"); } catch { }
            if (isMachine) return lines;

            AddRatio(item, grid, zh, lines);
            AddContents(grid, zh, lines);

            if (lines.Count > 0) Log.MarkOk("StorageInfo");
        }
        catch (Exception ex)
        {
            Log.Warn("StorageInfo", ex);
        }
        return lines;
    }

    private static void AddRatio(GameItem item, GameGridInventory grid, bool zh, List<TooltipLine> lines)
    {
        try
        {
            int innerW = 0, innerH = 0;
            int innerCells = 0;
            GridShape? inner = null;
            try
            {
                inner = grid.inventoryShape;
                if (inner != null)
                {
                    innerW = inner.width; innerH = inner.height;
                    // 内部网格 0 = 可用格（SetDataFill(0)），与物品外形的 1 = 实心相反
                    innerCells = GameRead.UsableCells(inner);
                    // 不再退回外接矩形：算不出就是算不出，宁可少一行也不给错数字
                }
            }
            catch (Exception ex) { Log.Warn("StorageInfo.InnerShape", ex); }

            if (innerCells <= 0)
            {
                Log.Warn("StorageInfo.Ratio", $"内部网格可用格数为 0（{innerW}x{innerH}），跳过压缩率");
                return;
            }

            // 自身占格：优先 modifiedShape（可能被改造改变），退回原始 shape
            int selfCells = 0;
            GridShape? selfShape = null;
            try { selfShape = item.modifiedShape; } catch { }
            if (selfShape == null)
            {
                try { selfShape = item.shape; } catch { }
            }
            if (selfShape != null)
            {
                selfCells = GameRead.OccupiedCells(selfShape);
            }

            if (selfCells <= 0)
            {
                return;
            }

            double ratio = (double)innerCells / selfCells;

            lines.Add(new TooltipLine(
                zh ? $"压缩率：{ratio:0.0}（{innerCells}/{selfCells}格）"
                   : $"Compression: {ratio:0.0} ({innerCells}/{selfCells} cells)",
                Color));

        }
        catch (Exception ex) { Log.Warn("StorageInfo.Ratio", ex); }
    }

    private static void AddContents(GameGridInventory grid, bool zh, List<TooltipLine> lines)
    {
        try
        {
            List<GameItem>? items = null;
            try { items = grid.childItems; }
            catch (Exception ex) { Log.Warn("StorageInfo.ChildItems", ex); return; }
            if (items == null || items.Count == 0) return;
            if (items.Count > 512) return;   // 异常保护

            // 按显示名聚合数量
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            long totalValue = 0;

            foreach (var it in items)
            {
                if (it == null) continue;

                string name = "";
                try { name = it.name ?? ""; } catch { }
                if (name.Length == 0) { try { name = it.identifier ?? "?"; } catch { name = "?"; } }

                int stack = 1;
                try { if (it.unitCount > 1) stack = it.unitCount; } catch { }

                if (counts.TryGetValue(name, out int c)) counts[name] = c + stack;
                else { counts[name] = stack; order.Add(name); }

                try
                {
                    // 嵌套容器不做递归求值：GetCurrentValue(withChild:true) 会遍历整棵子树，
                    // 容器套容器时成本不可控。这类物品只计数、不计价。
                    bool nestedContainer = false;
                    try { nestedContainer = it.contentWindow != null; } catch { }
                    if (nestedContainer) continue;

                    long unit = it.GetCurrentValue(useRetailMarkup: false, withChild: true,
                                                   includeEvents: true, forceMarkup: false);
                    if (unit <= 0) continue;
                    long stackTotal = unit;
                    if (stack > 1)
                    {
                        if (unit > (long.MaxValue - totalValue) / stack) continue;
                        stackTotal = unit * stack;
                    }
                    if (stackTotal > long.MaxValue - totalValue) continue;
                    totalValue += stackTotal;
                }
                catch { }
            }

            if (order.Count == 0) return;

            var sb = new StringBuilder();
            sb.Append(zh ? "内含：" : "Contains: ");
            int shown = 0;
            foreach (var name in order)
            {
                if (shown >= MaxKinds) break;
                if (shown > 0) sb.Append(zh ? "、" : ", ");
                int n = counts[name];
                sb.Append(n > 1 ? $"{name}×{n}" : name);
                shown++;
            }
            if (order.Count > MaxKinds)
                sb.Append(zh ? $" 等 {order.Count} 类" : $" and {order.Count} kinds");

            lines.Add(new TooltipLine(sb.ToString(), Color));

            if (totalValue > 0)
            {
                lines.Add(new TooltipLine(
                    zh ? $"合计价值：{totalValue}" : $"Total value: {totalValue}",
                    Color));
            }
        }
        catch (Exception ex) { Log.Warn("StorageInfo.Contents", ex); }
    }
}
