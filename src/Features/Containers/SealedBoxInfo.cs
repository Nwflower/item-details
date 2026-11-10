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
    private const float SnapshotStaleSeconds = 1f;
    private const float CacheSeconds = 5f;
    private const float ChildCacheSeconds = 10f;
    /// <summary>探针箱的重算最小间隔（一次重算约 50–100 ms）与保留上限。</summary>
    private const float ProbeMinIntervalSeconds = 1f;
    /// <summary>悬停满这么久才允许模拟，鼠标划过不触发；超过间隔没再构建 tooltip 视为已移开。</summary>
    private const float ProbeHoverDelaySeconds = 0.2f;
    private const float HoverGapSeconds = 0.5f;
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
        /// <summary>内容物快照；两次刷新之间每帧只比引用。</summary>
        internal ContainerContents? Contents;
        internal float ScannedAt = float.MinValue;
        internal float EstimateAt = float.MinValue;
        internal int EstimateRevision = -1;
        internal readonly Dictionary<GameItem, ChildValue> Values =
            new(ReferenceIdentityComparer<GameItem>.Instance);
    }

    /// <summary>
    /// 当前幸运下的“开锁后内容”模拟预览。物资箱在开锁时会按更高幸运重填，
    /// 未开锁前真实 <c>childItems</c> 还是旧的；这里用探针箱提前算出重填结果。
    /// 探针会临时创建 GameItem，按真实箱子缓存复用；只有全局随机状态或幸运变了才需要重算，
    /// 且同一只箱子两次重算至少间隔 <see cref="ProbeMinIntervalSeconds"/>。超出上限或停用时销毁。
    /// </summary>
    private sealed class Probe
    {
        internal GameItem Target = null!;   // 真实箱子，仅用于匹配，不读取其内容
        internal string CrateId = "";
        internal int Luck;
        internal UnityEngine.Random.State Rng;  // 生成这份样本时的全局随机状态
        internal float At;                  // 上次重算时间
        internal GameItem? Crate;           // 探针箱，用来保活子物品的图标与价值
        internal List<GameItem> Items = new();
        internal string Estimate = "";
    }

    private static readonly List<Entry> Entries = new();
    private static readonly List<Probe> Probes = new();
    private static GameItem? _hoverItem;
    private static float _hoverSince;
    private static float _hoverSeen;

    internal static void Stop()
    {
        _hoverItem = null;
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

            float now = UnityEngine.Time.unscaledTime;
            Entry? entry = Find(item, grid);
            ContainerContents? contents = entry?.Contents;

            // 每帧重建 tooltip，但内容物通常不变：隔 RecheckSeconds 才重新读库存，
            // 中间每帧复用同一个快照（O(1)）。
            if (contents == null || ContentsRefresh.Due(now, entry!.ScannedAt))
            {
                List<GameItem>? current = null;
                try { current = grid.childItems; }
                catch (Exception ex) { Log.Warn("SealedBox.Items", ex); }

                if (current != null)
                {
                    if (entry == null)
                    {
                        entry = new Entry { Item = item, Grid = grid };
                        Remember(entry);
                    }
                    var refreshed = ContainerContents.Refresh(entry.Contents, current, sealedBox: true);
                    if (!ReferenceEquals(refreshed, entry.Contents))
                    {
                        entry.Contents = refreshed;
                        entry.EstimateRevision = -1;
                        entry.At = float.MinValue;   // 估价缓存作废；逐件价值按指纹与 PruneChildren 自行复用
                    }
                    entry.ScannedAt = now;
                    contents = entry.Contents;
                }
            }

            if (contents == null || now - entry!.ScannedAt > SnapshotStaleSeconds || contents.Items.Count == 0) return lines;
            if (contents.Items.Count > MaxChildren)
            {
                Log.Warn("SealedBox.Items", $"内容物 {contents.Items.Count} 件，超过上限 {MaxChildren}，跳过");
                return lines;
            }

            List<GameItem> items = contents.Items;
            string estimate;
            bool settled = HoverSettled(item, now);
            if (TryGetLuckPreview(item, items, out var probeItems, out var probeEstimate, settled))
            {
                // 还没有样本（等待悬停）时不显示旧内容，否则会先闪一下与预览不符的物品。
                if (probeItems.Count == 0) return lines;
                // 探针列表是稳定实例，ContentsGrid 按实例缓存快照，不会每帧重建。
                _ = ContentsGrid.Claim(item, probeItems, sealedBox: true, sealedEstimate: probeEstimate, builder: builder);
                return lines;
            }

            if (entry!.EstimateRevision != contents.Revision || now - entry.EstimateAt > CacheSeconds)
            {
                entry.Estimate = EstimateTitleValue(item, grid, items);
                entry.EstimateAt = now;
                entry.EstimateRevision = contents.Revision;
            }
            estimate = entry.Estimate;
            // 没有文字回退：图标格不可用时，本次就不显示内容物与估价。
            _ = ContentsGrid.Claim(item, contents, sealedBox: true, sealedEstimate: estimate, builder: builder);
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
            Remember(entry);
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

    private static void Remember(Entry entry)
    {
        Entries.Add(entry);
        if (Entries.Count > MaxEntries) Entries.RemoveAt(0);
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
    /// 样本只反映当前随机状态：开箱前游戏再抽随机数，结果就可能不同，所以它是预览而不是保证。
    /// </summary>
    /// <param name="settled">false 时不做模拟：有样本就沿用，没有就返回 true 和空列表。</param>
    internal static bool TryGetLuckPreview(GameItem item, List<GameItem> realItems,
                                           out List<GameItem> items, out string estimate, bool settled = true)    {
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
            var rng = UnityEngine.Random.state;
            Probe? probe = FindProbe(item, crateId);
            if (probe?.Crate != null
                && ((probe.Luck == luck && probe.Rng.Equals(rng))
                    || !settled
                    || now - probe.At < ProbeMinIntervalSeconds))
            {
                // 状态没变时样本仍然有效；变了但未满悬停或间隔时先沿用，限制重算造成的卡顿。
                items = probe.Items;
                estimate = probe.Estimate;
                return true;
            }
            if (!settled)
            {
                items = new List<GameItem>();
                return true;
            }

            if (probe == null)
            {
                probe = new Probe { Target = item, CrateId = crateId };
                Probes.Add(probe);
            }
            else
            {
                DestroyProbeContents(probe);
                try { probe.Crate?.Destroy(); } catch { }
                probe.Crate = null;
            }

            GameItem? crate = null;
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
            probe.Luck = luck;
            probe.Rng = rng;
            probe.Items = children;
            probe.Estimate = EstimateChildren(children);
            probe.At = now;
            PruneProbes();
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

    /// <summary>tooltip 每帧重建，据此判断鼠标是否已在同一件物品上停留够久。</summary>
    internal static bool HoverSettled(GameItem item, float now)
    {
        if (!ReferenceEquals(_hoverItem, item) || now - _hoverSeen > HoverGapSeconds)
        {
            _hoverItem = item;
            _hoverSince = now;
        }
        _hoverSeen = now;
        return now - _hoverSince >= ProbeHoverDelaySeconds;
    }

    private static Probe? FindProbe(GameItem target, string crateId)
    {
        for (int i = Probes.Count - 1; i >= 0; i--)
        {
            var probe = Probes[i];
            if (ReferenceEquals(probe.Target, target) && probe.CrateId == crateId) return probe;
        }
        return null;
    }

    /// <summary>限制探针总数，先销毁最早建立的。</summary>
    private static void PruneProbes()
    {
        while (Probes.Count > MaxProbes)
        {
            DestroyProbe(Probes[0]);
            Probes.RemoveAt(0);
        }
    }

#if ITEMDETAILS_SELFTEST
    /// <summary>自检用：让探针视作已过重算间隔，不必真的等待。</summary>
    internal static void ExpireProbeIntervals()
    {
        foreach (var probe in Probes) probe.At = float.NegativeInfinity;
    }
#endif

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
