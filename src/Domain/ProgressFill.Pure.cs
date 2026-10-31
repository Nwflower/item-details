using System;

namespace ItemDetails;

/// <summary>
/// 进度条已填格数。原版是 floor(比例 × 格数)，只差一点没满也只画 length − 1 格；
/// 而旁边的数字往往四舍五入成已满（水罐差 0.4 ml 也写「3000 ml / 3000 ml」）。
/// 差额不到 <see cref="FullThreshold"/> 时按满画，其余与原版一致。
/// </summary>
internal static class ProgressFill
{
    internal const float FullThreshold = 0.9995f;

    internal static int Filled(float rawRatio, int length)
    {
        if (length <= 0) return 0;
        float ratio = float.IsFinite(rawRatio) ? Math.Clamp(rawRatio, 0f, 1f) : 0f;
        if (ratio >= FullThreshold) return length;
        return (int)(ratio * length);
    }
}
