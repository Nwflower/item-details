using System;
using System.Collections.Generic;
using UnityEngine;
using ItemDetails.Integration;

namespace ItemDetails;

/// <summary>显式第三方提供方。字典只由游戏主线程访问，生命周期由注册句柄对称管理。</summary>
internal static class ThirdPartyProviders
{
    private static readonly Dictionary<string, ItemDetailsAppraisalProvider> Appraisals = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ItemDetailsMachineEffectProvider> Machines = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, TooltipEntry> TooltipDisplays = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PurifierEntry> Purifiers = new(StringComparer.Ordinal);
    private static Func<RectTransform?>? TooltipAnchorProvider;
    private static readonly List<ItemTooltipEntry> ItemTooltipDisplays = new();

    internal static IDisposable RegisterAppraisal(string id, ItemDetailsAppraisalProvider provider)
    {
        id = Required(id, nameof(id));
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        if (Appraisals.ContainsKey(id)) throw new InvalidOperationException($"Appraisal provider already registered: {id}");
        Appraisals.Add(id, provider);
        return new Subscription(() => Remove(Appraisals, id, provider));
    }

    internal static IDisposable RegisterMachine(string id, ItemDetailsMachineEffectProvider provider)
    {
        id = Required(id, nameof(id));
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        if (Machines.ContainsKey(id)) throw new InvalidOperationException($"Machine effect provider already registered: {id}");
        Machines.Add(id, provider);
        return new Subscription(() => Remove(Machines, id, provider));
    }

    internal static IDisposable RegisterTooltip(string id, ItemDetailsTooltipSection sections)
    {
        id = Required(id, nameof(id));
        if (sections == ItemDetailsTooltipSection.None)
            throw new ArgumentException("At least one tooltip section is required.", nameof(sections));
        if ((sections & ~ItemDetailsTooltipSection.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(sections));
        if (TooltipDisplays.ContainsKey(id))
            throw new InvalidOperationException($"Tooltip display already registered: {id}");
        var entry = new TooltipEntry(sections);
        TooltipDisplays.Add(id, entry);
        return new Subscription(() => Remove(TooltipDisplays, id, entry));
    }

    internal static IDisposable RegisterTooltipAnchor(Func<RectTransform?> provider)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        if (TooltipAnchorProvider != null)
            throw new InvalidOperationException("A tooltip anchor provider is already registered.");
        TooltipAnchorProvider = provider;
        return new Subscription(() =>
        {
            if (ReferenceEquals(TooltipAnchorProvider, provider)) TooltipAnchorProvider = null;
        });
    }

    internal static bool TryTooltipAnchor(out RectTransform? anchor)
    {
        anchor = null;
        var provider = TooltipAnchorProvider;
        if (provider != null)
        {
            try { anchor = provider(); }
            catch { return false; }
            return anchor != null;
        }

        if (AppDomain.CurrentDomain.GetData(ItemDetailsClient.AnchorSlot) is Func<RectTransform?> anchors)
        {
            foreach (Func<RectTransform?> getAnchor in anchors.GetInvocationList())
            {
                try { anchor = getAnchor(); }
                catch { continue; }
                if (anchor != null) return true;
            }
        }
        return false;
    }

    internal static IDisposable RegisterTooltip(Func<GameItem, ItemDetailsTooltipSection> sections)
    {
        if (sections == null) throw new ArgumentNullException(nameof(sections));
        var entry = new ItemTooltipEntry(sections);
        ItemTooltipDisplays.Add(entry);
        return new Subscription(() => ItemTooltipDisplays.Remove(entry));
    }

    /// <summary>逐件注册的接管合在一起；回调抛错的那一个这次算不接管。</summary>
    private static ItemDetailsTooltipSection ItemSuppressions(GameItem item)
    {
        var sections = ItemDetailsTooltipSection.None;
        foreach (var entry in ItemTooltipDisplays)
        {
            try { sections |= entry.Sections(item); }
            catch { }
        }
        return sections & ItemDetailsTooltipSection.All;
    }

