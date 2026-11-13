using System;
using System.Globalization;

namespace ItemDetails;

/// <summary>
/// 水质成分的数值排版（纯逻辑，不依赖 Unity）：百分比、体积、容器容量的数字与单位。
///
/// 列对齐不在这里做：正式版 tooltip 用 NotoSansSC，中日韩字约为西文的 1.9 倍，
/// 按字符补空格必然错位，所以改为在 <see cref="WaterColumns"/> 里按实际字体像素宽度补位。
///
/// 1 part = 1 µL，即 1000 parts = 1 mL。体积单位用国际符号 mL / L（各语言一致），
/// 与百分号同理，不进本地化表。
/// </summary>
internal static class WaterLayout
{
    internal const string MilliliterUnit = "mL";
    internal const string LiterUnit = "L";

    /// <summary>
    /// 百分比数字（不含 %）。默认两位小数；<paramref name="optimized"/> 为真时按有效数字：
    /// ≥0.1% 仍是两位小数，更小的取 3 位有效数字（0.0312），低于 0.001% 写「&lt;0.001」，
    /// 免得微量杂质一排「0.00」。
    /// </summary>
    internal static string PercentText(int parts, int total, IFormatProvider? culture = null, bool optimized = false)
    {
        culture ??= CultureInfo.InvariantCulture;
        if (total <= 0 || parts <= 0) return "0.00";
        double p = (double)parts * 100.0 / total;
        if (!optimized || p >= 0.1) return p.ToString("0.00", culture);
        if (p >= 0.01) return p.ToString("0.0000", culture);
        if (p >= 0.001) return p.ToString("0.00000", culture);
        return "<0.001";
    }

    /// <summary>
    /// 成分用量的 mL 数字（不含单位）。1 part = 1 µL，所以 mL 的三位小数是精确值。
    /// 默认一位小数；<paramref name="optimized"/> 为真时取约 3 位有效数字：
    /// ≥100 取整，10–100 一位小数，1–10 两位小数，&lt;1 三位小数。
    /// </summary>
    internal static string AmountText(int parts, IFormatProvider? culture = null, bool optimized = false)
    {
        culture ??= CultureInfo.InvariantCulture;
        if (parts <= 0) return optimized ? "0" : "0.0";
        double ml = parts / 1000.0;
        if (!optimized) return ml.ToString("0.0", culture);
        if (ml >= 100) return ml.ToString("0", culture);
        if (ml >= 10) return ml.ToString("0.0", culture);
        if (ml >= 1) return ml.ToString("0.00", culture);
        return ml.ToString("0.000", culture);
    }

    /// <summary>体积数字（不含单位）：mL 保留一位小数，L 最多两位小数。</summary>
    internal static string VolumeText(int parts, bool liters, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        if (parts <= 0) return "0.0";
        return liters
            ? (parts / 1_000_000.0).ToString("0.##", culture)
            : (parts / 1000.0).ToString("0.0", culture);
    }

    /// <summary>容器行的体积段：`2000/2000mL`（紧凑写法，给进度条留宽度）。</summary>
    internal static string Capacity(int currentMl, int capacityMl, bool liters, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        string unit = liters ? LiterUnit : MilliliterUnit;
        return Volume.Number(currentMl, liters, culture) + "/"
             + Volume.Number(capacityMl, liters, culture) + unit;
    }

    /// <summary>
    /// 该成分是否低于 1%：固体成分表把这类折成一行提示，按住展开键（默认 Ctrl）展开。
    /// 用整数比较（parts × 100 &lt; total）而不是先除再比，避免 0.999…% 被算成 1%。
    /// </summary>
    internal static bool BelowOnePercent(int parts, int total)
        => total > 0 && (long)parts * 100L < total;
}
