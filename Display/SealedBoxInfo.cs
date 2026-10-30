using System;
using System.Collections.Generic;
using System.Text;

namespace ItemDetails;

/// <summary>
/// 密封箱预览：提前列出带锁容器（保险箱 / 枪柜等）的内容物与箱内价值。
/// 嵌套容器只列名称、不递归求值；任何子项被跳过时，总价以“+”表示还有未计入部分。
///
/// ⚠️ 这是**透视**功能：原版必须先开锁才能看到箱内。
/// 开关由调用方（ModEntry）判断，模块本身不认识配置。
/// </summary>
internal static class SealedBoxInfo
{
    private const RenderHandler.ColorPalette Color = RenderHandler.ColorPalette.LightTurquoise;

    private const int MaxItems = 8;

    /// <summary>异常数据上限：超过就不列明细、不给总价。</summary>
    private const int MaxChildren = 512;

    private const int MaxEntries = 16;
    private const float CacheSeconds = 5f;
    private const float ChildCacheSeconds = 10f;

    private sealed class ChildValue
    {
        internal long Fingerprint;
        internal bool FingerprintValid;
        internal float At;
        internal long Unit;
        internal bool Read;
    }

    private sealed class Entry
    {
        internal GameItem Item = null!;
        internal GameGridInventory Grid = null!;
        internal string Locale = "";
        internal float At;
        internal List<ContainerItemSignature> Items = new();
        internal List<TooltipLine> Lines = new();
        internal readonly Dictionary<GameItem, ChildValue> Values =
            new(ReferenceIdentityComparer<GameItem>.Instance);
    }

    private static readonly List<Entry> Entries = new();

    internal static void Stop()
    {
        Entries.Clear();
    }

    internal static List<TooltipLine> Collect(GameItem? item)
    {
        var lines = new List<TooltipLine>();
        try
        {
            if (item == null || !LockedContainer.IsLocked(item)) return lines;

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
            Entry? entry = Find(item, grid, locale);

            List<GameItem>? items = null;
            try { items = grid.childItems; }
            catch (Exception ex) { Log.Warn("SealedBox.Items", ex); }
            if (items == null) return lines;

            if (entry != null && now - entry.At <= CacheSeconds
                && ContainerItemSignature.Matches(entry.Items, items))
            {
                Touch(entry);
                lines.AddRange(entry.Lines);
                if (lines.Count > 0) Log.MarkOk("SealedBoxInfo");
                return lines;
            }

            if (items.Count > MaxChildren)
            {
                Log.Warn("SealedBox.Items", $"内容物 {items.Count} 件，超过上限 {MaxChildren}，跳过");
                Store(item, grid, locale, now, items, lines, entry);
                return lines;
            }

            // 先建立箱子条目，首次构建也能把子项价值留在箱内供后续差异刷新复用。
            if (entry == null)
            {
                entry = new Entry { Item = item, Grid = grid, Locale = locale };
                Entries.Add(entry);
            }
            BuildLines(items, now, entry, lines);
            Store(item, grid, locale, now, items, lines, entry);
            if (lines.Count > 0) Log.MarkOk("SealedBoxInfo");
        }
        catch (Exception ex)
        {
            Log.Warn("SealedBoxInfo", ex);
        }
        return lines;
    }

    private static void BuildLines(List<GameItem> items, float now, Entry? entry,
                                   List<TooltipLine> lines)
    {
        var contents = new StringBuilder();
        int shown = 0;
        int skipped = 0;
        long sum = 0;

        foreach (var child in items)
        {
            if (child == null) { skipped++; continue; }

            int stack = ContainerItemSignature.ReadStack(child);
            bool nestedContainer = child.contentWindow != null;

            long unit = 0;
            bool read = false;
            if (nestedContainer)
            {
                // 保持现有规则：嵌套容器只计数，不展开另一棵库存树。
                skipped++;
            }
            else
            {
                unit = ReadUnitValue(entry, child, now, out read);
                if (!read) skipped++;
            }

            if (read && unit > 0)
            {
                if (!StackValue.TryTotal(unit, stack, out long lineTotal)
                    || lineTotal > long.MaxValue - sum)
                {
                    skipped++;
                }
                else
                {
                    sum += lineTotal;
                }
            }

            if (shown >= MaxItems) continue;
            if (shown > 0) contents.Append(Loc.T("list.sep", ", "));
            contents.Append(ContainerItemSignature.DisplayName(child));
            if (read && unit > 0) contents.Append(Loc.T("sealed.unit", " ({0})", unit));
            shown++;
        }

        if (shown == 0)
        {
            lines.Add(new TooltipLine(Loc.T("sealed.empty", "Inside: empty"), Color));
            return;
        }

        string more = items.Count > shown
            ? Loc.T("sealed.more", " ... {0} items in total", items.Count)
            : "";
        if (sum > 0 || skipped > 0)
        {
            // 合计写在清单之前；“+”只表示仍有物品未计入，不把这个下限额伪装成完整总价。
            string value = skipped > 0 ? sum.ToString() + "+" : sum.ToString();
            lines.Add(new TooltipLine(
                Loc.T("sealed.contents_total", "Inside (total {0}): {1}{2}", value, contents.ToString(), more),
                Color));
        }
        else
        {
            lines.Add(new TooltipLine(
                Loc.T("sealed.contents", "Inside: {0}{1}", contents.ToString(), more),
                Color));
        }
    }

    private static long ReadUnitValue(Entry? entry, GameItem child, float now, out bool read)
    {
        bool fingerprintValid = ItemValueFingerprint.TryRead(child, out long fingerprint);
        if (entry != null && entry.Values.TryGetValue(child, out var cached)
            && fingerprintValid && cached.FingerprintValid
            && cached.Fingerprint == fingerprint && now - cached.At <= ChildCacheSeconds)
        {
            read = cached.Read;
            return cached.Unit;
        }

        long unit = 0;
        read = false;
        try
        {
            unit = child.GetCurrentValue(useRetailMarkup: false, withChild: true,
                                         includeEvents: true, forceMarkup: false);
            read = true;
        }
        catch { }

        if (entry != null)
        {
            entry.Values[child] = new ChildValue
            {
                Fingerprint = fingerprint,
                FingerprintValid = fingerprintValid,
                At = now,
                Unit = unit,
                Read = read,
            };
        }
        return unit;
    }

    private static Entry? Find(GameItem item, GameGridInventory grid, string locale)
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (ReferenceEquals(entry.Item, item) && ReferenceEquals(entry.Grid, grid)
                && entry.Locale == locale)
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

    private static void Store(GameItem item, GameGridInventory grid, string locale, float now,
                              List<GameItem> items, List<TooltipLine> lines, Entry? entry)
    {
        if (entry == null)
        {
            entry = new Entry { Item = item, Grid = grid, Locale = locale };
            Entries.Add(entry);
        }
        else
        {
            Touch(entry);
        }

        entry.At = now;
        entry.Items = ContainerItemSignature.Capture(items);
        entry.Lines = new List<TooltipLine>(lines);

        // 已移出箱子的物品不继续占子项缓存；箱子条目淘汰时整表一起释放。
        if (entry.Values.Count > 0)
        {
            var present = new HashSet<GameItem>(ReferenceIdentityComparer<GameItem>.Instance);
            foreach (var child in items) if (child != null) present.Add(child);
            List<GameItem>? removed = null;
            foreach (var child in entry.Values.Keys)
            {
                if (present.Contains(child)) continue;
                (removed ??= new List<GameItem>()).Add(child);
            }
            if (removed != null) foreach (var child in removed) entry.Values.Remove(child);
        }

        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }
}