    internal static IDisposable RegisterPurifier(string id, ItemDetailsPurifierKind kind)
    {
        id = Required(id, nameof(id));
        var mapped = kind switch
        {
            ItemDetailsPurifierKind.Machine => PurifySources.Kind.Machine,
            ItemDetailsPurifierKind.Portable => PurifySources.Kind.Portable,
            ItemDetailsPurifierKind.ChemicalAgent => PurifySources.Kind.Chem,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        if (Purifiers.ContainsKey(id)) throw new InvalidOperationException($"Water purifier already registered: {id}");
        var entry = new PurifierEntry(mapped);
        Purifiers.Add(id, entry);
        PurifySources.Invalidate();
        return new Subscription(() =>
        {
            Remove(Purifiers, id, entry);
            PurifySources.Invalidate();
        });
    }

    /// <summary>该物品被第三方接管的信息块；没有注册或读取失败时不隐藏任何信息。</summary>
    internal static ItemDetailsTooltipSection TooltipSuppressions(GameItem item)
    {
        var byItem = ItemSuppressions(item);
        if (SectionedLayout.DrawsContents(item)) byItem |= ItemDetailsTooltipSection.Contents;
        if (TooltipDisplays.Count == 0) return byItem;
        try
        {
            // 与 ModSourceHelper.GetItemModName 同一来源顺序：显式标签优先，其次物品工厂所属 Mod。
            string? id = GameRead.TagString(item, "MOD_ID_TAG");
            if (string.IsNullOrEmpty(id))
            {
                var factory = DirectoryMaster.GetFactory<GameItem>(item.identifier);
                id = ModSourceHelper.FindModByDelegate(factory)?.Manifest?.ID;
            }
            return byItem | (!string.IsNullOrEmpty(id) && TooltipDisplays.TryGetValue(id, out var entry)
                ? entry.Sections : ItemDetailsTooltipSection.None);
        }
        catch { return byItem; }
    }

    internal static bool HandlesTooltip(GameItem item, ItemDetailsTooltipSection section)
        => (TooltipSuppressions(item) & section) != 0;

    internal static bool TryPurifier(string id, out PurifySources.Kind kind)
    {
        if (Purifiers.TryGetValue(id, out var entry))
        {
            kind = entry.Kind;
            return true;
        }
        kind = default;
        return false;
    }

    /// <summary>该物品是否已有显式鉴定提供方；提供方接管后不重复走原版精选分解。</summary>
    internal static bool HasAppraisal(GameItem item)
    {
        try
        {
            var type = AppraisalItemRegistry.Find(item);
            return type?.id != null && Appraisals.ContainsKey(type.id);
        }
        catch { return false; }
    }

    internal static ItemDetailsBreakdown? Appraisal(GameItem item)
    {
        AppraisalItemType? type;
        try { type = AppraisalItemRegistry.Find(item); }
        catch (Exception ex) { Log.Warn("Provider.AppraisalFind", ex); return null; }
        if (type?.id == null || !Appraisals.TryGetValue(type.id, out var provider)) return null;

        try
        {
            var result = provider(item);
            if (result == null || result.Total <= 0) return null;
            int native = GameRead.TrueValue(item) ?? 0;
            if (native > 0 && result.Total != native)
            {
                Log.Warn("Provider.AppraisalTotal", $"{type.id}: provider={result.Total}, game={native}");
                return null;
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn("Provider.Appraisal." + type.id, ex);
            return null;
        }
    }

    /// <summary>返回 true 表示该 ID 有显式提供方，即使它本次选择不写注释。</summary>
    internal static bool TryMachineNote(GameItem machine, char stat, out string note)
    {
        note = "";
        string id = machine.identifier ?? "";
        if (id.Length == 0 || !Machines.TryGetValue(id, out var provider)) return false;

        var kind = stat switch
        {
            ModuleStatEffects.Performance => ItemDetailsModuleStat.Performance,
            ModuleStatEffects.Efficiency => ItemDetailsModuleStat.Efficiency,
            ModuleStatEffects.Quality => ItemDetailsModuleStat.Quality,
            _ => (ItemDetailsModuleStat)(-1),
        };
        if ((int)kind < 0) return true;
        try { note = provider(machine, kind) ?? ""; }
        catch (Exception ex) { Log.Warn("Provider.Machine." + id, ex); }
        return true;
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-empty id is required.", name);
        return value!.Trim();
    }

    private static void Remove<T>(Dictionary<string, T> registry, string id, T provider) where T : class
    {
        if (registry.TryGetValue(id, out var current) && ReferenceEquals(current, provider)) registry.Remove(id);
    }

    private sealed class TooltipEntry
    {
        internal readonly ItemDetailsTooltipSection Sections;
        internal TooltipEntry(ItemDetailsTooltipSection sections) => Sections = sections;
    }

    private sealed class ItemTooltipEntry
    {
        internal readonly Func<GameItem, ItemDetailsTooltipSection> Sections;
        internal ItemTooltipEntry(Func<GameItem, ItemDetailsTooltipSection> sections) => Sections = sections;
    }

    private sealed class PurifierEntry
    {
        internal readonly PurifySources.Kind Kind;
        internal PurifierEntry(PurifySources.Kind kind) => Kind = kind;
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;
        internal Subscription(Action dispose) => _dispose = dispose;
        public void Dispose() => System.Threading.Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
