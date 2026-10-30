using System;
using System.Collections;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 把物品名下面单独一行的生产公司并到名称行，靠右显示。
///
/// 原版 <c>GameItem.DisplayManufacturer</c> 先 <c>Add(" ")</c> 再 <c>AddLine(公司名)</c>，于是公司名单独成行、
/// 前面空一格。这里分两步：
///   1. DisplayManufacturer 结束后，在 builder 里去掉那一格空白，并在公司名前放一个私用区哨兵字符；
///   2. RichTextBuilder.Build 之后，按哨兵找到公司行，用 TMP 的 <c>&lt;line-height=0&gt;</c> 让它与上一行
///      （名称的最后一行）叠在同一行，再 <c>&lt;align=right&gt;</c> 靠右。
///
/// tooltip 宽度取前景文本的 preferredWidth，名称行比其它行都宽时，靠右的公司名会压在名称上；
/// 所以名称行末尾补一段全透明的「空格 + 公司名」占位，保证宽度至少是「名称 + 间隔 + 公司名」。
/// 背景层与前景层逐行对应（违禁品提示行带黑底），两层必须同时收起一行，否则下面的底色会错位。
///
/// 放不下（名称 + 间隔 + 公司名超过原版折行宽度）、公司行是最后一行、或结构对不上时，
/// 去掉哨兵、补回行首空格，保留原版的单独一行。
/// </summary>
internal static class ManufacturerInline
{
    private const string HarmonyId = "item_details.manufacturer_inline";
    private const char Mark = '';
    private const int Gap = 2;

    private static Harmony? _harmony;
    private static bool _patched;

    private static FieldInfo? _slices;
    private static FieldInfo? _text;
    private static FieldInfo? _isNewLine;
    private static FieldInfo? _noBreak;
    private static FieldInfo? _length;

