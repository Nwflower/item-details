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
return failures == 0 ? 0 : 1;

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
