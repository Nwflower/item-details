#if ITEMDETAILS_SELFTEST
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ItemDetails;

/// <summary>
/// 把行级快照渲染成 HTML 报告，便于在浏览器里检查我们追加的 tooltip 文本与配色，
/// 不必进游戏悬停。
///
/// 输入：物品 + 若干行富文本（形如 `<color=#42A2AA>…</color>`）。
/// 输出：深色背景的「模拟 tooltip」卡片，颜色标签转成 `<span style>`。
///
/// 注意：这里只包含**本模组追加的行**；原版行需要 element 实例才能取到，未纳入。
/// </summary>
internal static class TooltipReport
{
    internal sealed class Entry
    {
        internal string Id = "";
        internal string Name = "";
        internal readonly List<string> Lines = new();
        /// <summary>无法渲染时的说明（例如物品缺少初始化标签）。</summary>
        internal string Note = "";
    }

    private static readonly Regex ColorTag =
        new(@"<color=#([0-9A-Fa-f]{6,8})>", RegexOptions.Compiled);

    internal static string BuildHtml(IEnumerable<Entry> entries, string title)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"zh\"><head><meta charset=\"utf-8\">");
        sb.Append("<title>").Append(Esc(title)).Append("</title>");
        sb.Append(@"<style>
body{background:#1b1b22;color:#d8d8d8;font-family:'Segoe UI',system-ui,sans-serif;margin:24px;}
h1{font-size:18px;font-weight:600;color:#e8e8e8;margin:0 0 4px;}
.sub{color:#8a8a96;font-size:12px;margin-bottom:18px;}
.grid{display:flex;flex-wrap:wrap;gap:16px;}
.card{background:#2b2b36;border:1px solid #3d3d4a;border-radius:8px;padding:10px 12px;min-width:320px;max-width:460px;}
.card h2{font-size:13px;margin:0 0 8px;color:#b9b9c6;font-weight:600;}
.tip{background:#241f2b;border:1px solid #4a3d55;border-radius:4px;padding:8px;}
.tip div{font-size:12.5px;line-height:1.55;white-space:pre-wrap;}
.note{color:#c48a8a;font-size:12px;font-style:italic;}
</style></head><body>");
        sb.Append("<h1>").Append(Esc(title)).Append("</h1>");
        sb.Append("<div class=\"sub\">本模组追加的 tooltip 行（不含原版行）· 由实机自检生成</div>");
        sb.Append("<div class=\"grid\">");

        foreach (var e in entries)
        {
            sb.Append("<div class=\"card\"><h2>").Append(Esc(e.Id));
            if (!string.IsNullOrEmpty(e.Name))
                sb.Append(" · ").Append(Esc(e.Name));
            sb.Append("</h2><div class=\"tip\">");

            if (!string.IsNullOrEmpty(e.Note))
                sb.Append("<div class=\"note\">").Append(Esc(e.Note)).Append("</div>");

            foreach (string raw in e.Lines)
            {
                sb.Append("<div>").Append(RichToHtml(raw)).Append("</div>");
            }
            sb.Append("</div></div>");
        }

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>把游戏富文本（仅 color 标签）转成 HTML，其余内容做转义。</summary>
    private static string RichToHtml(string? rich)
    {
        if (string.IsNullOrEmpty(rich)) return "";

        // 先把闭合标签换成占位符：否则会被 Esc 转义成 &lt;/color&gt;，替换就失效了
        const char CloseMark = '\u0001';
        string src = rich.Replace("</color>", CloseMark.ToString());

        var sb = new StringBuilder(src.Length + 32);
        int last = 0;
        foreach (Match m in ColorTag.Matches(src))
        {
            sb.Append(Esc(src.Substring(last, m.Index - last)));
            string hex = m.Groups[1].Value;
            if (hex.Length == 8) hex = hex.Substring(0, 6);   // 去掉 alpha
            sb.Append("<span style=\"color:#").Append(hex).Append("\">");
            last = m.Index + m.Length;
        }
        sb.Append(Esc(src.Substring(last)));
        return sb.ToString().Replace(CloseMark.ToString(), "</span>");
    }

    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    /// <summary>写入 ModData 目录，返回绝对路径（失败返回 null）。</summary>
    internal static string? Write(string modId, string fileName, string html)
    {
        try
        {
            ModHelper.WriteModDataFileText(modId, fileName, html);
            string folder = ModHelper.GetModDataFolder(modId);
            return System.IO.Path.Combine(folder, fileName);
        }
        catch (Exception ex)
        {
            Log.Warn("TooltipReport.Write", ex);
            return null;
        }
    }
}
#endif
