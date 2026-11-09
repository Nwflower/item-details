using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>折行要看的一段文字：对应原版 RichTextBuilder 的一个片段。</summary>
internal readonly struct BreakSlice
{
    internal readonly string Text;
    internal readonly bool NewLine;
    internal readonly bool NoBreak;
    internal BreakSlice(string text, bool newLine, bool noBreak)
    {
        Text = text ?? ""; NewLine = newLine; NoBreak = noBreak;
    }
}

/// <summary>断点：片段下标 + 片段原始字符串里的下标。<see cref="IsSpace"/> 表示断开时吞掉这个空格。</summary>
internal readonly struct BreakPoint
{
    internal readonly int Slice;
    internal readonly int Index;
    internal readonly bool IsSpace;
    internal BreakPoint(int slice, int index, bool isSpace)
    {
        Slice = slice; Index = index; IsSpace = isSpace;
    }
}

/// <summary>
/// 不以空格分词的语言（中文、日文）的断点选择。原版只在空格处断，行首的「[标签] 」就成了唯一断点。
///   1. 行尾 40% 内优先断在空格或短语标点之后，不把短语拆开；
///   2. 否则逐字填满，遵守避头尾，不拆英文单词、数字和连续的 ASCII（如「(+33%)」）；
///   3. 不进不折行片段内部，下标跳过富文本标签。
/// 找不到断点时返回 false，由原版逻辑接手。
/// </summary>
internal static class TooltipLineBreak
{
    private const string PhraseEnders = "，。、；：！？";

    private readonly struct Glyph
    {
        internal readonly int Slice, Index;
        internal readonly char Char;
        internal readonly bool NoBreak;
        internal Glyph(int slice, int index, char c, bool noBreak)
        {
            Slice = slice; Index = index; Char = c; NoBreak = noBreak;
        }
    }

    internal static bool Find(IReadOnlyList<BreakSlice> slices, int budget, out BreakPoint point)
    {
        point = default;
        var glyphs = Visible(slices);
        int top = Math.Min(budget, glyphs.Count - 1);
        if (top < 1) return false;

        for (int k = top; k >= Math.Max(1, budget * 3 / 5); k--)
        {
            if (Inside(glyphs, k) || glyphs[k].NoBreak || glyphs[k - 1].NoBreak) continue;
            char c = glyphs[k].Char;
            if (c == ' ' || (PhraseEnders.IndexOf(glyphs[k - 1].Char) >= 0 && TextWrap.Closing.IndexOf(c) < 0))
            {
                point = At(glyphs[k]);
                return true;
            }
        }

        for (int k = top; k >= 1; k--)
        {
            if (Inside(glyphs, k)) continue;
            Glyph g = glyphs[k];
            if (g.Char == ' ' && !g.NoBreak)
            {
                point = At(g);
                return true;
            }
            if (CanBreakBetween(glyphs[k - 1].Char, g.Char))
            {
                point = At(g);
                return true;
            }
        }
        return false;
    }

    private static bool CanBreakBetween(char before, char after)
    {
        if (before == ' ') return false;    // 这个位置由前一轮的空格分支处理
        if (TextWrap.Closing.IndexOf(after) >= 0 || TextWrap.Opening.IndexOf(before) >= 0) return false;
        if (before < 128 && after < 128) return false;
        return !(TextWrap.IsWordChar(before) && TextWrap.IsWordChar(after));
    }

    /// <summary>k 落在同一个不折行片段的中间。</summary>
    private static bool Inside(List<Glyph> glyphs, int k)
        => glyphs[k].NoBreak && glyphs[k - 1].NoBreak && glyphs[k].Slice == glyphs[k - 1].Slice;

    private static BreakPoint At(Glyph g) => new(g.Slice, g.Index, g.Char == ' ');

    private static List<Glyph> Visible(IReadOnlyList<BreakSlice> slices)
    {
        var glyphs = new List<Glyph>();
        for (int i = 0; i < slices.Count; i++)
        {
            BreakSlice s = slices[i];
            if (s.NewLine) continue;
            bool tag = false;
            for (int j = 0; j < s.Text.Length; j++)
            {
                char c = s.Text[j];
                if (c == '<') tag = true;
                else if (c == '>') tag = false;
                else if (!tag && !char.IsControl(c)) glyphs.Add(new Glyph(i, j, c, s.NoBreak));
            }
        }
        return glyphs;
    }
}
