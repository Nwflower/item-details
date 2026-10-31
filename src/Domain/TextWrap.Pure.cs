using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 描述文字预折行：按与 RichTextBuilder.Build 相同的口径（一个字符算 1）把长段落切成不超过
/// limit 的行。原版只要 tooltip 里有进度条（noBreak 片段），就把整段的折行宽度从 0.66 放宽到 0.875，
/// 描述因此被拉成一长行；提前按 0.66 的宽度断好，原版就不会再拉宽它。
///
/// 规则：
///   - 原有换行保留，逐段处理；
///   - 不拆英文单词、数字和以空格分词的文字（韩文）：断点落在词中就退到词首；
///   - 避头尾：收尾标点不放行首，开头标点不放行尾；
///   - 退让不超过半行，退不动就在 limit 处硬断，保证每行都不超过 limit。
/// </summary>
internal static class TextWrap
{
    private const string Closing = "，。、；：！？）」』】》〉”’…·%,.;:!?)]}";
    private const string Opening = "（「『【《〈“‘([{";

    internal static string Wrap(string text, int limit)
    {
        if (string.IsNullOrEmpty(text) || limit < 2) return text;

        var lines = new List<string>();
        foreach (string segment in text.Split('\n'))
        {
            string s = segment;
            bool first = true;
            while (s.Length > limit)
            {
                int cut = BreakAt(s, limit);
                lines.Add(first ? s.Substring(0, cut).TrimEnd(' ') : s.Substring(0, cut).Trim(' '));
                s = s.Substring(cut).TrimStart(' ');
                first = false;
            }
            lines.Add(s);
        }
        return string.Join("\n", lines);
    }

    /// <summary>s 比 limit 长时的断点：返回本行长度（1..limit）。</summary>
    private static int BreakAt(string s, int limit)
    {
        int floor = Math.Max(1, limit / 2);
        int cut = limit;

        // 断在词中间：退到词首（词首前的空格留在上一行末尾，随后被裁掉）
        if (IsWordChar(s[cut - 1]) && IsWordChar(s[cut]))
        {
            int start = cut;
            while (start > 0 && IsWordChar(s[start - 1])) start--;
            if (start >= floor) cut = start;
        }

        // 避头尾：下一行不能以收尾标点开头，本行不能以开头标点结尾
        while (cut > floor && cut < s.Length && Closing.IndexOf(s[cut]) >= 0) cut--;
        while (cut > floor && Opening.IndexOf(s[cut - 1]) >= 0) cut--;
        return cut;
    }

    /// <summary>以空格分词的文字：拉丁字母、数字、韩文等；汉字和假名可以任意断开。</summary>
    private static bool IsWordChar(char c)
    {
        if (c == '%' || c == '\'' || c == '-' || c == '.') return true;
        if (!char.IsLetterOrDigit(c)) return false;
        bool cjk = (c >= 0x3040 && c <= 0x30FF)    // 平假名、片假名
                   || (c >= 0x3400 && c <= 0x9FFF) // 汉字
                   || (c >= 0xF900 && c <= 0xFAFF);
        return !cjk;
    }
}
