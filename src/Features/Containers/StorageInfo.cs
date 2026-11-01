using System;
using System.Collections.Generic;
using System.Text;

namespace ItemDetails;

/// <summary>
/// 储存容器信息：压缩率 + 内容物清单。
///
/// 数据来源（已在正式版 0.46D 源码核实）：
///   - 容器内部库存：DirectoryUtils.CreateInventoryWindow(w,h) 返回 (PixelWindow, GameGridInventory)
///     并 pixelWindow.Attach(grid)，随后 GameItem.SetContentWindow(pixelWindow)；
///     因此 item.contentWindow.child as GameGridInventory 即内部库存。
///   - 内部格数 = grid.inventoryShape 的**可用格**数（0 = 可用，与物品外形的 1 = 实心相反）
///   - 内容物   = grid.childItems（GameGridInventory 覆写，返回 items 的副本）
///   - 自身占格 = item.modifiedShape 的实心格数
///   - 储藏区扩建后，contentWindow 的根换成 MachineryPanelRow，每个标签页一个网格；
///     contentWindow.child 只返回第一页。所以按 MachineryContainerUtils.Collect 收集全部网格，
///     格数与内容物都按所有页合计。根下只要有一个不是网格的库存（槽位等），就归 MachineInterior。
///
/// 箱子自身售价已经通过 GetCurrentValue(withChild:true) 包含内容物，这里不再逐件估价，
/// 避免与原价格行重复，也避免嵌套容器/估价失败造成另一个口径的“合计价值”。
/// 只读；仅对带网格内部库存的容器显示；槽位型容器（无宽高）跳过。
/// 内容物交给 <see cref="ContentsGrid"/> 画成图标格时，清单缩成一行「N 类 M 件」。
/// </summary>
internal static class StorageInfo
{
    private const RenderHandler.ColorPalette Color = RenderHandler.ColorPalette.LightCyan;

    // 内容物最多列出的种类数，超出折叠，避免 tooltip 过宽。
    private const int MaxKinds = 5;

    // 多槽缓存覆盖箱子来回切换；签名发现变化立即重建，TTL 只作漏检兜底。
    private const int MaxEntries = 16;
    private const float CacheSeconds = 5f;

    private sealed class Entry
    {
        internal GameItem Item = null!;
        internal GameGridInventory Grid = null!;
        internal int GridCount;
        internal string Locale = "";
        internal bool IncludeRatio;
        internal bool IncludeContents;
        internal bool UseGrid;
        internal float At;
        internal List<ContainerItemSignature>? Items;
        internal List<TooltipLine> Lines = new();
    }

    private static readonly List<Entry> Entries = new();

    internal static void Stop()
    {
        Entries.Clear();
    }

    /// <param name="allowGrid">tooltip 正文才传 true：允许把清单交给图标格、自己只写摘要。</param>
    internal static List<TooltipLine> Collect(GameItem? item, bool includeRatio = true, bool includeContents = true,
        bool allowGrid = false)
    {
        var lines = new List<TooltipLine>();
        if (item == null) return lines;

        try
        {
            List<GameGridInventory>? grids;
            try { grids = GridStorage(item); }
            catch (Exception ex) { Log.Warn("StorageInfo.Window", ex); return lines; }
            if (grids == null) return lines;
            var grid = grids[0];

            // 机器也带 contentWindow，但显示压缩率没意义，跳过（用游戏自己的类型判断）。
            bool isMachine = false;
            try { isMachine = item.IsGameItemType("MACHINE"); } catch { }
            if (isMachine) return lines;

            // 上锁容器交给 SealedBoxInfo 的独立透视开关。
            if (LockedContainer.IsLocked(item)) return lines;

            float now = UnityEngine.Time.unscaledTime;
            string locale = Loc.Code;

            List<GameItem>? items = null;
            if (includeContents)
            {
                try { items = ChildItems(grids); }
                catch (Exception ex) { Log.Warn("StorageInfo.ChildItems", ex); }
            }

            bool useGrid = allowGrid && items != null && items.Count > 0 && ContentsGrid.Claim(item, sealedBox: false);
            Entry? entry = Find(item, grid, grids.Count, locale, includeRatio, includeContents, useGrid);

            if (entry != null && now - entry.At <= CacheSeconds
                && (!includeContents || items != null
                    && ContainerItemSignature.Matches(entry.Items!, items)))
            {
                Touch(entry);
                lines.AddRange(entry.Lines);
                if (lines.Count > 0) Log.MarkOk("StorageInfo");
                return lines;
            }

            var built = new List<TooltipLine>();
            if (includeRatio) AddRatio(item, grids, built);
            if (includeContents && items != null) AddContents(items, built, useGrid);

            // 内容物读取失败时只保留本次压缩率，不写缓存，下次允许立即重试。
            if (!includeContents || items != null)
            {
                Store(item, grid, grids.Count, locale, includeRatio, includeContents, useGrid, now, items, built, entry);
            }

            lines.AddRange(built);
            if (lines.Count > 0) Log.MarkOk("StorageInfo");
        }
        catch (Exception ex)
        {
            Log.Warn("StorageInfo", ex);
        }
        return lines;
    }

