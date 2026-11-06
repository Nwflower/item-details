using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 价值构成适配层：从 GameItem 读标签，调用纯公式 <c>src/Domain/ValueFormula.Pure.cs</c>，
/// 并做「拆解合计必须等于游戏真实值」的自校验。
///
/// 仅适用于机械手(HAND)、肋明治(RIBWICH)、等离子(PLASMA) 三类；
/// 这三类没有真伪概念，只有价值构成。
///
/// 公式常量来自 0.46D 反编译结果（AppHandHelper / RibwichHelper / PlasmaHelper）。
/// </summary>
internal static partial class ValueBreakdown
{
    internal static Kind DetectKind(GameItem? item)
    {
        if (GameRead.IsTag(item, "HAND_TAG")) return Kind.Hand;
        if (GameRead.IsTag(item, "RIBWICH_TAG")) return Kind.Ribwich;
        if (GameRead.IsTag(item, "PLASMA_CELL_TAG")) return Kind.Plasma;
        return Kind.None;
    }

    // 同一次 tooltip 构建里，估价隐藏、正文拆解和实测分解会对同一件物品各算一次；
    // 构建期间物品不会变，按构建序号复用。构建之外（标题、结果预填、自检）照常现算。
    private static GameItem? _memoItem;
    private static int _memoBuild = -1;
    private static Result? _memoResult;

    internal static Result? Analyze(GameItem? item)
    {
        if (item == null) return null;
        if (!TooltipPipeline.IsBuilding) return AnalyzeCore(item);

        int build = TooltipPipeline.BuildId;
        if (build == _memoBuild && ReferenceEquals(item, _memoItem)) return _memoResult;
        var r = AnalyzeCore(item);
        _memoItem = item;
        _memoBuild = build;
        _memoResult = r;
        return r;
    }

    private static Result? AnalyzeCore(GameItem item)
    {
        try
        {
            // 先认类型：大多数鉴定物不是这三类，不必为它们去算真实值
            Kind k = DetectKind(item);
            if (k == Kind.None) return null;

            // 公式归属：写死的常量只有在"游戏还在用原版算法、且没人改过它"时才可信。
            // 第三方换了 getCorrectValue 或给原版方法打了补丁，常量随时会失配，干脆不拆。
            if (!VanillaFormula(k, item))
            {
                Log.Warn("ValueBreakdown.Formula",
                    $"kind={k} 的定价已不是原版函数（被替换或打过补丁），不显示这三类的价值分解");
                return null;
            }

            int native = GameRead.TrueValue(item) ?? 0;

            Func<string, string?> str = key => StrTag(item, key);
            Func<string, int?> integer = key => GameRead.TagInt(item, key);

            Result r = k switch
            {
                Kind.Hand => ComputeHand(str),
                Kind.Plasma => ComputePlasma(str),
                Kind.Ribwich => ComputeRibwich(integer, native),
                _ => new Result { Kind = Kind.None, Valid = false },
            };
            if (!r.Valid) return null;

            // 与游戏真实值核对，不一致就不显示（宁可不说，也不说错）
            if (r.Total != native)
            {
                // 对不上就不给分解：这套常量按反编译逐项核对过，改的是游戏或别的模组的定价。
                // 宁可少一个信息块，也不显示一个自称等于总价、实际对不上的构成。
                Log.Warn("ValueBreakdown.Mismatch",
                    $"kind={r.Kind} 计算={r.Total} 游戏={native}（公式随版本或其它模组变化，本件不显示价值分解）");
                return null;
            }
            return r;
        }
        catch (Exception ex)
        {
            Log.Warn("ValueBreakdown", ex);
            return null;
        }
    }

    private static string? StrTag(GameItem item, string key) => GameRead.TagString(item, key);

    /// <summary>
    /// 这三类的定价现在是否仍由原版函数决定：鉴定类型挂的还是原版 <c>GetCorrectValue</c>，
    /// 并且那个方法上没有别的模组的 Harmony 补丁。
    /// 原版委托被替换（第三方另写了算法）或方法被改写（改了系数）时，反编译得到的常量都不再适用。
    /// 判定失败一律当作"不是原版"，宁可不拆解。
    /// </summary>
    // 三个原版定价函数的方法信息：直接取静态方法组，不走反射查找，模组扫描器因此不需要额外能力。
    private static readonly System.Reflection.MethodInfo? HandMethod =
        ((Func<GameItem, int>)AppHandHelper.GetCorrectValue).Method;
    private static readonly System.Reflection.MethodInfo? PlasmaMethod =
        ((Func<GameItem, int>)PlasmaHelper.GetCorrectValue).Method;
    private static readonly System.Reflection.MethodInfo? RibwichMethod =
        ((Func<GameItem, int>)RibwichHelper.GetCorrectValue).Method;

    private static bool VanillaFormula(Kind kind, GameItem item)
    {
        try
        {
            System.Reflection.MethodInfo? vanilla = kind switch
            {
                Kind.Hand => HandMethod,
                Kind.Plasma => PlasmaMethod,
                Kind.Ribwich => RibwichMethod,
                _ => null,
            };
            if (vanilla == null) return false;
            if (ForeignPatches(vanilla)) return false;

            // 类型表里登记的函数必须还是这个原版方法；登记不到（没有类型或没挂函数）时按标签走，不额外拦。
            var func = AppraisalItemRegistry.Find(item)?.getCorrectValue;
            return func == null || func.Method == vanilla;
        }
        catch { return false; }
    }

    /// <summary>方法上是否有别的模组留下的补丁（我们自己从不补这些函数）。</summary>
    private static bool ForeignPatches(System.Reflection.MethodBase method)
    {
        try
        {
            var info = HarmonyLib.Harmony.GetPatchInfo(method);
            if (info == null) return false;
            foreach (var p in info.Prefixes) if (!Mine(p)) return true;
            foreach (var p in info.Postfixes) if (!Mine(p)) return true;
            foreach (var p in info.Transpilers) if (!Mine(p)) return true;
            return false;
        }
        catch { return false; }
    }

    private static bool Mine(HarmonyLib.Patch patch) =>
        patch.owner != null && patch.owner.StartsWith("item_details", StringComparison.Ordinal);
}
