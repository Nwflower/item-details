#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using Runs = System.Collections.Generic.IReadOnlyList<(string Kind, string Text, RenderHandler.ColorPalette Color, float Ratio, int Length, RenderHandler.ColorPalette Background, string Tail)>;
using Fields = System.Collections.Generic.IReadOnlyDictionary<string, object>;
using Advice = System.Collections.Generic.IReadOnlyList<(string Name, double GainPercentagePoints)>;
using LiquidPayload = (int TotalMicroliters, int CapacityMicroliters, bool IsSolid, string Tier, string Label,
    int HiddenComponents, System.Collections.Generic.IReadOnlyList<(string Id, string Name, int Microliters, string Role)> Components,
    (int LethalDose, bool Toxic, int Euphoria, bool Synaprest, bool Contraceptive, string Text) Effects);

namespace ItemDetails.Integration;

/// <summary>Copy this source into the consuming mod. All calls and registrations run on the game thread.</summary>
internal static class ItemDetailsClient
{
    internal const string SectionDisplay = "item_details.tooltip_sections.v1.display";
    internal const string SectionQuery = "item_details.tooltip_sections.v1.read";
    internal const string SectionWatch = "item_details.tooltip_sections.v1.watch";

    // Register once on enable. Returning false leaves this section in the original tooltip.
    internal static IDisposable RegisterTooltipSections(Func<GameItem, RichTextBuilder, TooltipSection, bool> prepare)
    {
        if (prepare == null) throw new ArgumentNullException(nameof(prepare));
        Func<GameItem, RichTextBuilder, string, Runs, Fields, bool> callback =
            (item, builder, id, runs, fields) => prepare(item, builder, new TooltipSection(id, runs, fields));
        return new Registration(SectionDisplay, callback);
    }

    // Enable capture before building tooltips; query the exact builder after GetTooltipBasic returns.
    internal static IDisposable WatchTooltipSections() => new Registration(SectionWatch, (Action)(() => { }));

    internal static bool TryGetTooltipSections(RichTextBuilder builder, out IReadOnlyList<TooltipSection> sections)
    {
        sections = Array.Empty<TooltipSection>();
        if (AppDomain.CurrentDomain.GetData(SectionQuery) is not Func<RichTextBuilder, Action<string, Runs, Fields>, bool> read)
            return false;
        var result = new List<TooltipSection>();
        try
        {
            if (!read(builder, (id, runs, fields) => result.Add(new TooltipSection(id, runs, fields)))) return false;
            sections = result.AsReadOnly();
            return true;
        }
        catch { return false; }
    }

    internal const string LayoutSlot = "item_details.tooltip_layout.v1";
    internal const string AnchorSlot = "item_details.tooltip_anchor.v1";

    // null means this renderer does not own the current tooltip.
    internal static IDisposable RegisterTooltipLayout(Func<GameItem, TooltipLayoutOptions?> describe)
    {
        if (describe == null) throw new ArgumentNullException(nameof(describe));
        Func<GameItem, int?> callback = item => (int?)describe(item);
        return new Registration(LayoutSlot, callback);
    }

    internal static IDisposable RegisterTooltipAnchor(Func<RectTransform?> anchor)
        => new Registration(AnchorSlot, anchor ?? throw new ArgumentNullException(nameof(anchor)));

    internal const string AnalysisSlot = "item_details.liquid_analysis.v1";
    internal const string AdviceSlot = "item_details.water_advice.v1";
    internal const string AnalysisDisplay = AnalysisSlot + ".display";
    internal const string AdviceDisplay = AdviceSlot + ".display";

    internal static bool TryGetLiquidAnalysis(GameItem item, out LiquidAnalysis analysis)
    {
        analysis = null!;
        try
        {
            var query = AppDomain.CurrentDomain.GetData(AnalysisSlot) as Func<GameItem, LiquidPayload?>;
            if (query?.Invoke(item) is not LiquidPayload data) return false;
            analysis = new LiquidAnalysis(data);
            return true;
        }
        catch { return false; }
    }

    internal static bool TryGetWaterAdvice(GameItem item, out Advice advice)
    {
        advice = Array.Empty<(string, double)>();
        try
        {
            var query = AppDomain.CurrentDomain.GetData(AdviceSlot) as Func<GameItem, Advice?>;
            if (query?.Invoke(item) is not Advice data) return false;
            advice = data;
            return true;
        }
        catch { return false; }
    }

