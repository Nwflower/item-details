using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 密封箱预览：把带锁容器（保险箱 / 枪柜等）的内容物交给 <see cref="ContentsGrid"/> 显示，
/// 并在额外窗口标题中给出箱内估价。正文不生成文字清单、数量摘要或独立价值行。
/// 嵌套容器只计数、不递归求值；任何子项被跳过时，估价以“+”表示还有未计入部分。
///
/// 物资箱（<c>LOOT_CRATE_*</c>）在开锁时会按更高幸运重填，未开锁前真实 <c>childItems</c>
/// 仍是旧的。为了不误导玩家，这类箱子用探针箱按当前幸运模拟“开锁后会看到的内容”，
/// 详见 <see cref="TryGetLuckPreview"/>；保险箱/枪柜与未满足重填条件的箱子照常读真实内容。
///
/// ⚠️ 这是**透视**功能：原版必须先开锁才能看到箱内。
/// 开关由调用方（ModEntry）判断，模块本身不认识配置。
/// </summary>
internal static class SealedBoxInfo
{
    /// <summary>异常数据上限：超过就不显示内容物。</summary>
    private const int MaxChildren = 512;
    private const int MaxEntries = 16;
    private const float CacheSeconds = 5f;
    private const float ChildCacheSeconds = 10f;
    /// <summary>探针箱内容（当前幸运下的开锁预览）缓存时长与上限。</summary>
    private const float ProbeCacheSeconds = 3f;
    private const int MaxProbes = 4;

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
        internal float At;
        internal List<ContainerItemSignature> Items = new();
        internal string Estimate = "";
        internal readonly Dictionary<GameItem, ChildValue> Values =
            new(ReferenceIdentityComparer<GameItem>.Instance);
    }

    /// <summary>
    /// 当前幸运下的“开锁后内容”模拟预览。物资箱在开锁时会按更高幸运重填，
    /// 未开锁前真实 <c>childItems</c> 还是旧的；这里用探针箱提前算出重填结果。
    /// 探针会临时创建 GameItem，按 (真实箱子, 幸运) 缓存复用，换幸运/超时/停用时销毁。
    /// </summary>
    private sealed class Probe
    {
        internal GameItem Target = null!;   // 真实箱子，仅用于匹配，不读取其内容
        internal string CrateId = "";
        internal int Luck;
        internal float At;
        internal GameItem? Crate;           // 探针箱，用来保活子物品的图标与价值
        internal List<GameItem> Items = new();
        internal string Estimate = "";
    }

    private static readonly List<Entry> Entries = new();
    private static readonly List<Probe> Probes = new();

    internal static void Stop()
    {
        Entries.Clear();
        foreach (var probe in Probes) DestroyProbe(probe);
        Probes.Clear();
    }

    /// <param name="allowGrid">tooltip 正文才传 true：允许把内容物交给图标格。</param>
    internal static List<TooltipLine> Collect(GameItem? item, bool allowGrid = false, RichTextBuilder? builder = null)
    {
        var lines = new List<TooltipLine>();
        try
        {
            if (!allowGrid || item == null || !LockedContainer.IsLocked(item)
                || ThirdPartyProviders.HandlesTooltip(item, ItemDetailsTooltipSection.Contents)) return lines;

            GameGridInventory? grid = null;
            try
            {
                var cw = item.contentWindow;
                if (cw != null) grid = cw.child as GameGridInventory;
            }
            catch (Exception ex) { Log.Warn("SealedBox.Window", ex); return lines; }
            if (grid == null) return lines;

            List<GameItem>? items = null;
            try { items = grid.childItems; }
            catch (Exception ex) { Log.Warn("SealedBox.Items", ex); }
            if (items == null || items.Count == 0) return lines;
            if (items.Count > MaxChildren)
            {
                Log.Warn("SealedBox.Items", $"内容物 {items.Count} 件，超过上限 {MaxChildren}，跳过");
                return lines;
            }

            string estimate;
            List<GameItem> previewItems = items;
            if (TryGetLuckPreview(item, items, out var probeItems, out var probeEstimate))
            {
                previewItems = probeItems;
                estimate = probeEstimate;
            }
            else
            {
                estimate = EstimateTitleValue(item, grid, items);
            }
            // 没有文字回退：图标格不可用时，本次就不显示内容物与估价。
            _ = ContentsGrid.Claim(item, previewItems, sealedBox: true, sealedEstimate: estimate, builder: builder);
        }
        catch (Exception ex)
        {
            Log.Warn("SealedBoxInfo", ex);
        }
        return lines;
    }

    /// <summary>标题中的箱内估价；价格指纹与内容签名分缓存，变化最迟 5 秒刷新。</summary>
    internal static string EstimateTitleValue(GameItem item, GameGridInventory grid, List<GameItem> items)
    {
        float now = UnityEngine.Time.unscaledTime;
        Entry? entry = Find(item, grid);
        if (entry != null && now - entry.At <= CacheSeconds
            && ContainerItemSignature.Matches(entry.Items, items)) return entry.Estimate;

        if (entry == null)
        {
            entry = new Entry { Item = item, Grid = grid };
            Entries.Add(entry);
        }

        long sum = 0;
        bool skipped = false;
        foreach (var child in items)
        {
            if (child == null) { skipped = true; continue; }
            int stack = Math.Max(1, ContainerItemSignature.ReadStack(child));

            // 嵌套容器只计数，不展开另一棵库存树。
            if (child.contentWindow != null) { skipped = true; continue; }

            long unit = ReadUnitValue(entry, child, now, out bool read);
            if (!read) { skipped = true; continue; }
            if (unit <= 0) continue;
            if (!StackValue.TryTotal(unit, stack, out long lineTotal)
                || lineTotal > long.MaxValue - sum) skipped = true;
            else sum += lineTotal;
        }

        entry.At = now;
        entry.Items = ContainerItemSignature.Capture(items);
        entry.Estimate = sum + (skipped ? "+" : "");
        PruneChildren(entry, items);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
        return entry.Estimate;
    }

    private static long ReadUnitValue(Entry entry, GameItem child, float now, out bool read)
    {
        bool fingerprintValid = ItemValueFingerprint.TryRead(child, out long fingerprint);
        if (entry.Values.TryGetValue(child, out var cached)
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

        entry.Values[child] = new ChildValue
        {
            Fingerprint = fingerprint,
            FingerprintValid = fingerprintValid,
            At = now,
            Unit = unit,
            Read = read,
        };
        return unit;
    }

    private static Entry? Find(GameItem item, GameGridInventory grid)
    {
        for (int i = Entries.Count - 1; i >= 0; i--)
        {
            var entry = Entries[i];
            if (ReferenceEquals(entry.Item, item) && ReferenceEquals(entry.Grid, grid)) return entry;
        }
        return null;
    }

    private static void PruneChildren(Entry entry, List<GameItem> items)
    {
        if (entry.Values.Count == 0) return;
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

    // ---------------- 当前幸运下的开锁预览 ----------------

    /// <summary>
    /// 如果这只箱子会在开锁时因更高幸运被重填（<c>PreBuiltItemHelper.TryRefillForBetterLuck</c> 的条件），
    /// 就用探针箱模拟重填后的内容与估价。生成随机数前快照 <c>UnityEngine.Random.state</c>、
    /// 生成后立即还原，因此预览不会推进真实掉落的随机序列。
    /// 只在「物资箱 + 未扫描 + 当前幸运 &gt; 生成幸运」时成立；其余情况返回 false 走真实内容。
    /// </summary>
    internal static bool TryGetLuckPreview(GameItem item, List<GameItem> realItems,
                                           out List<GameItem> items, out string estimate)    {
        items = realItems;
        estimate = "";
        try
        {
            // 图标格关闭时没有任何地方显示这份预览，不做探针生成（避免白造物品）。
            if (!ModSettings.ContentsGrid) return false;
            if (!item.IsTag("LOOT_CRATE_ID_STRING") || item.IsTag("LOOT_CRATE_SCANNED_TAG")) return false;
            string crateId = GameRead.TagString(item, "LOOT_CRATE_ID_STRING") ?? "";
            if (crateId.Length == 0) return false;

            int luck = HealthData.GetLuck();
            if (luck <= LotteryHelper.GetSpawnLuck(item)) return false;
            if (!PreBuiltItemHelper.preBuiltItems.TryGetValue(crateId, out var factory) || factory == null) return false;

            float now = UnityEngine.Time.unscaledTime;
            Probe? probe = FindProbe(item, crateId, luck);
            if (probe != null && now - probe.At <= ProbeCacheSeconds && probe.Items.Count > 0)
            {
                // 滑动 TTL：持续悬停时沿用同一份样本，避免每 3 秒重新随机导致预览跳动。
                probe.At = now;
                items = probe.Items;
                estimate = probe.Estimate;
                return true;
            }

            if (probe == null)
            {
                probe = new Probe { Target = item, CrateId = crateId, Luck = luck };
                Probes.Add(probe);
            }
            else
            {
                DestroyProbeContents(probe);
                try { probe.Crate?.Destroy(); } catch { }
                probe.Crate = null;
            }

            GameItem? crate = null;
            var rng = UnityEngine.Random.state;
            // DirectoryMaster.Item 每建一个物品都会推进 PlayerStore.currentUniqueId，
            // 而它是 [ES3Serializable]（会写进存档）。探针物品不参与存档，这里用完即还原，
            // 并把探针子树改成本地负数 ID，避免与之后真实分配的 ID 冲突。
            bool hasSavedId = false;
            int savedId = 0;
            try
            {
                try { savedId = PlayerStore.Instance.currentUniqueId; hasSavedId = true; } catch { }
                try { crate = factory(); }
                finally
                {
                    UnityEngine.Random.state = rng;
                    if (hasSavedId) { try { PlayerStore.Instance.currentUniqueId = savedId; } catch { } }
                }
            }
            finally
            {
                if (crate != null) ReassignProbeIds(crate);
            }

            var grid = crate?.contentWindow?.child as GameGridInventory;
            var children = grid?.childItems;
            if (crate == null || grid == null || children == null)
            {
                try { crate?.Destroy(); } catch { }
                Probes.Remove(probe);
                return false;
            }

            probe.Crate = crate;
            probe.Items = children;
            probe.Estimate = EstimateChildren(children);
            probe.At = now;
            PruneProbes(item, crateId, luck);
            items = probe.Items;
            estimate = probe.Estimate;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("SealedBox.LuckPreview", ex);
            items = realItems;
            estimate = "";
            return false;
        }
    }

    /// <summary>与 <see cref="EstimateTitleValue"/> 相同口径，用于探针箱内容（探针是全新的，不另建子项缓存）。</summary>
    private static string EstimateChildren(List<GameItem> items)
    {
        long sum = 0;
        bool skipped = false;
        foreach (var child in items)
        {
            if (child == null) { skipped = true; continue; }
            int stack = Math.Max(1, ContainerItemSignature.ReadStack(child));
            if (child.contentWindow != null) { skipped = true; continue; }

            long unit = 0;
            bool read = false;
            try
            {
                unit = child.GetCurrentValue(useRetailMarkup: false, withChild: true,
                                             includeEvents: true, forceMarkup: false);
                read = true;
            }
            catch { }
            if (!read) { skipped = true; continue; }
            if (unit <= 0) continue;
            if (!StackValue.TryTotal(unit, stack, out long lineTotal)
                || lineTotal > long.MaxValue - sum) skipped = true;
            else sum += lineTotal;
        }
        return sum + (skipped ? "+" : "");
    }

    private static Probe? FindProbe(GameItem target, string crateId, int luck)
    {
        for (int i = Probes.Count - 1; i >= 0; i--)
        {
            var probe = Probes[i];
            if (ReferenceEquals(probe.Target, target) && probe.CrateId == crateId && probe.Luck == luck) return probe;
        }
        return null;
    }

    /// <summary>同一只箱子只保留当前幸运对应的探针；再按 LRU 限制总数。</summary>
    private static void PruneProbes(GameItem target, string crateId, int luck)
    {
        for (int i = Probes.Count - 1; i >= 0; i--)
        {
            var probe = Probes[i];
            if (ReferenceEquals(probe.Target, target) && (probe.CrateId != crateId || probe.Luck != luck))
            {
                DestroyProbe(probe);
                Probes.RemoveAt(i);
            }
        }
        while (Probes.Count > MaxProbes)
        {
            DestroyProbe(Probes[0]);
            Probes.RemoveAt(0);
        }
    }

    private static void DestroyProbe(Probe probe)
    {
        DestroyProbeContents(probe);
        try { probe.Crate?.Destroy(); } catch { }
        probe.Crate = null;
    }

    private static void DestroyProbeContents(Probe probe)
    {
        foreach (var child in probe.Items)
        {
            if (child == null) continue;
            try { child.Destroy(); } catch { }
        }
        probe.Items = new List<GameItem>();
    }

    /// <summary>
    /// 探针子树改成负数本地 ID：既不再占用 PlayerStore 的真实 ID 空间，
    /// 也不会和之后真实分配的 ID 撞车（探针不进入任何库存，不会参与存档）。
    /// </summary>
    private static void ReassignProbeIds(GameItem crate)
    {
        try
        {
            int next = -1;
            crate.uniqueId = next--;
            foreach (var child in crate.FindAllChildItems())
            {
                if (child == null) continue;
                child.uniqueId = next--;
            }
        }
        catch (Exception ex) { Log.Warn("SealedBox.ProbeIds", ex); }
    }
}
