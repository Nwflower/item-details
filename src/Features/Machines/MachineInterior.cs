using System;
using System.Collections.Generic;
using System.Text;

namespace ItemDetails;

/// <summary>
/// 机器与槽位容器的内部摘要：分三层，原版机器和第三方机器走同一条路径。
///
///   1. 骨架（本文件）：按结构识别——<c>contentWindow.childElement is MachineryPanelRow</c> 即机器，
///      不看 MACHINE 类型或物品 ID；逐个面板读出 role、已本地化的标题和内容物。
///      跳过 power / module / note 面板（电池和模块另有模块显示，说明书没有信息量）。
///   2. 内容渲染（本文件）：按物品标签识别滤芯、液体容器、计次工具、电池，放在任何机器里都按同一规则显示。
///   3. 专门适配（<see cref="MachineAdapters"/>）：进度剩余几天、水培用水、冰箱、催陈器、
///      自学习算法模块的学习进度（模块面板不列出，但读出来给适配器汇总）。
///      适配器先跑，可以"认领"面板；没被认领、也没被第二层汇总的物品由骨架按面板通用列出。
///
/// 只读：不调用 mayInventoryAddItemFunc、onCycleEnd* 这类委托，标签走 GameRead。
/// 某种机器结构异常时只停用这一种（按 identifier 计数），不拖垮整个模块。
/// 网格式非机器容器仍由 <see cref="StorageInfo"/> 处理，包括根是 MachineryPanelRow、但库存全是网格的扩建储藏区。
/// </summary>
internal static class MachineInterior
{
    private const RenderHandler.ColorPalette Info = RenderHandler.ColorPalette.LightCyan;
    private const RenderHandler.ColorPalette Warn = RenderHandler.ColorPalette.LightYellow;
    private const RenderHandler.ColorPalette Bad = RenderHandler.ColorPalette.LightRed;

    /// <summary>超过这么多个非空面板就合并成一行，避免工具箱这类九宫格刷屏。</summary>
    private const int MaxPanelLines = 3;
    private const int MaxKinds = 4;
    private const int MaxLiquidLines = 2;
    /// <summary>每个面板最多读这么多件；第三方机器可能有很大的网格。</summary>
    private const int MaxItemsPerPanel = 200;

    private const int FailureLimit = 3;
    private const int MaxEntries = 8;
    /// <summary>耐久、水量、进度不改变物品列表也会变，缓存只挡同一次悬停里的重复构建。</summary>
    private const float CacheSeconds = 1f;
    /// <summary>
    /// tooltip 每帧重建；这段时间内同一台机器直接复用上次的行，不再逐面板读结构、比对签名。
    /// </summary>
    private const float RecheckSeconds = 0.25f;

    private static readonly HashSet<string> SkippedRoles = new(StringComparer.Ordinal)
    {
        "power", "module", "note",
    };

    internal sealed class Panel
    {
        internal string Role = "";
        internal string Label = "";
        internal readonly List<GameItem> Items = new();
        /// <summary>适配器已完整显示这个面板，第二层和通用列表都跳过它。</summary>
        internal bool Claimed;
    }

    internal sealed class Context
    {
        internal GameItem Machine = null!;
        /// <summary>有 MachineryPanelRow 结构；槽位容器（过滤水壶等）为 false，不跑适配器。</summary>
        internal bool IsMachine;
        internal readonly List<Panel> Panels = new();
        /// <summary>role 为 power 的面板存在与否、以及其中的电源物品。</summary>
        internal bool HasPowerPanel;
        internal GameItem? PowerSource;
        /// <summary>role 为 module 的面板里的模块；不列出，只给适配器汇总学习进度用。</summary>
        internal readonly List<GameItem> Modules = new();
        /// <summary>已被第二层汇总过的物品，通用列表不再重复。</summary>
        internal readonly HashSet<GameItem> Summarized = new();
        /// <summary>水培适配器算出的"水还够几天"；进度适配器据此补充提示。-1 表示不适用。</summary>
        internal int WaterDays = -1;

