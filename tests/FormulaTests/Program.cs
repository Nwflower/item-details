using System;
using System.Collections.Generic;
using ItemDetails;

int failures = 0;
Check("intact plasma breakdown reconciles with native value", () =>
    Reconciles(ValueBreakdown.ComputePlasma(Plasma("NONE", "STANDARD")), 300));
Check("minimum plasma price is explained by its contributions", () =>
    Reconciles(ValueBreakdown.ComputePlasma(Plasma("CRACK", "POOR1")), 90));
Check("minimum hand price is explained by its contributions", () =>
{
    var tags = new Dictionary<string, string>
    {
        ["HAND_MATERIAL"] = "ALUMINUM_ALLOY", ["HAND_INTERFACE"] = "UNSPECIFIED",
        ["HAND_CONDITION"] = "DAMAGE", ["HAND_ADDON_ARMORED"] = "NONE",
        ["HAND_ADDON_BLADED"] = "NONE",
    };
    Reconciles(ValueBreakdown.ComputeHand(key => tags.GetValueOrDefault(key)), 50);
});
Check("furnace favors ore and changes its input/output count with blast module", () =>
{
    Require(FurnacePurity.MaterialOffset(0, false) == 0, "scrap-only base offset");
    Require(FurnacePurity.MaterialOffset(1, false) == 1, "mixed scrap/ore base offset");
    Require(FurnacePurity.MaterialOffset(2, false) == 2, "ore-only base offset");
    Require(FurnacePurity.MaterialOffset(2, true) == 1, "blast must use 3 inputs");
    Require(FurnacePurity.MaterialOffset(3, true) == 2, "blast favors the available ore");
    Require(FurnacePurity.InputCount(true) == 3 && FurnacePurity.OutputCount(true) == 2,
        "blast input/output yield");
});
Check("furnace bonus is tiered; blast penalty comes before flux floor", () =>
{
    Require(FurnacePurity.Offset(99, false, false) == 0, "+99% not a whole tier");
    Require(FurnacePurity.Offset(100, false, false) == 1, "+100% adds a tier");
    Require(FurnacePurity.Offset(200, false, false) == 2, "+200% adds two tiers");
    Require(FurnacePurity.Offset(0, true, false) == -1, "blast penalty");
    Require(FurnacePurity.Offset(0, true, true) == 1, "flux floor after blast penalty");
    Require(FurnacePurity.Offset(100, true, true) == 1, "flux doesn't stack on top");
});
Check("furnace consumes basic flux only when its floor actually applies", () =>
{
    Require(FurnacePurity.ConsumesBasicFlux(0, false, true, false), "no quality bonus");
    Require(!FurnacePurity.ConsumesBasicFlux(100, false, true, false), "native quality already sufficient");
    Require(FurnacePurity.ConsumesBasicFlux(100, true, true, false), "blast needs extra flux");
    Require(!FurnacePurity.ConsumesBasicFlux(200, true, true, false), "blast recovered by quality");
    Require(!FurnacePurity.ConsumesBasicFlux(0, false, true, true), "advanced flux takes priority");
});
Check("furnace next quality threshold accounts for flux and capped output", () =>
{
    Require(FurnacePurity.NextImprovement(125, q => FurnacePurity.Offset(q, false, false)) == 75,
        "next +200% threshold");
    Require(FurnacePurity.NextImprovement(0, q => FurnacePurity.Offset(q, false, true)) == 200,
        "flux already supplies first tier");
    Require(FurnacePurity.NextImprovement(0, q => FurnacePurity.Offset(q, true, true)) == 300,
        "blast and flux require three tiers of quality");
    Require(FurnacePurity.NextImprovement(200, q => Math.Min(10,
        8 + FurnacePurity.Offset(q, false, false))) == -1,
        "already at maximum ingot purity");
});
Check("small liquid amount in a large container stays in milliliters", () =>
{
    if (Volume.UseLiters(1)) throw new Exception("1 ml should remain in milliliters");
    if (Volume.UseLiters(3000)) throw new Exception("exactly 3000 ml should remain in milliliters");
});
Check("liquid amounts above 3 liters use liters", () =>
{
    if (!Volume.UseLiters(3001)) throw new Exception("3001 ml should use liters");
});
Check("component table folds sub-1% rows into a hint with the empty amount cell", () =>
{
    var table = WaterTable.Build(Components(), 1_908_000, expanded: false,
        "Ctrl to expand", parts => parts + "u", System.Globalization.CultureInfo.InvariantCulture);
    Require(table.Rows.Count == 3, "two bulky components plus one hint row");
    Require(table.Rows[0].Kind == WaterRowKind.Component && table.Rows[0].Percent == "78.62%", "water percent");
    Require(table.Rows[1].Kind == WaterRowKind.Component, "chemical pollutant stays visible");
    var hint = table.Rows[2];
    Require(hint.Kind == WaterRowKind.FoldHint, "last row is the fold hint");
    Require(hint.Amount.Length == 0, "hint leaves the amount cell empty");
    Require(hint.Percent == "0.42%", "hint percent is the folded total, got " + hint.Percent);
    Require(table.FoldedCount == 2 && table.FoldedParts == 8_000, "both traces folded");
});
Check("component table lists everything and drops the hint while expanded", () =>
{
    var table = WaterTable.Build(Components(), 1_908_000, expanded: true,
        "Ctrl to expand", parts => parts + "u", System.Globalization.CultureInfo.InvariantCulture);
    Require(table.Rows.Count == 4, "expanded table has no hint row");
    foreach (var row in table.Rows) Require(row.Kind == WaterRowKind.Component, "only component rows");
    Require(table.FoldedCount == 0, "nothing folded while expanded");
});
Check("no hint at all when nothing is folded", () =>
{
    var table = WaterTable.Build(BigComponents(), 1_908_000, expanded: false,
        "Ctrl to expand", parts => parts + "u", System.Globalization.CultureInfo.InvariantCulture);
    Require(table.Rows.Count == 2, "only the two visible components");
    foreach (var row in table.Rows) Require(row.Kind == WaterRowKind.Component, "no hint row");
});
Check("solid components reuse the same rows with their own amount format", () =>
{
    var table = WaterTable.Build(Components(), 1_908_000, expanded: false,
        "Ctrl to expand", p => (p / 1000.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " mL",
        System.Globalization.CultureInfo.InvariantCulture);
    Require(table.Rows[0].Amount == "1500.00 mL", "solid amount comes from the injected formatter, got " + table.Rows[0].Amount);
    Require(table.Rows[0].Name == "水" && table.Rows[1].Name == "化学污染物", "names pass through untouched");
});
Check("a near-full bar matches either fill convention", () =>
{
    // 原生 AddProgressBar 截断：0.9996 × 10 只画 9 格；本模组自己的条把接近满的补成 10 格。
    Require(ProgressFill.Native(0.9996f, 10) == 9, "native fill truncates");
    Require(ProgressFill.Filled(0.9996f, 10) == 10, "our bars snap a near-full ratio to full");
    Require(ProgressFill.Accepts(0.9996f, 10, 9), "truncated text still finds its record");
    Require(ProgressFill.Accepts(0.9996f, 10, 10), "snapped text finds it too");
    Require(!ProgressFill.Accepts(0.5f, 10, 9), "a different ratio must not borrow the record");
    Require(ProgressFill.Accepts(0.5f, 10, 5), "a half-filled bar matches");
});
Check("flowing rows always keep three cells, hint included", () =>
{
    var table = WaterTable.Build(Components(), 1_908_000, expanded: false,
        "Ctrl to expand", parts => parts + "u", System.Globalization.CultureInfo.InvariantCulture);
    string[] component = WaterTable.JoinCells(table.Rows[0], '　').Split('　');
    Require(component.Length == 3 && component[1] == "1500000u", "component keeps name, amount, percent");
    string[] hint = WaterTable.JoinCells(table.Rows[table.Rows.Count - 1], '　').Split('　');
    Require(hint.Length == 3, "hint row must not collapse to two cells, got " + hint.Length);
    Require(hint[0] == "Ctrl to expand", "hint text stays in the first cell");
    Require(hint[1] == WaterTable.EmptyCell, "empty amount cell holds the placeholder");
    Require(hint[2] == "0.42%", "percent stays in the last cell");
});
return failures == 0 ? 0 : 1;

static IReadOnlyList<(string Name, int Parts)> Components() => new List<(string Name, int Parts)>
{
    ("水", 1_500_000),
    ("化学污染物", 400_000),
    ("有机废物", 5_000),
    ("微生物", 3_000),
};

static IReadOnlyList<(string Name, int Parts)> BigComponents() => new List<(string Name, int Parts)>
{
    ("水", 1_500_000),
    ("化学污染物", 408_000),
};

void Check(string name, Action run)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Reconciles(ValueBreakdown.Result result, int nativeValue)
{
    if (!result.Valid || result.Total != nativeValue) throw new Exception($"expected native total {nativeValue}, got {result.Total}");
    int subtotal = 0;
    foreach (var part in result.Parts) subtotal += part.Amount;
    if (subtotal != result.Total) throw new Exception($"displayed contributions sum to {subtotal}, total is {result.Total}");
}

static Func<string, string?> Plasma(string crack, string finish)
{
    var tags = new Dictionary<string, string>
    {
        ["PLASMA_CASING_CONDITION_WEAR"] = "NONE", ["PLASMA_CASING_CONDITION_CRACK"] = crack,
        ["PLASMA_CASING_CONDITION_UNCOVERED"] = "NONE", ["PLASMA_CASING_CONDITION_REINFORCED"] = "NONE",
        ["PLASMA_COLOR"] = finish, ["PLASMA_SEAL"] = "NONE",
    };
    return key => tags.GetValueOrDefault(key);
}
