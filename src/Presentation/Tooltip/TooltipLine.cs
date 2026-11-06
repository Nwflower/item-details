namespace ItemDetails;

/// <summary>
/// tooltip 追加的一行：已本地化的文本 + 颜色。
///
/// 以前每个信息模块各定义一个 { Zh, En } 结构，再由 ModEntry 用
/// <c>_zh ? ln.Zh : ln.En</c> 选语言；可模块内部其实早就按 zh 参数选好了，
/// 于是同一段表达式被抄了两遍（有的甚至直接把同一个字符串塞进两个字段）。
/// 现在模块直接给出最终文本与颜色，调用方只负责输出。
///
/// 带进度条的行（<see cref="HasBar"/>）保留比例、颜色和文字，由输出端按提示框布局绘制。
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

    /// <summary>纯文本行（进度条已由表头那一行画，这里只要文字）。</summary>
    internal TooltipLine(string text, RenderHandler.ColorPalette color, bool noBar)
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

    internal readonly string? DataKind;
    internal readonly object? Data;

    private TooltipLine(TooltipLine source, string kind, object data)
    {
        this = source;
        DataKind = kind;
        Data = data;
    }

    internal TooltipLine WithData(string kind, object data) => new TooltipLine(this, kind, data);

    /// <summary>
    /// 原版窗口里的呈现写法（如成分表的 &lt;pos&gt; 三列）；null 表示照 <see cref="Text"/> 显示。
    /// <see cref="Text"/> 始终是规范输出，第三方读到的是它；呈现由 <see cref="RenderSlices"/> 在构建时换入。
    /// </summary>
    internal readonly string? Render;

    private TooltipLine(TooltipLine source, string render)
    {
        this = source;
        Render = render;
    }

    internal TooltipLine WithRender(string render) => new TooltipLine(this, render);

    private TooltipLine(TooltipLine source, int barLength)
    {
        this = source;
        BarLength = barLength;
    }

    /// <summary>同一行，换一个进度条格数。</summary>
    internal TooltipLine WithBarLength(int length) => new TooltipLine(this, length);

    /// <summary>不画进度条：尾注并回行文字（规范文本与呈现写法都一样），其余不变。</summary>
    internal TooltipLine WithoutBar()
    {
        string tail = Tail ?? "";
        var plain = new TooltipLine(Text + tail, Color);
        plain = plain.WithRender((Render ?? Text) + (MachineAdapters.RechargeTailRender(this) ?? tail));
        return DataKind != null && Data != null ? plain.WithData(DataKind, Data) : plain;
    }

    internal bool HasBar => Tail != null;
}
