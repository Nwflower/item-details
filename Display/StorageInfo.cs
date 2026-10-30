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
///
/// 箱子自身售价已经通过 GetCurrentValue(withChild:true) 包含内容物，这里不再逐件估价，
/// 避免与原价格行重复，也避免嵌套容器/估价失败造成另一个口径的“合计价值”。
/// 只读；仅对带网格内部库存的容器显示；槽位型容器（无宽高）跳过。
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
        internal string Locale = "";
        internal bool IncludeRatio;
        internal bool IncludeContents;
        internal float At;
        internal List<ContainerItemSignature>? Items;
        internal List<TooltipLine> Lines = new();
    }

    private static readonly List<Entry> Entries = new();

    internal static void Stop()
    {
        Entries.Clear();
    }

    internal static List<TooltipLine> Collect(GameItem? item, bool includeRatio = true, bool includeContents = true)
    {
        var lines = new List<TooltipLine>();
        if (item == null) return lines;

        try
        {
            GameGridInventory? grid = null;
            try
            {
                var cw = item.contentWindow;
                if (cw != null) grid = cw.child as GameGridInventory;
            }
            catch (Exception ex) { Log.Warn("StorageInfo.Window", ex); return lines; }
            if (grid == null) return lines;

            // 机器也带 contentWindow，但显示压缩率没意义，跳过（用游戏自己的类型判断）。
            bool isMachine = false;
            try { isMachine = item.IsGameItemType("MACHINE"); } catch { }
            if (isMachine) return lines;

            // 上锁容器交给 SealedBoxInfo 的独立透视开关。
            if (LockedContainer.IsLocked(item)) return lines;

            float now = UnityEngine.Time.unscaledTime;
            string locale = Loc.Code;
            Entry? entry = Find(item, grid, locale, includeRatio, includeContents);

            List<GameItem>? items = null;
            if (includeContents)
            {
                try { items = grid.childItems; }
                catch (Exception ex) { Log.Warn("StorageInfo.ChildItems", ex); }
            }

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
            if (includeRatio) AddRatio(item, grid, built);
            if (includeContents && items != null) AddContents(items, built);

            // 内容物读取失败时只保留本次压缩率，不写缓存，下次允许立即重试。
            if (!includeContents || items != null)
            {
                Store(item, grid, locale, includeRatio, includeContents, now, items, built, entry);
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

    private static Entry? Find(GameItem item, GameGridInventory grid, string locale,
                               bool includeRatio, bool includeContents)
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (ReferenceEquals(entry.Item, item) && ReferenceEquals(entry.Grid, grid)
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

    private static void Store(GameItem item, GameGridInventory grid, string locale,
                              bool includeRatio, bool includeContents, float now,
                              List<GameItem>? items, List<TooltipLine> lines, Entry? entry)
    {
        if (entry == null)
        {
            entry = new Entry
            {
                Item = item,
                Grid = grid,
                Locale = locale,
                IncludeRatio = includeRatio,
                IncludeContents = includeContents,
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

    private static void AddRatio(GameItem item, GameGridInventory grid, List<TooltipLine> lines)
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
                    // 内部网格 0 = 可用格（SetDataFill(0)），与物品外形的 1 = 实心相反。
                    innerCells = GameRead.UsableCells(inner);
                }
            }
            catch (Exception ex) { Log.Warn("StorageInfo.InnerShape", ex); }

            if (innerCells <= 0)
            {
                Log.Warn("StorageInfo.Ratio", $"内部网格可用格数为 0（{innerW}x{innerH}），跳过压缩率");
                return;
            }

            int selfCells = 0;
            GridShape? selfShape = null;
            try { selfShape = item.modifiedShape; } catch { }
            if (selfShape == null)
            {
                try { selfShape = item.shape; } catch { }
            }
            if (selfShape != null) selfCells = GameRead.OccupiedCells(selfShape);
            if (selfCells <= 0) return;

            double ratio = (double)innerCells / selfCells;
            lines.Add(new TooltipLine(
                Loc.T("storage.ratio", "Compression: {0} ({1}/{2} cells)",
                    ratio.ToString("0.0"), innerCells, selfCells),
                Color));
        }
        catch (Exception ex) { Log.Warn("StorageInfo.Ratio", ex); }
    }

    private static void AddContents(List<GameItem> items, List<TooltipLine> lines)
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
            sb.Append(Loc.T("storage.more_kinds", " ... {0} kinds in total", order.Count));

        lines.Add(new TooltipLine(sb.ToString(), Color));
    }
}