    internal static void Apply()
    {
        if (_patched) return;
        try
        {
            var display = AccessTools.Method(typeof(GameItem), "DisplayManufacturer",
                new[] { typeof(RichTextBuilder), typeof(string) });
            var build = AccessTools.Method(typeof(RichTextBuilder), "Build", new[] { typeof(int) });
            var slice = AccessTools.Inner(typeof(RichTextBuilder), "TextSlice");
            _slices = AccessTools.Field(typeof(RichTextBuilder), "slices");
            _text = slice == null ? null : AccessTools.Field(slice, "text");
            _isNewLine = slice == null ? null : AccessTools.Field(slice, "isNewLine");
            _noBreak = slice == null ? null : AccessTools.Field(slice, "noBreak");
            _length = slice == null ? null : AccessTools.Field(slice, "length");
            if (display == null || build == null || _slices == null || _text == null || _isNewLine == null)
            {
                Log.Warn("ManufacturerInline.Apply", "未找到 DisplayManufacturer/Build/TextSlice 字段，生产公司保持单独一行");
                return;
            }

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(display,
                prefix: new HarmonyMethod(typeof(ManufacturerInline), nameof(BeforeDisplay)),
                postfix: new HarmonyMethod(typeof(ManufacturerInline), nameof(AfterDisplay)));
            _harmony.Patch(build, postfix: new HarmonyMethod(typeof(ManufacturerInline), nameof(AfterBuild)));
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
    }

    private static void BeforeDisplay(RichTextBuilder __0, out int __state)
    {
        __state = -1;
        if (!ModSettings.ManufacturerInline) return;
        try
        {
            if (_slices!.GetValue(__0) is IList slices) __state = slices.Count;
        }
        catch { }
    }

    /// <summary>
    /// 原版这一步追加的是「换行、" "、公司名」三段；前面紧挨着名称。只有完全对得上才动。
    /// </summary>
    private static void AfterDisplay(RichTextBuilder __0, int __state)
    {
        if (__state < 1) return;
        try
        {
            if (_slices!.GetValue(__0) is not IList slices || slices.Count != __state + 3) return;
            object name = slices[__state - 1], newLine = slices[__state], space = slices[__state + 1], company = slices[__state + 2];
            if (IsNewLine(name) || !IsNewLine(newLine) || Text(space) != " " || IsNewLine(company)) return;
            string text = Text(company);
            if (string.IsNullOrEmpty(text)) return;

            slices.RemoveAt(__state + 1);
            _text!.SetValue(company, Mark + text);
            _length?.SetValue(company, -1);
        }
        catch (Exception ex)
        {
            Log.Warn("ManufacturerInline.Mark", ex);
        }
    }

    private static void AfterBuild(RichTextBuilder __instance, ref RichText __result)
    {
        var r = __result;
        if (r?.richTextForeground == null || r.richTextForeground.IndexOf(Mark) < 0) return;
        try
        {
            __result = Inline(__instance, r) ?? Separate(r);
        }
        catch (Exception ex)
        {
            Log.Warn("ManufacturerInline.Build", ex);
            __result = Separate(r);
        }
    }

    /// <summary>不并行：去掉哨兵，补回原版行首那一格（哨兵在背景层本来就占了一格空白）。</summary>
    private static RichText Separate(RichText r)
    {
        string[] fg = r.richTextForeground.Split('\n');
        for (int i = 0; i < fg.Length; i++)
            if (fg[i].IndexOf(Mark) >= 0) fg[i] = " " + Unmark(fg[i]);
        return Rebuild(r, string.Join("\n", fg), r.richTextBackground);
    }

    private static RichText? Inline(RichTextBuilder builder, RichText r)
    {
        string[] fg = r.richTextForeground.Split('\n');
        string[] bg = (r.richTextBackground ?? "").Split('\n');
        if (fg.Length != bg.Length) return null;

        int c = Array.FindIndex(fg, line => line.IndexOf(Mark) >= 0);
        if (c < 1 || c + 1 >= fg.Length) return null;

        string company = Unmark(fg[c]);
        string plain = Visible(company);
        if (plain.Length == 0) return null;
        if (VisibleLength(fg[c - 1]) + Gap + plain.Length > WrapWidth(builder)) return null;

        fg[c - 1] += "<alpha=#00>" + new string(' ', Gap) + plain + "<alpha=#FF><line-height=0>";
        fg[c] = "</line-height><align=right>" + company;
        fg[c + 1] = "</align>" + fg[c + 1];
        bg[c - 1] += "<line-height=0>";
        bg[c] = "</line-height>" + bg[c];

        return Rebuild(r, string.Join("\n", fg), string.Join("\n", bg));
    }

    private static RichText Rebuild(RichText r, string foreground, string background)
    {
        // 原版 nakedText 与前景是同一串；推荐宽度按原版口径（含标签字符）重算
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

    /// <summary>与 RichTextBuilder.Build 相同的折行宽度。</summary>
    private static int WrapWidth(RichTextBuilder builder)
    {
        float scale = UISettings.current != null ? UISettings.current.GetCharWidthScale() : 1f;
        if (scale <= 1f) return builder.maxCharWidth;

        bool noBreak = false;
        if (_noBreak != null && _slices!.GetValue(builder) is IList slices)
            foreach (var s in slices)
                if (s != null && _noBreak.GetValue(s) is true) { noBreak = true; break; }
        return Math.Max(1, (int)(builder.maxCharWidth * (noBreak ? 0.875f : 0.66f)));
    }

    /// <summary>与 TextSlice.GetLength 同口径：跳过 &lt;…&gt; 标签与控制字符。</summary>
    private static int VisibleLength(string text) => Visible(text).Length;

    private static string Visible(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inTag = false;
        foreach (char ch in text)
        {
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>') { inTag = false; continue; }
            if (!inTag && !char.IsControl(ch) && ch != Mark) sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string Unmark(string text) => text.Replace(Mark.ToString(), "");

    private static bool IsNewLine(object slice) => _isNewLine!.GetValue(slice) is true;

    private static string Text(object slice) => _text!.GetValue(slice) as string ?? "";
}