        internal Panel? FindPanel(string role)
        {
            foreach (var p in Panels)
                if (string.Equals(p.Role, role, StringComparison.Ordinal)) return p;
            return null;
        }
    }

    private sealed class Entry
    {
        internal GameItem Item = null!;
        internal string Locale = "";
        internal int Flags;
        internal float At;
        /// <summary>上次确认内容物没变的时间。</summary>
        internal float CheckedAt;
        internal List<ContainerItemSignature> Items = new();
        internal List<TooltipLine> Lines = new();
    }

    private static readonly List<Entry> Entries = new();
    private static readonly Dictionary<string, int> Failures = new(StringComparer.Ordinal);

    internal static void Stop()
    {
        Entries.Clear();
        Failures.Clear();
    }

    /// <param name="listing">列出内容物并做第二层汇总（「容器内容」开关）。</param>
    /// <param name="adapters">专门适配：剩余几天、用水、冷藏、陈化（「信息增强」开关）。</param>
    /// <param name="hiddenTruth">滤芯显示真实耐久（「透视」开关）；关闭时与物品自身 tooltip 一致。</param>
    /// <param name="purity">液体容器附带水质纯度。</param>
    internal static List<TooltipLine> Collect(GameItem? item, bool listing, bool adapters,
                                              bool hiddenTruth, bool purity)
    {
        var lines = new List<TooltipLine>();
        if (item == null || (!listing && !adapters)) return lines;

        string key = KindKey(item);
        if (Failures.TryGetValue(key, out int failed) && failed >= FailureLimit) return lines;

        try
        {
            if (LockedContainer.IsLocked(item)) return lines;

            int flags = (listing ? 1 : 0) | (adapters ? 2 : 0) | (hiddenTruth ? 4 : 0) | (purity ? 8 : 0);
            string locale = Loc.Code;
            float now = UnityEngine.Time.unscaledTime;

            var entry = Find(item, locale, flags);
            if (entry != null && now - entry.CheckedAt <= RecheckSeconds && now - entry.At <= CacheSeconds)
            {
                lines.AddRange(entry.Lines);
                return lines;
            }

            var ctx = Read(item);
            if (ctx == null) return lines;
            if (!ctx.IsMachine && !listing) return lines;

            var all = AllItems(ctx);
            if (entry != null && now - entry.At <= CacheSeconds
                && ContainerItemSignature.Matches(entry.Items, all))
            {
                entry.CheckedAt = now;
                lines.AddRange(entry.Lines);
                return lines;
            }

            var built = new List<TooltipLine>();
            bool clean = true;
            if (adapters && ctx.IsMachine) clean &= Stage(key, "Adapters", () => MachineAdapters.Run(ctx, built));
            if (listing)
            {
                clean &= Stage(key, "Filters", () => SummarizeFilters(ctx, built, hiddenTruth));
                clean &= Stage(key, "Liquids", () => SummarizeLiquids(ctx, built, purity));
                clean &= Stage(key, "Tools", () => SummarizeTools(ctx, built));
                clean &= Stage(key, "Batteries", () => SummarizeBatteries(ctx, built));
                clean &= Stage(key, "List", () => ListRemaining(ctx, built));
            }

            Store(item, locale, flags, now, all, built, entry);
            lines.AddRange(built);
            // 只有整次构建没有任何一步失败才清零：否则某步每次都坏、其它步照常输出，
            // 失败计数会被反复清掉，永远停不下来。
            if (clean)
            {
                Failures.Remove(key);
                if (lines.Count > 0) Log.MarkOk("MachineInterior");
            }
        }
        catch (Exception ex)
        {
            Fail(key, "Collect", ex);
        }
        return lines;
    }

    // ---------------- 第一层：结构读取 ----------------

