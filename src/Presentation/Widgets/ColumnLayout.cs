namespace ItemDetails;

/// <summary>
/// 机器与模块 tooltip 的两列式布局：标签（性能、效率、质量……）一列，数值与说明一列，
/// 列位置用 TMP 的 &lt;pos&gt; 对齐；标签只用文字色区分语气，不画底色块。
///
/// 规范文本不受影响（第三方读到的仍是原文）；两列写法只在原版窗口构建时换入，
/// 见 <see cref="RenderSlices"/>。
/// 配色一律取游戏自己的 <see cref="RenderHandler.ColorPalette"/>，跟随原版色调。
/// </summary>
internal static class ColumnLayout
{
    private static string Hex(RenderHandler.ColorPalette color) => "#" + ((int)color).ToString("x6");

    // 标签文字色：正向绿、负向红、自学习紫、提示琥珀（橙）、信息青蓝。
    internal static readonly string PositiveText = Hex(RenderHandler.ColorPalette.LightGreen);
    internal static readonly string NegativeText = Hex(RenderHandler.ColorPalette.LightRed);
    internal static readonly string PurpleText = Hex(RenderHandler.ColorPalette.LightPurple);
    internal static readonly string AmberText = Hex(RenderHandler.ColorPalette.LightOrange);
    internal static readonly string BlueText = Hex(RenderHandler.ColorPalette.LightBlue);
    internal static readonly string CyanText = Hex(RenderHandler.ColorPalette.LightCyan);

    /// <summary>数值的正负色：与同一行的标签同色，读起来是一个整体。</summary>
    internal static readonly string ValuePositive = PositiveText;
    internal static readonly string ValueNegative = NegativeText;
}
