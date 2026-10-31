using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 把物品名下面单独一行的生产公司并到名称行，靠右显示。
///
/// 原版 <c>GameItem.DisplayManufacturer</c> 先 <c>Add(" ")</c> 再 <c>AddLine(公司名)</c>。
/// 这里让原版照常生成带正确颜色和本地化的公司行，只在 <c>RichTextBuilder.Build</c> 后：
///   1. 按本次 DisplayManufacturer 的公司文本找到那一行；
///   2. 用 TMP 的 <c>&lt;line-height=0&gt;</c> 让它与上一行叠在同一行，再 <c>&lt;align=right&gt;</c> 靠右；
///   3. 名称行末尾补全透明占位，保证宽度至少是「名称 + 间隔 + 公司名」。
///
/// 背景层与前景层逐行对应，两层必须同时收起一行。放不下、公司行是最后一行、
/// 或结构对不上时直接保留原版输出。全程只调用公开 API，不读 TextSlice 私有字段。
/// </summary>
internal static class ManufacturerInline
{
    private const string HarmonyId = "item_details.manufacturer_inline";
    private const int Gap = 2;

    private static Harmony? _harmony;
    private static bool _patched;
    private static RichTextBuilder? _pendingBuilder;
    private static string _pendingCompany = "";

    // 右对齐对齐的是文本节点宽度，而该节点比内框多出左右内边距（tooltipElement.SetPadding(8,8,8,8)）。
    // 用 TMP 的右边距把靠右文字收回内框：右对齐只受 margin.z 影响，左对齐行起点不变。
    // 边距必须在**每次 SetRichText 时按内容重设**（合并过的设 16，其它一律清零），
    // 否则会残留到共用同一个 tooltipElement 的其它提示框，看起来像「所有提示框都多了内边距」。
    // Validate/Show 只重设 sizeDelta，不会清 margin，所以在 SetRichText 后设是有效的。
    private static FieldInfo? _textNodeField;
    private static FieldInfo? _leftPadField;
    private static FieldInfo? _rightPadField;
    private static PropertyInfo? _textProp;

    internal static void Apply()
    {
        if (_patched) return;
        try
        {
            var display = AccessTools.Method(typeof(GameItem), "DisplayManufacturer",
                new[] { typeof(RichTextBuilder), typeof(string) });
            var build = AccessTools.Method(typeof(RichTextBuilder), "Build", new[] { typeof(int) });
            if (display == null || build == null)
            {
                Log.Warn("ManufacturerInline.Apply", "未找到 DisplayManufacturer/Build，生产公司保持单独一行");
                return;
            }

            var setRich = AccessTools.Method(typeof(RichTextElement), "SetRichText",
                new[] { typeof(RichText), typeof(int) });
            _textNodeField = AccessTools.Field(typeof(RichTextElement), "textNode");
            _leftPadField = AccessTools.Field(typeof(RichTextElement), "leftPadding");
            _rightPadField = AccessTools.Field(typeof(RichTextElement), "rightPadding");
            _textProp = AccessTools.Property(typeof(TreeNodeRender), "text");

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(display, postfix: new HarmonyMethod(typeof(ManufacturerInline), nameof(AfterDisplay)));
            _harmony.Patch(build, postfix: new HarmonyMethod(typeof(ManufacturerInline), nameof(AfterBuild)));
            bool ok = setRich != null && _textNodeField != null && _leftPadField != null
                      && _rightPadField != null && _textProp != null;
            if (ok) _harmony.Patch(setRich, postfix: new HarmonyMethod(typeof(ManufacturerInline), nameof(AfterSetRichText)));
            else Log.Warn("ManufacturerInline.Margin", "未找到 SetRichText/边距字段，公司名右对齐可能贴边");
            _patched = true;
            Log.Msg("ManufacturerInline patched GameItem.DisplayManufacturer/RichTextBuilder.Build");
        }
        catch (Exception ex)
        {
            Log.Error("ManufacturerInline.Apply", ex);
        }
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _patched = false;
        _pendingBuilder = null;
        _pendingCompany = "";
    }

    /// <summary>记录本次原版公司行的文本；不改写 builder，保留原版所有颜色与分支。</summary>
    private static void AfterDisplay(RichTextBuilder __0, string manufacturer)
    {
        _pendingBuilder = null;
        _pendingCompany = "";
        if (!ModSettings.ManufacturerInline || string.IsNullOrEmpty(manufacturer)) return;
        try
        {
            string company;
            if (manufacturer == "georgio")
                company = LocHelper.GetLocalizedClientName("name_georgio");
            else if (manufacturer == "harvey_bee")
                company = LocHelper.GetLocalizedOrDefault("ClientName", "corpo_name_harvey_bee", "M. Harvey Bee");
            else
                company = LocHelper.GetLocalizedClientName("corpo_name_" + manufacturer);
            if (string.IsNullOrEmpty(company)) return;
            _pendingBuilder = __0;
            _pendingCompany = company;
        }
        catch { }
    }

