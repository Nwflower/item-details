namespace ItemDetails;

/// <summary>
/// tooltip 追加的一行：已本地化的文本 + 颜色。
///
/// 以前每个信息模块各定义一个 { Zh, En } 结构，再由 ModEntry 用
/// <c>_zh ? ln.Zh : ln.En</c> 选语言；可模块内部其实早就按 zh 参数选好了，
/// 于是同一段表达式被抄了两遍（有的甚至直接把同一个字符串塞进两个字段）。
/// 现在模块直接给出最终文本与颜色，调用方只负责输出。
/// </summary>
internal readonly struct TooltipLine
{
    internal readonly string Text;
    internal readonly RenderHandler.ColorPalette Color;

    internal TooltipLine(string text, RenderHandler.ColorPalette color)
    {
        Text = text;
        Color = color;
    }
}
