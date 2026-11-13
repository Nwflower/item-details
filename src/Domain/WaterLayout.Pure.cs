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

    /// <summary>百分比数字（不含 %），保留两位小数。</summary>
    internal static string PercentText(int parts, int total, IFormatProvider? culture = null)
    {
        if (total <= 0 || parts <= 0) return "0.00";
        return ((double)parts * 100.0 / total).ToString("0.00", culture ?? CultureInfo.InvariantCulture);
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
