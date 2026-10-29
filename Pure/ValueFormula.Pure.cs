using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 纯公式层：只依赖标签值，不引用 UnityEngine / GameItem / GameRead。
///
/// 拆出来的目的：可以让 <c>tools/FormulaTests</c> 直接编译本文件做离线回归测试，
/// 不需要启动游戏。公式常量与 0.46D 的
/// AppHandHelper / PlasmaHelper / RibwichHelper.GetCorrectValue 逐项核对。
///
/// 与 <c>ValueBreakdown.cs</c> 是同一个 partial class 的两半：
/// 本文件负责算，另一半负责从 GameItem 取标签并做自校验。
/// </summary>
internal static partial class ValueBreakdown
{
    internal enum Kind { None, Hand, Ribwich, Plasma }

    /// <summary>
    /// 一项价值来源。文案拆成「本地化键 + 英文兜底」：本文件属于纯公式层，
    /// 不引用 Unity/游戏类型，所以不在这里选语言，交给 Appraisal 层用 Loc.T 解析。
    /// <see cref="Args"/> 供 <c>{0}</c> 之类的占位符使用。
    /// </summary>
    internal readonly struct Part
    {
        internal readonly string Key;
        internal readonly string En;
        internal readonly int Amount;
        internal readonly bool IsBase;
        internal readonly object[] Args;
        internal Part(string key, string en, int amount, bool isBase = false, params object[] args)
        { Key = key; En = en; Amount = amount; IsBase = isBase; Args = args ?? System.Array.Empty<object>(); }
    }

    internal sealed class Result
    {
        internal Kind Kind;
        internal readonly List<Part> Parts = new();
        internal int Total;
        internal bool Valid;
    }

    // ---------------- HAND ----------------
    // 对应 AppHandHelper.GetCorrectValue：基础 100，下限 50
    internal static Result ComputeHand(Func<string, string?> str)
    {
        string? mat = str("HAND_MATERIAL");
        string? iface = str("HAND_INTERFACE");
        string? cond = str("HAND_CONDITION");
        string? armored = str("HAND_ADDON_ARMORED");
        string? bladed = str("HAND_ADDON_BLADED");

        if (!OneOf(mat, "ALUMINUM_ALLOY", "CARBON_COMPOSITE", "GOLD_PLATED")
            || !OneOf(iface, "EMG", "EMG_PLUS", "NEURAL", "UNSPECIFIED")
            || !OneOf(cond, "NONE", "WEAR", "DAMAGE")
            || !OneOf(armored, "NONE", "ARMORED")
            || !OneOf(bladed, "NONE", "BLADED"))
            return new Result { Kind = Kind.Hand, Valid = false };

        var r = new Result { Kind = Kind.Hand };
        r.Parts.Add(new("appraisal.base", "Base", 100, true));
        if (mat == "CARBON_COMPOSITE") r.Parts.Add(new("appraisal.carbon", "Carbon-fiber shell", 50));
        if (mat == "GOLD_PLATED") r.Parts.Add(new("appraisal.gold", "Gold plating", 200));
        if (iface == "NEURAL") r.Parts.Add(new("appraisal.neural", "Neural jack", 120));
        if (iface == "EMG_PLUS") r.Parts.Add(new("appraisal.emgplus", "EMG+ connector", 45));
        if (iface == "UNSPECIFIED") r.Parts.Add(new("appraisal.iface_unknown", "Interface type unknown", -60));
        if (cond == "WEAR") r.Parts.Add(new("appraisal.worn", "Worn", -35));
        if (cond == "DAMAGE") r.Parts.Add(new("appraisal.damaged", "Damaged", -85));
        if (armored == "ARMORED") r.Parts.Add(new("appraisal.armor", "Armor module", 50));
        if (bladed == "BLADED") r.Parts.Add(new("appraisal.blade", "Blade module", 80));
        if (mat == "CARBON_COMPOSITE" && (iface == "EMG_PLUS" || iface == "NEURAL")
            && cond == "NONE" && armored == "ARMORED" && bladed == "BLADED")
            r.Parts.Add(new("appraisal.build_tactical", "Tactical build", 100));
        if (mat == "GOLD_PLATED" && iface == "NEURAL" && cond == "NONE")
            r.Parts.Add(new("appraisal.build_deluxe", "Deluxe build", 200));
        Sum(r, 50);
        return r;
    }