    // Return true only once the replacement for this build is ready. Dispose on OnDisable.
    internal static IDisposable RegisterLiquidAnalysisDisplay(Func<GameItem, LiquidAnalysis, bool> prepare)
    {
        if (prepare == null) throw new ArgumentNullException(nameof(prepare));
        Func<GameItem, LiquidPayload, bool> callback = (item, data) => prepare(item, new LiquidAnalysis(data));
        return new Registration(AnalysisDisplay, callback);
    }

    internal static IDisposable RegisterWaterAdviceDisplay(Func<GameItem, Advice, bool> prepare)
        => new Registration(AdviceDisplay, prepare ?? throw new ArgumentNullException(nameof(prepare)));

    private sealed class Registration : IDisposable
    {
        private readonly string slot;
        private Delegate? callback;

        internal Registration(string slot, Delegate callback)
        {
            this.slot = slot;
            this.callback = callback;
            var domain = AppDomain.CurrentDomain;
            domain.SetData(slot, Delegate.Combine(domain.GetData(slot) as Delegate, callback));
        }

        public void Dispose()
        {
            if (callback == null) return;
            var domain = AppDomain.CurrentDomain;
            domain.SetData(slot, Delegate.Remove(domain.GetData(slot) as Delegate, callback));
            callback = null;
        }
    }
}

[Flags]
internal enum TooltipLayoutOptions
{
    None = 0,
    NativeProgressBars = 1,
    FlowingText = 2,
    DrawsContents = 4,
}

internal sealed class LiquidAnalysis
{
    internal LiquidPayload Data { get; }
    internal LiquidAnalysis(LiquidPayload data) => Data = data;
    internal int TotalMicroliters => Data.TotalMicroliters;
    internal int CapacityMicroliters => Data.CapacityMicroliters;
    internal bool IsSolid => Data.IsSolid;
    internal string Tier => Data.Tier;
    internal string Label => Data.Label;
    internal bool LabelMismatch => Tier.Length > 0 && Label.Length > 0 && Tier != Label;
    internal int HiddenComponents => Data.HiddenComponents;
    internal IReadOnlyList<(string Id, string Name, int Microliters, string Role)> Components => Data.Components;
    internal (int LethalDose, bool Toxic, int Euphoria, bool Synaprest, bool Contraceptive, string Text) Effects => Data.Effects;
}


internal sealed class TooltipSection
{
    internal string Id { get; }
    internal Runs Runs { get; }
    internal Fields Fields { get; }
    internal TooltipSection(string id, Runs runs, Fields fields)
    {
        Id = id;
        Runs = runs;
        Fields = fields;
    }

    internal bool CanReplace => !TryGet<bool>("canReplace", out var replace) || replace;
    internal IReadOnlyList<(int Line, string Kind, object Value)> RowData =>
        TryGet<IReadOnlyList<(int, string, object)>>("rowData", out var rows) ? rows : Array.Empty<(int, string, object)>();
    internal LiquidAnalysis? Liquid => TryGet<LiquidPayload>("analysis", out var data) ? new LiquidAnalysis(data) : null;
    internal Advice? PurificationMethods => TryGet<Advice>("methods", out var methods) ? methods : null;

    internal bool TryGetAppraisal(out (int Total, int PerfectMin, int PerfectMax,
        IReadOnlyList<(string Label, int Amount, bool IsBase)> Parts) appraisal)
    {
        appraisal = default;
        if ((Id != "value" && Id != "injected-value") || !TryGet<int>("total", out var total)
            || !TryGet<int>("perfectMin", out var low) || !TryGet<int>("perfectMax", out var high)
            || !TryGet<IReadOnlyList<(string, int, bool)>>("parts", out var parts)) return false;
        appraisal = (total, low, high, parts);
        return true;
    }

    internal bool TryGetMachineEffect(out (string Stat, int? BonusPercent, string Text,
        IReadOnlyList<(string Metric, double Delta)> Changes) effect)
    {
        effect = default;
        if (Id != "machine-effect" || !TryGet<string>("stat", out var stat)
            || !TryGet<string>("effectText", out var text)) return false;
        effect = (stat, TryGet<int>("bonusPercent", out var bonus) ? bonus : null, text,
            TryGet<IReadOnlyList<(string, double)>>("changes", out var changes) ? changes : Array.Empty<(string, double)>());
        return true;
    }

    internal bool TryGet<T>(string name, out T value)
    {
        if (Fields.TryGetValue(name, out var field) && field is T typed)
        {
            value = typed;
            return true;
        }
        value = default!;
        return false;
    }
}
