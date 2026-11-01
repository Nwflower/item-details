using System;
using System.Globalization;

namespace ItemDetails;

/// <summary>
/// 液体体积显示：原版一律写 ml，大容器读起来是一串四五位数。
/// 用于决定单位的体积超过 3 L 时整行改用 L，最多两位小数；同一行的几个数共用一个单位，
/// 所以由调用方按实际要判断的体积决定单位，再逐个格式化。
/// </summary>
internal static class Volume
{
    /// <summary>超过这个毫升数才改用 L；3000 ml 的水罐仍写 ml。</summary>
    internal const int LiterThresholdMl = 3000;

    internal static bool UseLiters(int volumeMl) => volumeMl > LiterThresholdMl;

    /// <summary>只返回数字部分；单位由调用方按语言拼接。小数点按 <paramref name="culture"/>（游戏语言）。</summary>
    internal static string Number(int ml, bool liters, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        return liters ? (ml / 1000.0).ToString("0.##", culture) : ml.ToString(culture);
    }
}
