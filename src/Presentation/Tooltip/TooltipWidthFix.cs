using System;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 修正 tooltip 框宽：RichTextBuilder.Build 用「原始字符数」算框宽
/// （RichTextBuilder.cs:622：text.Split('\n').Max(s => s.Count(c => !char.IsControl(c)))），
/// 把 TMP 的富文本标签（&lt;pos=…&gt;、&lt;color=…&gt;、&lt;sprite…&gt;）也算进去；
/// 而 TMP 渲染这些标签是零宽的。于是标签越多、框越宽：三列用 &lt;pos&gt; 定位的成分表
/// 每行多出两个 12 字符的标签，框被撑大一大截，右侧留白就是这么来的。
///
/// 这里只改「框宽」的统计口径，不动折行：折行由 maxCharWidth×0.66 与 GetLength()
/// （本身已跳过标签）决定，保持原版行为。
/// 新宽 = 最长一行去掉标签后的可见字符数，即玩家实际看到的字符数。
///
/// 打 Build 的 postfix 而不是 get_recommendedWidth 的 postfix：后者只改返回值，
/// RichText.GetHashCode() 也读这个值，会让缓存比较带上修正后的宽度；改源头更干净。
/// </summary>
internal static class TooltipWidthFix
{

    /// <summary>RichText.recommendedWidth 的 backing field；postfix 里直接改写。</summary>
    private static System.Reflection.FieldInfo? _widthField;

    internal static void Install(PatchScope scope)
    {
        try
        {
            var build = AccessTools.Method(typeof(RichTextBuilder), nameof(RichTextBuilder.Build));
            if (build == null)
            {
                Log.Warn("TooltipWidthFix", "未找到 RichTextBuilder.Build，框宽修正跳过");
                return;
            }
            _widthField = AccessTools.Field(typeof(RichText), "<recommendedWidth>k__BackingField");
            if (_widthField == null)
            {
                Log.Warn("TooltipWidthFix", "未找到 recommendedWidth 的 backing field，框宽修正跳过");
                return;
            }
            scope.Patch(build, postfix: new HarmonyMethod(typeof(TooltipWidthFix), nameof(FixWidth)));
            Log.Msg("TooltipWidthFix: patched RichTextBuilder.Build");
        }
        catch (Exception ex)
        {
            Log.Error("TooltipWidthFix.Apply", ex);
        }
    }

    /// <summary>把 RichText 的框宽换成「最长一行去掉富文本标签后的可见字符数」。</summary>
    private static void FixWidth(RichText __result)
    {
        try
        {
            if (__result == null || _widthField == null) return;
            int visible = Visible(__result);
            if (visible <= 0) return;
            int current = (int)_widthField.GetValue(__result)!;
            // 只下调不上调：标签算多了才会偏宽，其它界面保持原版行为。
            if (visible < current) _widthField.SetValue(__result, visible);
        }
        catch (Exception ex) { Log.Warn("TooltipWidthFix.FixWidth", ex); }
    }

    /// <summary>五路文本分别取可见宽度后取最大值：它们由 RichTextElement 分别渲染，框要同时容纳。</summary>
    private static int Visible(RichText text)
    {
        return Math.Max(
            Math.Max(VisibleWidth(text.richTextForeground), VisibleWidth(text.richTextBackground)),
            Math.Max(VisibleWidth(text.nakedText),
                Math.Max(VisibleWidth(text.richTextUnderline), VisibleWidth(text.richTextStrikethrough))));
    }

    /// <summary>
    /// <c>&lt;</c> 处若是富文本标签，返回其 <c>&gt;</c> 的下标，否则返回 -1。
    /// 只有「&lt; 后面跟字母或 /，且同一行内出现 &gt;」才算标签：文案里本来就有字面尖括号
    /// （水培机的「不足 1 天」在各语言写作 <c>&lt;1 day</c>），旧写法会把 &lt; 之后的整段文本
    /// ——包括以下所有行——都当标签吃掉，框宽只剩它前面的最长行。
    /// </summary>
    private static int TagEnd(string text, int start)
    {
        int i = start + 1;
        if (i >= text.Length) return -1;
        char first = text[i];
        if (!char.IsLetter(first) && first != '/') return -1;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '>') return i;
            if (c == '\n') return -1;
        }
        return -1;
    }

    /// <summary>逐行去掉 &lt;…&gt; 标签后取最大的可见字符数（控制字符不计）。</summary>
    internal static int VisibleWidth(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int longest = 0, current = 0;
        for (int i = 0; i < text!.Length; i++)
        {
            char c = text[i];
            if (c == '<')
            {
                int end = TagEnd(text, i);
                if (end >= 0) { i = end; continue; }   // 标签整体跳过；不是标签就按普通字符计
            }
            if (c == '\n') { if (current > longest) longest = current; current = 0; continue; }
            if (!char.IsControl(c)) current++;
        }
        return current > longest ? current : longest;
    }
}
