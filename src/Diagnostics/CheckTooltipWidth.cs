#if ITEMDETAILS_SELFTEST
using System;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 框宽修正的自检：确认 Harmony 已挂上 RichText.recommendedWidth，
/// 且可见宽度按「最长一行去掉标签后的字符数」计算。
/// </summary>
internal static class CheckTooltipWidth
{
    internal static string Run(out bool ok)
    {
        ok = false;
        try
        {
            var build = AccessTools.Method(typeof(RichTextBuilder), nameof(RichTextBuilder.Build));
            if (build == null) return "tooltip width: 未找到 RichTextBuilder.Build";
            var info = Harmony.GetPatchInfo(build);
            bool patched = false;
            if (info?.Postfixes != null)
                foreach (var patch in info.Postfixes)
                    // 全部补丁共用 PatchScope 的单一所有者，不能再按旧的分类所有者字符串查找。
                    if (patch.owner == PatchScope.HarmonyId) { patched = true; break; }

            // 「水<pos=5.9em>196.37 mL<pos=11.67em>78.55%」：原始 40 字符，去掉标签后可见 16 字符。
            const string sample = "水<pos=5.9em>196.37 mL<pos=11.67em>78.55%";
            int visible = TooltipWidthFix.VisibleWidth(sample);
            int raw = sample.Length;
            bool shrunk = visible == 16 && visible < raw;

            // 文案里的字面尖括号（水培机「不足 1 天」）不是标签：它后面的文字必须照常计入，
            // 否则整段 tooltip 会被当成标签吃掉、框宽骤减。
            const string literal = "Water: 4L (1L/day), <1 day";
            int literalWidth = TooltipWidthFix.VisibleWidth(literal);
            bool kept = literalWidth == literal.Length;

            ok = patched && shrunk && kept;
            return $"tooltip width: patched={patched} visible={visible} raw={raw} " +
                   $"literal={literalWidth}/{literal.Length} " +
                   "(期望 patched=True、visible=16 < raw=40、literal 等于原长)";
        }
        catch (Exception ex) { return "tooltip width: " + ex; }
    }
}
#endif
