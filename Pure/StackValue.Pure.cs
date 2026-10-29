using System;

namespace ItemDetails;

/// <summary>
/// 堆叠价值纯逻辑：GetCurrentValue() 是单件当前价值，原版不乘 unitCount。
/// 整组价值必须显式乘数量；数量异常或溢出时宁可不出行，也不输出错误总价。
/// </summary>
internal static class StackValue
{
    internal static bool TryTotal(long unit, int count, out long total)
    {
        total = 0;
        if (unit <= 0) return false;
        if (count < 1) count = 1;
        if (count > 1 && unit > long.MaxValue / count) return false;

        total = unit * count;
        return true;
    }

    internal static double PerCell(long total, int cells)
        => total <= 0 || cells <= 0 ? 0 : (double)total / cells;
}
