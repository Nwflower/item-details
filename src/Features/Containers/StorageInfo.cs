using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 储存容器信息：压缩率 + 把内容物交给额外图标窗口，不生成文字清单或数量摘要。
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
/// 内容物只交给 <see cref="ContentsGrid"/> 画成图标格；图标格不可用时也没有文字回退。
///
/// 性能：tooltip 每帧重建。内容物按 <see cref="ContentsRefresh.IntervalSeconds"/> 刷新一次快照
/// （<see cref="ContainerContents"/>），两次刷新之间每帧只做一次引用比较；
/// 旧实现每帧都复制一次 childItems 并跑一次全量签名比对，2500 格容器会拖慢整帧。
/// </summary>
internal static class StorageInfo
{
    private const RenderHandler.ColorPalette Color = RenderHandler.ColorPalette.LightCyan;

    // 多槽缓存覆盖箱子来回切换；签名发现变化立即重建，TTL 只作漏检兜底。
    private const int MaxEntries = 16;
    private const float CacheSeconds = 5f;
    /// <summary>读取失败时旧快照最多再沿用这么久，避免一次异常就让图标格闪没。</summary>
    private const float SnapshotStaleSeconds = 1f;

    private sealed class Entry
    {
        internal GameItem Item = null!;
        internal GameGridInventory Grid = null!;
        internal int GridCount;
        internal string Locale = "";
        internal bool IncludeRatio;
        internal bool IncludeContents;
        internal float At;
        internal ContainerContents? Contents;
        internal float ScannedAt = float.MinValue;
        /// <summary>构建 <see cref="Lines"/> 时快照的 Revision；内容物变了就要重建行。</summary>
        internal int LinesRevision = -1;
        internal List<TooltipLine> Lines = new();
    }

    private static readonly List<Entry> Entries = new();

    internal static void Stop()
    {
        Entries.Clear();
    }

    /// <param name="allowGrid">tooltip 正文才传 true：允许把内容物交给图标格。</param>
    internal static List<TooltipLine> Collect(GameItem? item, bool includeRatio = true, bool includeContents = true,
        bool allowGrid = false, RichTextBuilder? builder = null)
    {
        var lines = new List<TooltipLine>();
        if (item == null) return lines;
        if (ThirdPartyProviders.HandlesTooltip(item, ItemDetailsTooltipSection.ValueEnhance)) includeRatio = false;
        if (ThirdPartyProviders.HandlesTooltip(item, ItemDetailsTooltipSection.Contents)) includeContents = false;

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

            Entry? entry = Find(item, grid, grids.Count, locale, includeRatio, includeContents);

            // 内容物：隔 RecheckSeconds 才重新读库存；中间每帧复用同一个快照（O(1)）。
            if (includeContents && allowGrid)
            {
                if (entry == null || entry.Contents == null || ContentsRefresh.Due(now, entry.ScannedAt))
                {
                    List<GameItem>? current = null;
                    try { current = ChildItems(grids); }
                    catch (Exception ex) { Log.Warn("StorageInfo.ChildItems", ex); }

                    if (current != null)
                    {
                        if (entry == null)
                        {
                            entry = NewEntry(item, grid, grids.Count, locale, includeRatio, includeContents);
                            Entries.Add(entry);
                        }
                        var snapshot = ContainerContents.Refresh(entry.Contents, current, sealedBox: false);
                        if (!ReferenceEquals(snapshot, entry.Contents))
                        {
                            entry.Contents = snapshot;
                            entry.LinesRevision = -1;
                        }
                        entry.ScannedAt = now;
                    }
                }

                if (entry?.Contents != null && now - entry.ScannedAt <= SnapshotStaleSeconds)
                {
                    // 没有文字回退：图标格不可用时，本次就不显示内容物。
                    _ = ContentsGrid.Claim(item, entry.Contents, sealedBox: false, builder: builder);
                }
            }

            if (entry != null && now - entry.At <= CacheSeconds
                && (!includeContents || !allowGrid || entry.Contents != null && entry.LinesRevision == entry.Contents.Revision))
            {
                Touch(entry);
                lines.AddRange(entry.Lines);
                if (lines.Count > 0) Log.MarkOk("StorageInfo");
                return lines;
            }

            var built = new List<TooltipLine>();
            if (includeRatio) AddRatio(item, grids, built);

            if (entry == null)
            {
                entry = NewEntry(item, grid, grids.Count, locale, includeRatio, includeContents);
                Entries.Add(entry);
            }
            else
            {
                Touch(entry);
            }
            entry.At = now;
            entry.Lines = built;
            entry.LinesRevision = entry.Contents?.Revision ?? -1;
            while (Entries.Count > MaxEntries) Entries.RemoveAt(0);

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

    private static Entry NewEntry(GameItem item, GameGridInventory grid, int gridCount, string locale,
                                  bool includeRatio, bool includeContents)
        => new()
        {
            Item = item,
            Grid = grid,
            GridCount = gridCount,
            Locale = locale,
            IncludeRatio = includeRatio,
            IncludeContents = includeContents,
        };

    private static Entry? Find(GameItem item, GameGridInventory grid, int gridCount, string locale,
                               bool includeRatio, bool includeContents)
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (ReferenceEquals(entry.Item, item) && ReferenceEquals(entry.Grid, grid)
                && entry.GridCount == gridCount
                && entry.Locale == locale && entry.IncludeRatio == includeRatio
                && entry.IncludeContents == includeContents)
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
                Color).WithData("compression", (innerCells, selfCells)));
        }
        catch (Exception ex) { Log.Warn("StorageInfo.Ratio", ex); }
    }

}
