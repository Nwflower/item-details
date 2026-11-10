using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ItemDetails;

/// <summary>已核实的原版回礼。成交回调可能写入游戏状态，预览只读取奖池和输入。</summary>
internal static class TrashExchangeRewards
{
    internal static string? Describe(ExchangeBarter offer, List<GameItem> inputs, bool nativeAccept)
    {
        var callback = offer.onAccept;
        if (callback == null || callback.GetInvocationList().Length != 1) return null;

        // 原版回调是 ExchangeList 上的编译器生成方法。自定义回调即使沿用配方 ID，也可能换了回礼。
        var method = callback.Method;
        var owner = method.DeclaringType;
        while (owner?.DeclaringType != null) owner = owner.DeclaringType;
        if (owner != typeof(ExchangeList) || Harmony.GetPatchInfo(method) != null) return null;
        if (!nativeAccept) return Unknown();

        switch (offer.identifier)
        {
            case "filter_barter": return Item("water_filter", "Water filter");
            case "smuggler_barter": return Item("smuggler_bay_mod", "Modified smuggler bay");
            case "sec_crate_barter": return Item("sec_box", "Security crate");
            case "med_crate_barter": return Item("med_box", "Medical crate");
            case "eng_crate_barter": return Item("eng_box", "Engineering crate");
            case "seed_barter": return Energy(100);
            case "immunivax_barter": return Item("large_purple_injector", "Immunivax injector");
            case "bm_rep_barter": return Loc.T("exchange.reputation", "Black Market reputation");
            case "module_barter": return Module(inputs);
            case "commandKeycardBarter":
                return Loc.T("exchange.keycards", "Two draws from: {0}", Names(
                    ("sec_keycard", "Security keycard"), ("eng_keycard", "Engineering keycard"),
                    ("med_keycard", "Medical keycard")));
            case "bullet_barter":
                return Candidates(("cat_bar", "Cat bar"), ("processed_meat", "Processed meat"),
                    ("soda_red", "Red soda"), ("processed_milk", "Processed milk"), ("galaxy_blend", "Galaxy Blend"));
            case "dream_dust_barter":
                return DreamDust(inputs.Sum(item => (int)Purity(item)));
            case "improved_dream_dust_barter":
                var rewardTable = AccessTools.Method(typeof(ExchangeList), "ImprovedDreamDustRewards", new[] { typeof(double) });
                return rewardTable == null || Harmony.GetPatchInfo(rewardTable) != null
                    ? Unknown()
                    : ImprovedDreamDust(inputs.Average(Purity));
            case "poison_barter":
                int charge = 0;
                foreach (var item in inputs)
                {
                    // 原版对非化学成品先改写违禁与许可证特性再估价。这里只预览确定的回礼类型。
                    if (!GameRead.TagEnabled(item, "CHEMICAL_PRODUCT_TAG")) return Energy();
                    charge += (int)Purity(item) / 2;
                }
                return Energy(Mathf.Clamp(charge, 0, 100));
            default: return Unknown();
        }
    }

    private static string Module(List<GameItem> inputs)
    {
        var pool = ExchangeList.modulePool;
        var weights = ExchangeList.modulePoolOdd;
        if (pool == null || weights == null || weights.Count < pool.Count) return Unknown();

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        for (int i = 0; i < pool.Count; i++)
        {
            string id = pool[i];
            if (inputs.Any(item => item.identifier == id)) continue;
            if (weights[i] < 0) return Unknown();
            if (weights[i] == 0 || !ids.Add(id)) continue;
            names.Add(Item(id, id switch
            {
                "system_module_performance" => "Performance module",
                "system_module_efficiancy" => "Efficiency module",
                "system_module_quality" => "Quality module",
                "system_module_overclock" => "Overclock module",
                "system_module_eco" => "Eco module",
                "system_module_fineness" => "Fineness module",
                _ => id,
            }));
        }
        string core = Item("system_capped_neural_core", "Capped neural core");
        return names.Count == 0
            ? Loc.T("exchange.module", "Rare: {0}; otherwise no reward", core)
            : Loc.T("exchange.module_candidates", "{0}; rare: {1}", RandomFrom(string.Join(" / ", names)), core);
    }

    private static string DreamDust(int totalPurity)
    {
        if (totalPurity < 60) return Loc.T("exchange.low_purity", "No reward (Dream Dust purity too low)");

        var names = new List<string>
        {
            Item("water_filter", "Water filter"), Item("small_raw_meat", "Small raw meat"),
            Item("metal_ingot", "Metal ingot"), Item("common_electronic", "Common electronics"),
            Item("kitchen_knife", "Kitchen knife"), Item("common_medical", "Common medical supplies"),
            Item("birth_control_pill", "Birth control pill"), Item("blood_bag", "Blood bag"),
            Item("processed_meat", "Processed meat"),
        };
        if (!PlayerStore.HasInstance || !StartingPerk.IsBannedCommissary())
            names.Add(Item("food_stamp", "Food stamp"));
        if (totalPurity >= 120)
        {
            names.Add(Item("energy_credit_breeder", "Breeder energy credit"));
            names.Add(Item("scav_token", "Scavenger token"));
            names.Add(Item("kitchen_cleaver", "Kitchen cleaver"));
            names.Add(Item("pack_condom", "Pack of condoms"));
            names.Add(Energy(100));
        }
        return RandomFrom(string.Join(" / ", names));
    }

    private static string ImprovedDreamDust(double averagePurity)
    {
        var injector = ("large_purple_injector", "Immunivax injector");
        var pink = ("pink_injector", "Pink injector");
        var black = ("black_injector", "Black injector");
        var blood = ("blood_bag", "Blood bag");
        var blue = ("blue_blood_bag", "Blue blood bag");
        var medical = ("common_medical", "Common medical supplies");
        var salve = ("salve", "Salve");
        var surgery = ("surgery_tool", "Surgery tool");
        return averagePurity switch
        {
            >= 99 => Candidates(black, injector, blue),
            >= 90 => Candidates(black, injector, salve, blue, pink, surgery),
            >= 80 => Candidates(injector, pink, medical, salve, blue, blood, surgery, black),
            >= 60 => Candidates(pink, medical, injector, blood, black, blue),
            >= 45 => Candidates(pink, medical, injector, blood),
            >= 30 => Candidates(("bandage_item", "Bandage"), ("hemostatic_bandage_item", "Hemostatic bandage"),
                ("topical_bandage_item", "Topical bandage"), medical, pink),
            _ => Item("bandage_item", "Bandage") + " ×2",
        };
    }

    private static string Unknown() => Loc.T("exchange.unknown", "Reward determined at settlement");

    private static string Candidates(params (string Id, string Name)[] items) => RandomFrom(Names(items));

    private static string RandomFrom(string names) => Loc.T("exchange.candidates", "Random from: {0}", names);

    private static string Names(params (string Id, string Name)[] items)
        => string.Join(" / ", items.Select(item => Item(item.Id, item.Name)));

    private static double Purity(GameItem item)
        => GameRead.TagEnabled(item, "CHEMICAL_PRODUCT_PURITY_TAG")
            ? GameRead.TagDouble(item, "CHEMICAL_PRODUCT_PURITY_TAG") ?? 0
            : 0;

    private static string Energy(int? charge = null)
    {
        string item = Item("energy_credit", "Energy credit");
        return charge.HasValue ? $"{item} ({charge} kJ)" : item;
    }

    private static string Item(string id, string fallback)
    {
        string name = GameRead.LookupItemName(id);
        return name.Length > 0 ? name : fallback;
    }
}
