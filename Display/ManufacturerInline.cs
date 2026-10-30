using System;
using System.Text;
using HarmonyLib;

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

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(display, postfix: new HarmonyMethod(typeof(ManufacturerInline), nameof(AfterDisplay)));
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

    private static RichText? Inline(RichTextBuilder builder, RichText r, string company)
    {
        string[] fg = r.richTextForeground.Split('\n');
        string[] bg = (r.richTextBackground ?? "").Split('\n');
        if (fg.Length != bg.Length) return null;

        int c = Array.FindIndex(fg, line => string.Equals(Visible(line), company, StringComparison.Ordinal));
        if (c < 1 || c + 1 >= fg.Length) return null;
        if (VisibleLength(fg[c - 1]) + Gap + company.Length > WrapWidth(builder)) return null;

        fg[c - 1] += "<alpha=#00>" + new string(' ', Gap) + company + "<alpha=#FF><line-height=0>";
        fg[c] = "</line-height><align=right>" + fg[c];
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