    /// <summary>读出全部面板；不是机器、也不是槽位容器时返回 null。</summary>
    private static Context? Read(GameItem item)
    {
        var root = item.contentWindow?.childElement;
        if (root == null) return null;

        bool isMachineType = false;
        try { isMachineType = item.IsGameItemType("MACHINE"); } catch { }

        // 扩建后的储藏区也是 MachineryPanelRow（每个标签页一个网格），但它是储物容器：
        // 非机器且库存全是网格的归 StorageInfo，按所有标签页合计，这里不再重复列出。
        if (root is MachineryPanelRow && !isMachineType && StorageInfo.GridStorage(item) != null) return null;

        var ctx = new Context { Machine = item };
        if (root is MachineryPanelRow row)
        {
            ctx.IsMachine = true;
            var panels = row.Panels;
            if (panels == null) return null;

            foreach (var mp in panels)
            {
                if (mp == null) continue;
                if (mp.isHidden) continue;

                string role = mp.role ?? "";
                var content = mp.content;

                if (role == "power")
                {
                    ctx.HasPowerPanel = true;
                    var power = new List<GameItem>();
                    ReadItems(content, power);
                    if (ctx.PowerSource == null && power.Count > 0) ctx.PowerSource = power[0];
                    continue;
                }
                if (role == "module")
                {
                    ReadItems(content, ctx.Modules);
                    continue;
                }
                if (SkippedRoles.Contains(role)) continue;

                var panel = new Panel { Role = role };
                try { panel.Label = CleanLabel(mp.label?.GetText()); } catch { }
                ReadItems(content, panel.Items);
                ctx.Panels.Add(panel);
            }
            return ctx;
        }

        // 网格容器归 StorageInfo；只有它跳过的 MACHINE 类型网格才由这里通用列出。
        if (root is GameGridInventory && !isMachineType) return null;

        var single = new Panel();
        ReadItems(root, single.Items);
        ctx.Panels.Add(single);
        return ctx;
    }

    private static void ReadItems(PixelElement? content, List<GameItem> into)
    {
        if (content == null) return;
        List<GraphNodeStorage>? storages = null;
        try { storages = MachineryContainerUtils.Collect(content); } catch { }
        if (storages == null) return;

        int start = into.Count;
        foreach (var storage in storages)
        {
            if (storage is not GameInventory inv) continue;
            var children = inv.childItems;
            if (children == null) continue;
            foreach (var child in children)
            {
                if (child == null) continue;
                if (into.Count - start >= MaxItemsPerPanel) return;
                into.Add(child);
            }
        }
    }

