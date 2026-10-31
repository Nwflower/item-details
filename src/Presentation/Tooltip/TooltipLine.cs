namespace ItemDetails;

/// <summary>
/// tooltip 追加的一行：已本地化的文本 + 颜色。
///
/// 以前每个信息模块各定义一个 { Zh, En } 结构，再由 ModEntry 用
/// <c>_zh ? ln.Zh : ln.En</c> 选语言；可模块内部其实早就按 zh 参数选好了，
/// 于是同一段表达式被抄了两遍（有的甚至直接把同一个字符串塞进两个字段）。
/// 现在模块直接给出最终文本与颜色，调用方只负责输出。
///
/// 带进度条的行（<see cref="HasBar"/>）由 <see cref="ProgressBarStyle.Format"/> 生成文本后直接输出，
/// 避免调用原版 AddProgressBar() 引入 noBreak 影响整段 tooltip 折行宽度。
/// </summary>
internal readonly struct TooltipLine
{
    internal readonly string Text;
    internal readonly RenderHandler.ColorPalette Color;

    /// <summary>进度条比例（0–1）。</summary>
    internal readonly float Bar;
    /// <summary>进度条格数。</summary>
    internal readonly int BarLength;
    /// <summary>进度条未填部分的颜色。</summary>
    internal readonly RenderHandler.ColorPalette BarDark;
    /// <summary>进度条后面的文字；null 表示这一行没有进度条。</summary>
    internal readonly string? Tail;

    internal TooltipLine(string text, RenderHandler.ColorPalette color)
    {
        Text = text;
        Color = color;
        Bar = 0f;
        BarLength = 0;
        BarDark = RenderHandler.ColorPalette.None;
        Tail = null;
    }

    /// <param name="length">进度条格数；原版各处 AddProgressBar 都用 10。</param>
    internal TooltipLine(string text, float bar, int length, string tail,
        RenderHandler.ColorPalette color, RenderHandler.ColorPalette barDark)
    {
        Text = text;
        Color = color;
        Bar = bar > 0f ? bar : 0f;
        BarLength = length;
        BarDark = barDark;
        Tail = tail ?? "";
    }

    internal bool HasBar => Tail != null;
}
