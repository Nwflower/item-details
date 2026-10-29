namespace ItemDetails;

/// <summary>
/// 鉴定区间计算（纯函数，可离线测试）。
///
/// 必须与游戏完全同源，否则区间边界会差 1：
///   RNG.CalculatePercentage(value, p) = value * p / 100   ← 整数截断（向零取整）
///   完美档 = 真实价 95%–105%（AppraisalHelper.GetEstimatePrecision 的 grade 1 区间）
/// </summary>
internal static class AppraisalRanges
{
    /// <summary>等价于游戏的 RNG.CalculatePercentage（整数截断）。</summary>
    internal static int Percent(int value, int percent) => value * percent / 100;

    /// <summary>grade 1「完美」区间，闭区间 [low, high]。</summary>
    internal static (int Low, int High) Perfect(int correct)
        => (Percent(correct, 95), Percent(correct, 105));

    /// <summary>
    /// 完整档位（1 完美 … 5 糟糕），逐行照抄 AppraisalHelper.GetEstimatePrecision。
    /// 存在的意义：可以用它做「显示区间 == 游戏判定」的暴力一致性测试。
    /// </summary>
    internal static int Grade(int correct, int value)
    {
        int low = Percent(correct, 95);
        int high = Percent(correct, 105);

        if (value >= low && value <= high) return 1;

        if (value < low)
        {
            if (value <= Percent(correct, 75)) return 5;
            if (value <= Percent(correct, 85)) return 4;
            if (value <= Percent(correct, 90)) return 3;
            return 2;
        }

        if (value >= Percent(correct, 125)) return 5;
        if (value >= Percent(correct, 115)) return 4;
        if (value >= Percent(correct, 110)) return 3;
        return 2;
    }
}