    private static void AfterBuild(RichTextBuilder __instance, ref RichText __result)
    {
        var r = __result;
        string company = ReferenceEquals(_pendingBuilder, __instance) ? _pendingCompany : "";
        _pendingBuilder = null;
        _pendingCompany = "";
        if (r?.richTextForeground == null || company.Length == 0) return;
        try
        {
            // 结构不匹配时 Inline 返回 null，原始 RichText 原样保留。
            __result = Inline(__instance, r, company) ?? r;
        }
        catch (Exception ex)
        {
            Log.Warn("ManufacturerInline.Build", ex);
            __result = r;
        }
    }

    /// <summary>
    /// 每次设置文本后重设右边距：合并过的提示框用「左右内边距之和」把靠右的公司名收回内框，
    /// 其它提示框一律清零。这样切换物品时不会把上一次的边距残留下来。
    /// </summary>
    private static void AfterSetRichText(RichTextElement __instance, RichText richText)
    {
        try
        {
            object? node = _textNodeField?.GetValue(__instance);
            if (node == null || _textProp!.GetValue(node) is not TMP_Text tmp) return;

            string fg = richText?.richTextForeground ?? "";
            bool ours = fg.IndexOf("<align=right>", StringComparison.Ordinal) >= 0
                        && fg.IndexOf("<line-height=0>", StringComparison.Ordinal) >= 0;
            float right = 0f;
            if (ours)
                right = (int)(_leftPadField!.GetValue(__instance) ?? 0) + (int)(_rightPadField!.GetValue(__instance) ?? 0);

            Vector4 margin = tmp.margin;
            if (margin.x != 0f || margin.y != 0f || margin.w != 0f || margin.z != right)
                tmp.margin = new Vector4(0f, 0f, right, 0f);
        }
        catch (Exception ex) { Log.Warn("ManufacturerInline.Margin", ex); }
    }

    private static RichText? Inline(RichTextBuilder builder, RichText r, string company)
    {
        string[] fg = r.richTextForeground.Split('\n');
        string[] bg = (r.richTextBackground ?? "").Split('\n');
        if (fg.Length != bg.Length) return null;

        // 原版公司行前面带一个空格（DisplayManufacturer 先 Add(" ")），比较时要去掉。
        int c = Array.FindIndex(fg, line => string.Equals(Visible(line).Trim(), company, StringComparison.Ordinal));
        if (c < 1 || c + 1 >= fg.Length) return null;
        // 与 Build 的折行同一口径：一个字符算 1（中日韩的字宽已由 WrapWidth 的 0.66 折算），
        // 不能按全角算 2，否则中文物品名几乎都会被判成放不下。
        if (VisibleLength(fg[c - 1]) + Gap + company.Length > WrapWidth(builder)) return null;

        // 名称行末尾补一段全透明的「间隔 + 公司名」，保证本行至少有这么宽，靠右的公司名不会压住名称。
        string companyLine = fg[c].TrimStart(' ');
        fg[c - 1] += "<alpha=#00>" + new string(' ', Gap) + company + "<alpha=#FF><line-height=0>";
        fg[c] = "</line-height><align=right>" + companyLine;
        fg[c + 1] = "</align>" + fg[c + 1];
        bg[c - 1] += "<line-height=0>";
        bg[c] = "</line-height>" + bg[c];

        return Rebuild(r, string.Join("\n", fg), string.Join("\n", bg));
    }

    private static RichText Rebuild(RichText r, string foreground, string background)
    {
        // 原版 nakedText 与前景是同一串；推荐宽度按原版口径（含标签字符）重算。
        int width = 0;
        foreach (var line in foreground.Split('\n'))
        {
            int n = 0;
            foreach (char ch in line) if (!char.IsControl(ch)) n++;
            if (n > width) width = n;
        }
        return new RichText(foreground, r.richTextStrikethrough, r.richTextUnderline, foreground, background,
            width, r.recommendedHeight, r.isTruncated);
    }

    /// <summary>与 RichTextBuilder.Build 相同的折行宽度；无法确认 noBreak 时采用保守值。</summary>
    private static int WrapWidth(RichTextBuilder builder)
    {
        float scale = UISettings.current != null ? UISettings.current.GetCharWidthScale() : 1f;
        if (scale <= 1f) return builder.maxCharWidth;
        return Math.Max(1, (int)(builder.maxCharWidth * 0.66f));
    }

    private static int VisibleLength(string text) => Visible(text).Length;

    /// <summary>跳过富文本标签与控制字符，按显示文本匹配公司行。</summary>
    private static string Visible(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inTag = false;
        foreach (char ch in text)
        {
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>') { inTag = false; continue; }
            if (!inTag && !char.IsControl(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }
}
