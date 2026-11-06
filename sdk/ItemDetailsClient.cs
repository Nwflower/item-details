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

    /// <summary>协议版本；与 AppDomain 键后缀一致。新增键时才递增。</summary>
    internal const int SectionProtocolVersion = 1;

    /// <summary>物品详情已启用区块协议。false 时全部走自己的原版显示路径。</summary>
    internal static bool SectionProtocolAvailable =>
        AppDomain.CurrentDomain.GetData(SectionQuery) != null;

    /// <summary>物品详情已启用液体查询。区块可用不代表液体设置开启，仍以查询返回值为准。</summary>
    internal static bool LiquidProtocolAvailable =>
        AppDomain.CurrentDomain.GetData(AnalysisSlot) != null &&
        AppDomain.CurrentDomain.GetData(AdviceSlot) != null;

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
    internal const string BadgeSlot = "item_details.tooltip_badge.v1";

    // null means this renderer does not own the current tooltip.
    internal static IDisposable RegisterTooltipLayout(Func<GameItem, TooltipLayoutOptions?> describe)
    {
        if (describe == null) throw new ArgumentNullException(nameof(describe));
        Func<GameItem, int?> callback = item => (int?)describe(item);
        return new Registration(LayoutSlot, callback);
    }

    internal static IDisposable RegisterTooltipAnchor(Func<RectTransform?> anchor)
        => new Registration(AnchorSlot, anchor ?? throw new ArgumentNullException(nameof(anchor)));

    // 分类徽章的容器；没显示时返回 null。排版方按它留出行高，不再按节点名找 GameObject。
    internal static IDisposable RegisterTooltipBadge(Func<RectTransform?> badge)
        => new Registration(BadgeSlot, badge ?? throw new ArgumentNullException(nameof(badge)));

    // 读取方：当前可见分类徽章，没有则 false。多次注册时先返回非空的那个。
    internal static bool TryGetTooltipBadge(out RectTransform? badge)
    {
        badge = null;
        if (AppDomain.CurrentDomain.GetData(BadgeSlot) is not Func<RectTransform?> providers) return false;
        foreach (Func<RectTransform?> getBadge in providers.GetInvocationList())
        {
            try { badge = getBadge(); }
            catch { continue; }
            if (badge != null) return true;
        }
        return false;
    }

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

    /// <summary>
    /// 成分表这一帧实际画出的行（`water` 区块）：Kind、名称、用量、百分比。
    /// 只列真正显示的行，已经按 Ctrl 状态折叠过；折叠提示行的用量为空、百分比是折起来的合计。
    /// 要判断表头（容量条、档位）用 <see cref="Liquid"/>。
    /// </summary>
    internal IReadOnlyList<(string Kind, string Name, string Amount, string Percent)> WaterRows =>
        TryGet<IReadOnlyList<(string, string, string, string)>>("rows", out var rows)
            ? rows : Array.Empty<(string, string, string, string)>();

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

    /// <summary>“（另 N 项低于显示阈值）”那一行的整行文本；没有隐藏成分时返回 false。</summary>
    internal bool TryGetTrace(out string text) => TryGet("trace", out text);

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

/// <summary>
/// 物品详情发出的稳定区块 ID。字段只增不改，读取方用 TryGet 并提供默认值。
/// 内嵌改写区块（price/calories/machine-* 等）来自对原生行的改写，顺序与原版一致。
/// </summary>
internal static class TooltipSectionIds
{
    internal const string Authenticity = "auth";
    internal const string Appraisal = "value";
    internal const string InjectedAppraisal = "injected-value";
    internal const string RibwichReasons = "ribwich";
    internal const string Berry = "berry";
    internal const string Hidden = "hidden";
    internal const string ValueEnhance = "value-enhance";
    internal const string FeatureReveal = "feature-reveal";
    internal const string Storage = "storage";
    internal const string MachineInterior = "machine-interior";
    internal const string ModuleLearning = "module-learning";
    internal const string Conditions = "conditions";
    internal const string Contraband = "contraband";
    internal const string Lottery = "lottery";
    internal const string Liquid = "water";
    internal const string SealedBox = "sealed-box";
    internal const string Scratcher = "scratcher";
    internal const string Seed = "seed";
    internal const string WaterAdvice = "water-advice";
    internal const string Wine = "wine";
    internal const string Meat = "meat";
    internal const string Market = "market";
    internal const string Price = "price";
    internal const string Calories = "calories";
    internal const string MachineSpeed = "machine-speed";
    internal const string MachinePower = "machine-power";
    internal const string MachineEffect = "machine-effect";
    internal const string Categories = "categories";
    internal const string Contents = "contents";
    internal const string SealedContents = "sealed-contents";
    internal const string Label = "label";
    internal const string Manufacturer = "manufacturer";
    internal const string InspectHint = "inspect-hint";
}

/// <summary>
/// 区块 `group` 字段的固定词表：这一块在玩家眼里属于哪一类信息。
/// 只增不改；读取方遇到不认识的组按原样保留该行，不要丢。
/// </summary>
internal static class TooltipGroupIds
{
    internal const string Liquid = "liquid";
    internal const string Plant = "plant";
    internal const string Machine = "machine";
    internal const string Condition = "condition";
    internal const string Appraisal = "appraisal";
    internal const string Food = "food";
    internal const string Lottery = "lottery";
    internal const string Brew = "brew";
    internal const string Module = "module";
    internal const string Label = "label";
    internal const string Description = "description";
    internal const string Price = "price";
    internal const string Contents = "contents";
}

/// <summary>
/// `water` 区块 `rows` 字段里每一行的 Kind。只增不改：读取方遇到不认识的 Kind 按普通成分行处理。
/// </summary>
internal static class WaterRowKinds
{
    /// <summary>一种成分：名称、用量、百分比三格都有值。</summary>
    internal const string Component = "component";

    /// <summary>折叠提示：用量为空，百分比是被折起来那几项的合计（可能为空）。</summary>
    internal const string FoldHint = "fold-hint";
}
