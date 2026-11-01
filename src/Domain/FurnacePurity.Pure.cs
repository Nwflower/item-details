using System;

namespace ItemDetails;

/// <summary>
/// 熔炉每晚回调中的纯度运算（Assembly-CSharp: MachineFurnace.Furnace 的回调）。
/// 这里只还原整数运算，不创建/消耗物品；最终纯度常量仍由游戏自己的 GetPurityConst 决定。
/// </summary>
internal static class FurnacePurity
{
    internal static int InputCount(bool blast) => blast ? 3 : 2;
    internal static int OutputCount(bool blast) => blast ? 2 : 1;

    /// <summary>游戏先收集 scrap_metal，再追加 common_ore，最后 GetRange(count - N, N)。</summary>
    internal static int CommonOreUsed(int availableOre, bool blast)
        => Math.Min(availableOre, InputCount(blast));

    internal static int MaterialOffset(int availableOre, bool blast)
        => 2 * CommonOreUsed(availableOre, blast) / InputCount(blast);

    /// <summary>C# 整数除法向零截断；先减高炉惩罚，再执行助熔剂的最低 +1 保底。</summary>
    internal static int Offset(int qualityBonus, bool blast, bool anyFlux)
    {
        int value = qualityBonus / 100;
        if (blast) value--;
        if (anyFlux && value < 1) value = 1;
        return value;
    }

    /// <summary>普通助熔剂只在未被高级助熔剂取代，且实际触发保底时消耗。</summary>
    internal static bool ConsumesBasicFlux(int qualityBonus, bool blast, bool basicFlux, bool advancedFlux)
        => basicFlux && !advancedFlux && Offset(qualityBonus, blast, false) < 1;

    /// <summary>
    /// 寻找下一次可观察输出真正发生改善的质量百分点增量。
    /// evaluate 应返回对应质量加成下的有效档位或游戏纯度级别，且必须单调不减。
    /// 不存在可观察提升（例如原料已经达到纯度上限）则返回 -1。
    /// </summary>
    internal static int NextImprovement(int qualityBonus, Func<int, int> evaluate)
    {
        const int limit = 1000;
        int current = evaluate(qualityBonus);
        if (evaluate(qualityBonus + limit) <= current) return -1;

        int lo = 1, hi = limit;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (evaluate(qualityBonus + mid) <= current) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
