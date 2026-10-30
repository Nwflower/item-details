using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>显式第三方提供方。字典只由游戏主线程访问，生命周期由注册句柄对称管理。</summary>
internal static class ThirdPartyProviders
{
    private static readonly Dictionary<string, ItemDetailsAppraisalProvider> Appraisals = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ItemDetailsMachineEffectProvider> Machines = new(StringComparer.Ordinal);

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
        string id = "";
        try { id = machine.identifier ?? ""; } catch { }
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

    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;
        internal Subscription(Action dispose) => _dispose = dispose;
        public void Dispose() => System.Threading.Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
