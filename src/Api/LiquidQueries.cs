using System;
using System.Collections.Generic;
using ItemDetails.Integration;
using Advice = System.Collections.Generic.IReadOnlyList<(string Name, double GainPercentagePoints)>;

namespace ItemDetails;

internal static class LiquidQueries
{
    // The inferred delegate contains game/BCL types only. The SDK compiled in another mod binds the same signature.
    private static readonly Delegate AnalysisQuery = (GameItem item) => Analyze(item)?.Data;
    private static readonly Func<GameItem, Advice?> AdviceQuery = GetAdvice;
    private static LiquidAnalysis? currentAnalysis;
    private static Advice? currentAdvice;
    private static bool analyzed, simulated;

    internal static void Enable()
    {
        var domain = AppDomain.CurrentDomain;
        domain.SetData(ItemDetailsClient.AnalysisSlot, AnalysisQuery);
        domain.SetData(ItemDetailsClient.AdviceSlot, AdviceQuery);
    }

    internal static void Disable()
    {
        var domain = AppDomain.CurrentDomain;
        if (ReferenceEquals(domain.GetData(ItemDetailsClient.AnalysisSlot), AnalysisQuery))
            domain.SetData(ItemDetailsClient.AnalysisSlot, null);
        if (ReferenceEquals(domain.GetData(ItemDetailsClient.AdviceSlot), AdviceQuery))
            domain.SetData(ItemDetailsClient.AdviceSlot, null);
        EndTooltip();
    }

    internal static void EndTooltip()
    {
        currentAnalysis = null;
        currentAdvice = null;
        analyzed = simulated = false;
    }

    internal static LiquidAnalysis? Analyze(GameItem item)
    {
        if (item == null || !LiquidGate.Allows(item)) return null;
        bool cache = ReferenceEquals(item, TooltipPipeline.Current);
        if (cache && analyzed) return currentAnalysis;
        var raw = WaterInfo.Analyze(item);
        var result = raw.HasData ? Snapshot(raw, item) : null;
        if (cache)
        {
            analyzed = true;
            currentAnalysis = result;
        }
        return result;
    }

    internal static LiquidAnalysis Snapshot(WaterInfo.Result raw, GameItem item)
    {
        var components = new List<(string Id, string Name, int Microliters, string Role)>();
        if (raw.WaterParts > 0)
            components.Add(("water", raw.WaterName.Length > 0 ? raw.WaterName : "?", raw.WaterParts, "water"));
        foreach (var active in raw.Actives)
            components.Add((active.Id, active.Name, active.Parts, "active"));

        int hidden = 0;
        foreach (var part in raw.Impurities)
        {
            if (part.Parts < WaterInfo.MinVisibleParts || part.Parts * 100.0 / raw.TotalParts < 0.005)
                hidden++;
            else
                components.Add((part.Id, part.Name, part.Parts, "other"));
        }
        components.Sort((a, b) => b.Microliters.CompareTo(a.Microliters));
        int capacity = raw.Solid ? 0 : Math.Max(0, GameRead.TagInt(item, "LIQUID_CONTAINER_CAPACITY") ?? 0);
        return new LiquidAnalysis((raw.TotalParts, capacity, raw.Solid, raw.Tier, raw.Label, hidden,
            components.AsReadOnly(), (raw.LethalDose, raw.Toxic, raw.Euphoria, raw.Synaprest, raw.Contraceptive, DescribeEffects(raw))));
    }

    /// <summary>「致死（2 倍致死量）、强烈欣快、戒瘾」</summary>
    private static string DescribeEffects(WaterInfo.Result r)
    {
        if (!r.HasEffects) return "";
        var parts = new List<string>();
        if (r.LethalDose > 0) parts.Add(Loc.T("chem.lethal", "lethal ({0}x lethal dose)", r.LethalDose));
        else if (r.Toxic) parts.Add(Loc.T("chem.toxic", "toxic"));
        switch (r.Euphoria)
        {
            case 1: parts.Add(Loc.T("chem.euphoria_weak", "weak euphoria")); break;
            case 2: parts.Add(Loc.T("chem.euphoria_mild", "mild euphoria")); break;
            case 3: parts.Add(Loc.T("chem.euphoria_strong", "strong euphoria")); break;
            case 4: parts.Add(Loc.T("chem.euphoria_extreme", "extreme euphoria")); break;
        }
        if (r.Synaprest) parts.Add(Loc.T("chem.synaprest", "anti-addiction"));
        if (r.Contraceptive) parts.Add(Loc.T("chem.contraceptive", "contraceptive"));
        return string.Join(Loc.T("list.sep", ", "), parts);
    }

    internal static Advice? GetAdvice(GameItem item)
    {
        if (item == null || !LiquidGate.Allows(item)) return null;
        bool cache = ReferenceEquals(item, TooltipPipeline.Current);
        if (cache && simulated) return currentAdvice;
        var methods = WaterAdvice.SimulateMethods(item, out bool available);
        Advice? result = available ? methods.ConvertAll(m => (m.Name, m.GainPct)).AsReadOnly() : null;
        if (cache)
        {
            simulated = true;
            currentAdvice = result;
        }
        return result;
    }

    internal static bool TryDisplay<T>(string slot, GameItem item, T data)
    {
        if (AppDomain.CurrentDomain.GetData(slot) is not Func<GameItem, T, bool> callbacks) return false;
        foreach (Func<GameItem, T, bool> prepare in callbacks.GetInvocationList())
        {
            try { if (prepare(item, data)) return true; }
            catch (Exception ex) { Log.Warn("LiquidDisplay." + slot, ex); }
        }
        return false;
    }
}