    // ---------------- PLASMA ----------------
    // 对应 PlasmaHelper.GetCorrectValue：基础 300，下限 90
    internal static Result ComputePlasma(Func<string, string?> str)
    {
        string? wear = str("PLASMA_CASING_CONDITION_WEAR");
        string? crack = str("PLASMA_CASING_CONDITION_CRACK");
        string? uncov = str("PLASMA_CASING_CONDITION_UNCOVERED");
        string? reinf = str("PLASMA_CASING_CONDITION_REINFORCED");
        string? color = str("PLASMA_COLOR");
        string? seal = str("PLASMA_SEAL");

        if (!OneOf(wear, "NONE", "WEAR", "WEAR_HEAVY")
            || !OneOf(crack, "NONE", "CRACK")
            || !OneOf(uncov, "NONE", "UNCOVERED")
            || !OneOf(reinf, "NONE", "REINFORCED")
            || !OneOf(color, "STANDARD", "POOR1", "POOR2", "HIGH_QUALITY")
            || !OneOf(seal, "NONE", "LL", "ZHONGHUA", "REPUBLIC", "IGNITION_LAB"))
            return new Result { Kind = Kind.Plasma, Valid = false };

        var r = new Result { Kind = Kind.Plasma };
        r.Parts.Add(new("appraisal.base", "Base", 300, true));
        if (wear == "WEAR") r.Parts.Add(new("appraisal.casing_wear", "Slight wear", -45));
        if (wear == "WEAR_HEAVY") r.Parts.Add(new("appraisal.casing_wear_heavy", "Severe wear", -90));
        if (crack == "CRACK") r.Parts.Add(new("appraisal.casing_crack", "Casing fracture", -150));
        if (uncov == "UNCOVERED") r.Parts.Add(new("appraisal.casing_screws", "Loose screws", -30));
        if (reinf == "REINFORCED") r.Parts.Add(new("appraisal.casing_reinforced", "Reinforcement", 60));
        if (color == "POOR1" || color == "POOR2") r.Parts.Add(new("appraisal.finish_cheap", "Cheap finish", -150));
        if (color == "HIGH_QUALITY") r.Parts.Add(new("appraisal.finish_premium", "Premium finish", 150));
        if (seal == "LL" && (color == "STANDARD" || color == "POOR1" || color == "POOR2"))
            r.Parts.Add(new("appraisal.seal_ll", "Wild's Rides incentive", 60));
        else if (seal == "ZHONGHUA" && wear == "NONE" && crack == "NONE" && uncov == "NONE" && color == "STANDARD")
            r.Parts.Add(new("appraisal.seal_zhonghua", "ZhongHua incentive", 120));
        else if (seal == "REPUBLIC" && crack != "CRACK" && color == "STANDARD")
            r.Parts.Add(new("appraisal.seal_republic", "Republic Naval incentive", 120));
        else if (seal == "IGNITION_LAB" && reinf == "NONE" && wear == "NONE" && crack == "NONE" && uncov == "NONE" && color == "HIGH_QUALITY")
            r.Parts.Add(new("appraisal.seal_ignition", "Ignition Lab incentive", 750));
        Sum(r, 90);
        return r;
    }

    // ---------------- RIBWICH ----------------
    // 对应 RibwichHelper.GetCorrectValue：基础 150，下限 40
    internal static Result ComputeRibwich(Func<string, int?> integer, int native)
    {
        int? wrap = integer("RIBWICH_WRAP_TAG");
        if (wrap == null) return new Result { Kind = Kind.Ribwich, Valid = false };

        // 已开封(包装 1/2) 与赝品在原版都直接返回固定 40：
        // RibwichHelper.GetCorrectValue 里 IsOpened(...) || IsCounterfeit(...) → return 40。
        // 赝品判定涉及日期/厂号/logo 多个标签，这里以"游戏真实值为 40"为准，
        // 与自校验同源，不会与游戏结论冲突。
        if (wrap == 1 || wrap == 2 || native == 40)
        {
            var fr = new Result { Kind = Kind.Ribwich, Valid = true, Total = 40 };
            fr.Parts.Add(new("appraisal.ribwich_opened", "Opened or fake (flat 40)", 40, true));
            return fr;
        }

        int? meat = integer("RIBWICH_MEAT_TAG");
        int? bun = integer("RIBWICH_BUN_TAG");
        int? pickles = integer("RIBWICH_PICKLES_TAG");
        int? month = integer("RIBWICH_PRODUCTION_MONTH_TAG");
        int? year = integer("RIBWICH_PRODUCTION_YEAR_TAG");
        int? fac = integer("RIBWICH_FACILITY_NUMBER_TAG");

        if (meat == null || bun == null || pickles == null || month == null || year == null || fac == null)
            return new Result { Kind = Kind.Ribwich, Valid = false };

        var r = new Result { Kind = Kind.Ribwich };
        r.Parts.Add(new("appraisal.base", "Base", 150, true));
        if (meat == 1) r.Parts.Add(new("appraisal.ribwich_double_meat", "Double meat", 800));
        if (bun == 1 && pickles == 1) r.Parts.Add(new("appraisal.ribwich_deluxe", "Deluxe combo", 300));
        if ((month == 1 && year == 425) || (month == 12 && year == 438))
            r.Parts.Add(new("appraisal.ribwich_month_edge", "First or final month", 50));
        if (year >= 425 && year <= 429) r.Parts.Add(new("appraisal.ribwich_first_batch", "First batch", 80));
        int f = fac.Value;
        if (OneOf(f, 1, 2, 5, 8, 21, 34))
            r.Parts.Add(new("appraisal.ribwich_plant_grade_a", "Grade-A plant {0:D2}", 50, false, f));
        else if (OneOf(f, 7, 15, 18, 26, 31))
            r.Parts.Add(new("appraisal.ribwich_plant_grade_c", "Grade-C plant {0:D2}", -50, false, f));
        Sum(r, 40);
        return r;
    }

    // ---------------- 工具 ----------------
    internal static void Sum(Result r, int minimum)
    {
        int sub = 0;
        foreach (var p in r.Parts) sub += p.Amount;
        r.Total = Math.Max(minimum, sub);
        r.Valid = true;
    }

    internal static bool OneOf(string? v, params string[] opts)
    {
        if (v == null) return false;
        foreach (string o in opts) if (v == o) return true;
        return false;
    }

    internal static bool OneOf(int v, params int[] opts)
    {
        foreach (int o in opts) if (v == o) return true;
        return false;
    }
}