    /// <summary>标题去掉结尾冒号，拼接时统一由 inner.line 加。</summary>
    private static string CleanLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        return label!.Trim().TrimEnd(':', '：').Trim();
    }

    private static List<GameItem> AllItems(Context ctx)
    {
        var all = new List<GameItem>();
        if (ctx.PowerSource != null) all.Add(ctx.PowerSource);
        all.AddRange(ctx.Modules);
        foreach (var p in ctx.Panels) all.AddRange(p.Items);
        return all;
    }

    /// <summary>未被认领面板里、还没被汇总过的物品。</summary>
    private static IEnumerable<GameItem> Open(Context ctx)
    {
        foreach (var p in ctx.Panels)
        {
            if (p.Claimed) continue;
            foreach (var it in p.Items)
                if (!ctx.Summarized.Contains(it)) yield return it;
        }
    }

    // ---------------- 第二层：按标签的内容渲染 ----------------

    /// <summary>
    /// 滤芯：真实耐久为 0 时游戏完全不过滤（WaterHelper.HandleFilter），玩家只看机器看不出来。
    /// 透视关闭且未揭露时显示伪装值，与滤芯自身 tooltip 一致。
    /// </summary>
    private static void SummarizeFilters(Context ctx, List<TooltipLine> lines, bool hiddenTruth)
    {
        var filters = new List<(GameItem Item, int Cur, int Max, int Fake)>();
        foreach (var it in Open(ctx))
        {
            if (!GameRead.IsTag(it, "WATER_FILTER") || !GameRead.IsTag(it, "DURABILITY_TAG")) continue;
            int max = GameRead.TagInt(it, "MAX_DURABILITY_TAG") ?? 0;
            if (max <= 0) continue;
            int real = GameRead.TagInt(it, "DURABILITY_TAG") ?? 0;
            int fake = GameRead.TagInt(it, "DURABILITY_FAKE_TAG") ?? real;
            int shown = hiddenTruth || IsDiscovered(it, "DURABILITY_TAG") ? real : fake;
            filters.Add((it, shown, max, fake));
        }
        if (filters.Count == 0) return;
        foreach (var f in filters) ctx.Summarized.Add(f.Item);

        if (filters.Count == 1)
        {
            var f = filters[0];
            string text = Loc.T("inner.durability", "{0}: durability {1}/{2}",
                ContainerItemSignature.DisplayName(f.Item), f.Cur, f.Max);
            if (hiddenTruth && f.Fake != f.Cur && !IsDiscovered(f.Item, "DURABILITY_TAG"))
                text += Loc.T("inner.faked", " (labelled {0})", f.Fake);
            if (f.Cur <= 0) text += Loc.T("inner.filter_spent", " (spent, no longer filters)");
            lines.Add(new TooltipLine(text, Level(f.Cur, f.Max)));
            return;
        }

        int spent = 0;
        var lowest = filters[0];
        foreach (var f in filters)
        {
            if (f.Cur <= 0) spent++;
            if ((long)f.Cur * lowest.Max < (long)lowest.Cur * f.Max) lowest = f;
        }
        string summary = Loc.T("inner.filters", "Filters ×{0}: lowest durability {1}/{2}",
            filters.Count, lowest.Cur, lowest.Max);
        if (spent > 0) summary += Loc.T("inner.spent_count", ", {0} spent", spent);
        lines.Add(new TooltipLine(summary, spent > 0 ? Bad : Level(lowest.Cur, lowest.Max)));
    }

    /// <summary>液体容器：容量，按开关附带水质。超出上限的留给通用列表。</summary>
    private static void SummarizeLiquids(Context ctx, List<TooltipLine> lines, bool purity)
    {
        int shown = 0;
        foreach (var it in new List<GameItem>(Open(ctx)))
        {
            if (shown >= MaxLiquidLines) break;
            if (!GameRead.IsTag(it, "LIQUID_CONTAINER_TAG")) continue;

            int cur = WaterHelper.GetCurrentCapacityML(it);
            int max = WaterHelper.GetTotalCapacityML(it);
            string state;
            if (cur <= 0)
            {
                state = Loc.T("inner.liquid_empty", "empty");
            }
            else
            {
                bool liters = Volume.UseLiters(max);
                state = Loc.T("inner.liquid", "{0}/{1}{2}",
                    Volume.Number(cur, liters, Loc.Culture), Volume.Number(max, liters, Loc.Culture), WaterInfo.VolumeUnit(liters));
                if (purity)
                {
                    var r = WaterInfo.Analyze(it);
                    if (r.HasData && r.WaterParts > 0)
                        state += Loc.T("inner.liquid_purity", ", {0}% {1}",
                            (r.Purity / 100.0).ToString("0.00", Loc.Culture), r.Tier);
                }
            }

            ctx.Summarized.Add(it);
            lines.Add(new TooltipLine(
                Loc.T("inner.line", "{0}: {1}", ContainerItemSignature.DisplayName(it), state),
                Info));
            shown++;
        }
    }

    /// <summary>计次工具：一件时写剩余次数，多件时写最少的那件和已用完几件。</summary>
    private static void SummarizeTools(Context ctx, List<TooltipLine> lines)
    {
        var tools = new List<(GameItem Item, int Cur, int Max)>();
        foreach (var it in Open(ctx))
        {
            if (!GameRead.IsTag(it, "USE_COUNT_ITEM_TAG")) continue;
            int cur = GameRead.TagInt(it, "CURRENT_USE_COUNT_INT") ?? 0;
            int max = GameRead.TagInt(it, "MAX_USE_COUNT_INT") ?? 0;
            if (max <= 0) max = 1;   // 与 UseCountHelper.CreateUseCountItemTooltip 相同的兜底
            tools.Add((it, cur, max));
        }
        if (tools.Count == 0) return;
        foreach (var t in tools) ctx.Summarized.Add(t.Item);

        if (tools.Count == 1)
        {
            var t = tools[0];
            lines.Add(new TooltipLine(
                Loc.T("inner.uses", "{0}: uses remaining {1}/{2}",
                    ContainerItemSignature.DisplayName(t.Item), t.Cur, t.Max),
                Level(t.Cur, t.Max)));
            return;
        }

        int spent = 0;
        var fewest = tools[0];
        foreach (var t in tools)
        {
            if (t.Cur <= 0) spent++;
            if (t.Cur < fewest.Cur) fewest = t;
        }
        string text = Loc.T("inner.tools", "Tools ×{0}, fewest uses: {1} ({2}/{3})",
            tools.Count, ContainerItemSignature.DisplayName(fewest.Item), fewest.Cur, fewest.Max);
        if (spent > 0) text += Loc.T("inner.spent_count", ", {0} spent", spent);
        lines.Add(new TooltipLine(text, spent > 0 ? Bad : Level(fewest.Cur, fewest.Max)));
    }

    /// <summary>
    /// 不在电源面板里的电池（充电类机器的输入/输出槽）。逐节写出电量，不设行数上限：
    /// 面板自己的格数就是天然上限，把剩下的电量折进「能量电池×N」只会丢信息。
    /// </summary>
    private static void SummarizeBatteries(Context ctx, List<TooltipLine> lines)
    {
        foreach (var it in new List<GameItem>(Open(ctx)))
        {
            if (!IsPlainBattery(it)) continue;
            int cur = GameRead.TagInt(it, "power_source_item_energy") ?? 0;
            int max = GameRead.TagInt(it, "power_source_item_max_energy") ?? 0;
            if (max <= 0) continue;

            ctx.Summarized.Add(it);
            lines.Add(new TooltipLine(
                Loc.T("inner.energy", "{0}: energy {1}/{2}",
                    ContainerItemSignature.DisplayName(it), cur, max),
                Level(cur, max)));
        }
    }

    // ---------------- 通用列表 ----------------

    /// <summary>
    /// 剩余物品按面板列出：非空面板不多且都有标题时，每个面板一行「标题：A×2, B」；
    /// 否则合并成一行「内含：…」。
    /// </summary>
    private static void ListRemaining(Context ctx, List<TooltipLine> lines)
    {
        var groups = new List<(string Label, List<GameItem> Items)>();
        bool allLabelled = true;
        foreach (var p in ctx.Panels)
        {
            if (p.Claimed) continue;
            var items = new List<GameItem>();
            foreach (var it in p.Items)
                if (!ctx.Summarized.Contains(it)) items.Add(it);
            if (items.Count == 0) continue;
            if (p.Label.Length == 0) allLabelled = false;
            groups.Add((p.Label, items));
        }
        if (groups.Count == 0) return;

        if (allLabelled && groups.Count <= MaxPanelLines)
        {
            foreach (var g in groups)
                lines.Add(new TooltipLine(Loc.T("inner.line", "{0}: {1}", g.Label, Names(g.Items)), Info));
            return;
        }

        var merged = new List<GameItem>();
        foreach (var g in groups) merged.AddRange(g.Items);
        lines.Add(new TooltipLine(Loc.T("storage.contains", "Contains: ") + Names(merged), Info));
    }

    /// <summary>名称×数量，按首次出现顺序，超出种类上限折叠。</summary>
    private static string Names(List<GameItem> items)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var it in items)
        {
            string name = ContainerItemSignature.DisplayName(it);
            int stack = ContainerItemSignature.ReadStack(it);
            if (counts.TryGetValue(name, out int c)) counts[name] = c + stack;
            else { counts[name] = stack; order.Add(name); }
        }

        var sb = new StringBuilder();
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
        return sb.ToString();
    }

    // ---------------- 共用判定 ----------------

    /// <summary>
    /// 普通电池：带 power_source_item 标签，且不是电流转换器或超科技电池。
    /// 后两者也走 InitPowerSourceItem(item, 0, 0)，能量标签恒为 0，按普通电池读会误报"没电"。
    /// </summary>
    internal static bool IsPlainBattery(GameItem? item)
    {
        if (item == null || !GameRead.IsTag(item, "power_source_item")) return false;
        try
        {
            return !CurrentConverterHelper.IsCurrentConverter(item)
                && !HypertechBatteryHelper.IsHypertechBattery(item);
        }
        catch { return false; }
    }

    /// <summary>
    /// 机器的电源物品：先找 role 为 power 的面板；没有这种面板时才退回原版的第 0 个槽
    /// （MachineHelper.GetBatterySlot），且槽里的物品必须带 power_source_item 标签。
    /// 有电源面板但空着时返回 null，不再退回第 0 个槽。
    /// </summary>
    internal static GameItem? FindPowerSource(GameItem? machine)
    {
        if (machine == null) return null;
        var root = machine.contentWindow?.childElement;
        if (root is MachineryPanelRow row)
        {
            try
            {
                foreach (var mp in row.Panels)
                {
                    if (mp == null || mp.role != "power") continue;
                    var items = new List<GameItem>();
                    ReadItems(mp.content, items);
                    return items.Count > 0 ? items[0] : null;
                }
            }
            catch { return null; }
        }

        GameItem? first = null;
        try { first = MachineHelper.GetBatterySlot(machine)?.childItem; } catch { }
        return GameRead.IsTag(first, "power_source_item") ? first : null;
    }

    private static bool IsDiscovered(GameItem item, string featureId)
    {
        try
        {
            var feature = item.FindItemFeatureByID(featureId);
            return feature != null && feature.IsFeatureDiscovered();
        }
        catch { return false; }
    }

    internal static RenderHandler.ColorPalette Level(int cur, int max)
    {
        if (cur <= 0) return Bad;
        if (max > 0 && cur * 4 < max) return Warn;
        return Info;
    }

    // ---------------- 隔离与缓存 ----------------

    private static string KindKey(GameItem item)
    {
        string id = item.identifier ?? "";
        if (id.Length == 0) id = "name:" + ContainerItemSignature.DisplayName(item);
        return id;
    }

    /// <summary>每一步单独兜底：某步失败只丢这一步的行，并计入这种机器的失败次数。</summary>
    private static bool Stage(string key, string stage, Action action)
    {
        try { action(); return true; }
        catch (Exception ex) { Fail(key, stage, ex); return false; }
    }

    private static void Fail(string key, string stage, Exception ex)
    {
        Failures.TryGetValue(key, out int n);
        Failures[key] = ++n;
        Log.Warn("MachineInterior." + stage, ex);
        if (n == FailureLimit)
            Log.Warn("MachineInterior", $"「{key}」连续失败 {n} 次，本次会话不再显示它的内部摘要");
    }

    private static Entry? Find(GameItem item, string locale, int flags)
    {
        foreach (var e in Entries)
            if (ReferenceEquals(e.Item, item) && e.Locale == locale && e.Flags == flags) return e;
        return null;
    }

    private static void Store(GameItem item, string locale, int flags, float now,
                              List<GameItem> all, List<TooltipLine> lines, Entry? entry)
    {
        if (entry == null)
        {
            entry = new Entry { Item = item, Locale = locale, Flags = flags };
            Entries.Add(entry);
        }
        else
        {
            Entries.Remove(entry);
            Entries.Add(entry);
        }
        entry.At = now;
        entry.CheckedAt = now;
        entry.Items = ContainerItemSignature.Capture(all);
        entry.Lines = lines;
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }
}