    /// <summary>
    /// 该物品的全部网格库存（扩建储藏区的每个标签页各一个）。根下有非网格库存、或一个网格都没有时返回 null。
    /// </summary>
    internal static List<GameGridInventory>? GridStorage(GameItem item)
    {
        var root = item.contentWindow?.childElement;
        if (root == null) return null;
        // MachineryPanelRow 自己也是 GraphNodeStorage，Collect(row) 只会返回这一行；
        // 要用 row.children 往下取（面板 → 标签页 → 每页的网格）。
        var storages = root is MachineryPanelRow row ? row.children : MachineryContainerUtils.Collect(root);
        var grids = new List<GameGridInventory>();
        foreach (var storage in storages)
        {
            if (storage is not GameGridInventory grid) return null;
            grids.Add(grid);
        }
        return grids.Count > 0 ? grids : null;
    }

    /// <summary>所有网格的内容物，按标签页顺序拼接。</summary>
    internal static List<GameItem> ChildItems(List<GameGridInventory> grids)
    {
        if (grids.Count == 1) return grids[0].childItems;
        var all = new List<GameItem>();
        foreach (var grid in grids)
        {
            var children = grid.childItems;
            if (children != null) all.AddRange(children);
        }
        return all;
    }

    private static Entry? Find(GameItem item, GameGridInventory grid, int gridCount, string locale,
                               bool includeRatio, bool includeContents, bool useGrid)
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (ReferenceEquals(entry.Item, item) && ReferenceEquals(entry.Grid, grid)
                && entry.GridCount == gridCount
                && entry.Locale == locale && entry.IncludeRatio == includeRatio
                && entry.IncludeContents == includeContents && entry.UseGrid == useGrid)
            {
                return entry;
            }
        }
        return null;
    }

    private static void Touch(Entry entry)
    {
        int index = Entries.IndexOf(entry);
        if (index < 0 || index == Entries.Count - 1) return;
        Entries.RemoveAt(index);
        Entries.Add(entry);
    }

    private static void Store(GameItem item, GameGridInventory grid, int gridCount, string locale,
                              bool includeRatio, bool includeContents, bool useGrid, float now,
                              List<GameItem>? items, List<TooltipLine> lines, Entry? entry)
    {
        if (entry == null)
        {
            entry = new Entry
            {
                Item = item,
                Grid = grid,
                GridCount = gridCount,
                Locale = locale,
                IncludeRatio = includeRatio,
                IncludeContents = includeContents,
                UseGrid = useGrid,
            };
            Entries.Add(entry);
        }
        else
        {
            Touch(entry);
        }

        entry.At = now;
        entry.Items = items == null ? null : ContainerItemSignature.Capture(items);
        entry.Lines = lines;
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }

    private static void AddRatio(GameItem item, List<GameGridInventory> grids, List<TooltipLine> lines)
    {
        try
        {
            int innerCells = 0;
            foreach (var grid in grids)
            {
                try
                {
                    var inner = grid.inventoryShape;
                    // 内部网格 0 = 可用格（SetDataFill(0)），与物品外形的 1 = 实心相反。
                    if (inner != null) innerCells += GameRead.UsableCells(inner);
                }
                catch (Exception ex) { Log.Warn("StorageInfo.InnerShape", ex); }
            }

            if (innerCells <= 0)
            {
                Log.Warn("StorageInfo.Ratio", $"内部网格可用格数为 0（{grids.Count} 个网格），跳过压缩率");
                return;
            }

            int selfCells = 0;
            var selfShape = item.modifiedShape ?? item.shape;
            if (selfShape != null) selfCells = GameRead.OccupiedCells(selfShape);
            if (selfCells <= 0) return;

            double ratio = (double)innerCells / selfCells;
            lines.Add(new TooltipLine(
                Loc.T("storage.ratio", "Compression: {0} ({1}/{2} {2|cell|cells})",
                    ratio.ToString("0.0", Loc.Culture), innerCells, selfCells),
                Color));
        }
        catch (Exception ex) { Log.Warn("StorageInfo.Ratio", ex); }
    }

    private static void AddContents(List<GameItem> items, List<TooltipLine> lines, bool useGrid)
    {
        if (items.Count == 0) return;

        // 只聚合名称和数量；箱子售价已包含内容物，不再在这里重复逐件估值。
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var item in items)
        {
            if (item == null) continue;
            string name = ContainerItemSignature.DisplayName(item);
            int stack = ContainerItemSignature.ReadStack(item);
            if (counts.TryGetValue(name, out int count)) counts[name] = count + stack;
            else { counts[name] = stack; order.Add(name); }
        }
        if (order.Count == 0) return;

        if (useGrid)
        {
            int pieces = 0;
            foreach (int count in counts.Values) pieces += count;
            lines.Add(new TooltipLine(Loc.T("storage.summary", "Contains: {0} {0|kind|kinds}, {1} {1|item|items}", order.Count, pieces), Color));
            return;
        }

        var sb = new StringBuilder();
        sb.Append(Loc.T("storage.contains", "Contains: "));
        int shown = 0;
        foreach (string name in order)
        {
            if (shown >= MaxKinds) break;
            if (shown > 0) sb.Append(Loc.T("list.sep", ", "));
            int count = counts[name];
            sb.Append(count > 1 ? $"{name}×{count}" : name);
            shown++;
        }
        if (order.Count > MaxKinds)
            sb.Append(Loc.T("storage.more_kinds", " ... {0} {0|kind|kinds} in total", order.Count));

        lines.Add(new TooltipLine(sb.ToString(), Color));
    }
}
