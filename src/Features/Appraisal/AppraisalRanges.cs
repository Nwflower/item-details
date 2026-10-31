using System;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 鉴定区间的运行时一半：完美区间直接由游戏的
/// <c>AppraisalServiceHelper.GetEstimatePrecision(correct, value)</c> 判定，
/// 在真实价两侧二分出 grade 1 的边界。其它模组改了档位规则（补丁或换实现）时，
/// 显示的区间与游戏实际判定保持一致。
///
/// 函数找不到、真实价本身不判完美（规则已面目全非）时退回照抄版 <see cref="Perfect"/>。
/// 档位函数是纯计算，不读写物品，反复调用没有副作用。
/// </summary>
internal static partial class AppraisalRanges
{
    /// <summary>向上找边界时最多翻倍的次数，防止异常规则让搜索跑飞。</summary>
    private const int MaxDoublings = 20;

    private static Func<int, int, int>? _grade;
    private static bool _gradeResolved;

    /// <summary>游戏实际判定为「完美」的闭区间 [low, high]。</summary>
    internal static (int Low, int High) GamePerfect(int correct)
    {
        var grade = Grade();
        if (grade == null || correct <= 0) return Perfect(correct);
        try
        {
            if (grade(correct, correct) != 1) return Perfect(correct);

            // 下界：[0, correct] 内第一个判完美的值
            int lo = 0, hi = correct;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (grade(correct, mid) == 1) hi = mid; else lo = mid + 1;
            }
            int low = lo;

            // 上界：先翻倍找到一个不完美的值，再在两者之间二分
            int inside = correct;
            int outside = correct;
            for (int i = 0; i < MaxDoublings; i++)
            {
                outside = outside > int.MaxValue / 2 ? int.MaxValue : outside * 2;
                if (grade(correct, outside) != 1) break;
                inside = outside;
                if (outside == int.MaxValue) return Perfect(correct);
            }
            if (grade(correct, outside) == 1) return Perfect(correct);
            while (outside - inside > 1)
            {
                int mid = inside + (outside - inside) / 2;
                if (grade(correct, mid) == 1) inside = mid; else outside = mid;
            }
            return (low, inside);
        }
        catch (Exception ex)
        {
            Log.Warn("AppraisalRanges.Game", ex);
            return Perfect(correct);
        }
    }

    private static Func<int, int, int>? Grade()
    {
        if (_gradeResolved) return _grade;
        _gradeResolved = true;
        try
        {
            var method = AccessTools.Method(typeof(AppraisalServiceHelper), "GetEstimatePrecision",
                new[] { typeof(int), typeof(int) });
            if (method == null || method.ReturnType != typeof(int) || !method.IsStatic)
            {
                Log.Warn("AppraisalRanges.Game", "未找到 AppraisalServiceHelper.GetEstimatePrecision，完美区间使用内置规则");
                return null;
            }
            _grade = (Func<int, int, int>)Delegate.CreateDelegate(typeof(Func<int, int, int>), method);
        }
        catch (Exception ex)
        {
            Log.Warn("AppraisalRanges.Game", ex);
        }
        return _grade;
    }
}
