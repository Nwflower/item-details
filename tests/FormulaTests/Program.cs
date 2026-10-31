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
return failures == 0 ? 0 : 1;

void Check(string name, Action run)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
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
