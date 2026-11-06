using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 逻辑层与渲染层分离的进度条实现：
/// - 逻辑层：条一律由游戏自己的 <c>AddProgressBar</c> 写出，原版 `[=====     ]` 纯文本、字符宽度不变；
///   同一个入口（<see cref="BeforeBar"/>）记录高精度比例与底色，原版会丢掉背景色；
/// - 第三方层：美化排版 Mod 从 `<c>AddProgressBar</c>` 读比例、从文本切分时看到的是标准原版字符，
///   提取与列宽计算不会被 TMP sprite 标签破坏；
/// - 渲染层（RichTextElement.SetRichText）：在实际向屏幕 TextMeshPro 提交富文本的前夕，
///   统一匹配 `[=====     ]` 并根据记录的高精度浮点数替换为高清 SpriteProgressBar（或回退为 █ 块）。
/// </summary>
internal static class ProgressBarStyle
{
    private const string HarmonyId = "item_details.progress_bar";
    private const char Block = '█';
    private const int MaxLength = 40;
    private const string DimAlpha = "<alpha=#40>";
    private static readonly Regex BarRegex = new Regex(@"\[([= ]{2,40})\]", RegexOptions.Compiled);

    private static Harmony? _harmony;
    private static bool _patched;

    private static FieldInfo? _textNodeField;
    private static PropertyInfo? _textProp;

    internal readonly struct BarRecord
    {
        internal readonly float Ratio;
        internal readonly int Length;
        internal readonly RenderHandler.ColorPalette Dark;
        internal readonly bool FullLength;

        internal BarRecord(float ratio, int length, RenderHandler.ColorPalette dark, bool fullLength = true)
        {
            Ratio = ratio;
            Length = length;
            Dark = dark;
            FullLength = fullLength;
        }
    }

    private static readonly ConditionalWeakTable<RichTextBuilder, List<BarRecord>> BuilderBars = new();
    private static readonly ConditionalWeakTable<RichText, List<BarRecord>> RichTextBars = new();

    internal static void Apply()
    {
        if (_patched) return;
        try
        {
            var bar = AccessTools.Method(typeof(RichTextBuilder), "AddProgressBar", new[]
            {
                typeof(float), typeof(int), typeof(RenderHandler.ColorPalette), typeof(RenderHandler.ColorPalette),
                typeof(char[]), typeof(string), typeof(bool), typeof(bool)
            });
            var addBuilder = AccessTools.Method(typeof(RichTextBuilder), "Add", new[] { typeof(RichTextBuilder) });
            var clear = AccessTools.Method(typeof(RichTextBuilder), "Clear", Type.EmptyTypes);
            var build = AccessTools.Method(typeof(RichTextBuilder), "Build", new[] { typeof(int) });
            var setRich = AccessTools.Method(typeof(RichTextElement), "SetRichText",
                new[] { typeof(RichText), typeof(int) });

            if (bar == null || build == null || setRich == null)
            {
                Log.Warn("ProgressBar.Apply", "未找到 AddProgressBar/Build/SetRichText，进度条保持原版");
                return;
            }

            _textNodeField = AccessTools.Field(typeof(RichTextElement), "textNode");
            _textProp = AccessTools.Property(typeof(TreeNodeRender), "text");

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(bar, prefix: new HarmonyMethod(typeof(ProgressBarStyle), nameof(BeforeBar)));
            if (addBuilder != null)
                _harmony.Patch(addBuilder, postfix: new HarmonyMethod(typeof(ProgressBarStyle), nameof(AfterAddBuilder)));
            if (clear != null)
                _harmony.Patch(clear, postfix: new HarmonyMethod(typeof(ProgressBarStyle), nameof(AfterClear)));
            // 预算收窄只靠计数：并入与清空都要能跟上，否则宁可保持原版放宽。
            bool canNarrow = addBuilder != null && clear != null;
            if (!canNarrow) Log.Warn("ProgressBar.Budget", "未找到 Add(builder)/Clear，含进度条的 tooltip 按原版 0.875 折行");
            // 另外两个也会写不折行片段的原版入口：只计数，不记录比例。
            foreach (string other in new[] { "AddProgressBarOld", "AddProgressTest" })
            {
                var method = AccessTools.Method(typeof(RichTextBuilder), other);
                if (method != null)
                    _harmony.Patch(method, prefix: new HarmonyMethod(typeof(ProgressBarStyle), nameof(CountBar)));
            }
            _harmony.Patch(build,
                prefix: canNarrow ? new HarmonyMethod(typeof(ProgressBarStyle), nameof(BeforeBuild)) : null,
                postfix: new HarmonyMethod(typeof(ProgressBarStyle), nameof(AfterBuild)),
                finalizer: canNarrow ? new HarmonyMethod(typeof(ProgressBarStyle), nameof(RestoreWidth)) : null);
            _harmony.Patch(setRich, prefix: new HarmonyMethod(typeof(ProgressBarStyle), nameof(BeforeSetRichText)));

            _patched = true;
            Log.Msg("ProgressBarStyle: hooked AddProgressBar/Build/SetRichText for decoupled bar rendering");
        }
        catch (Exception ex)
        {
            Log.Error("ProgressBar.Apply", ex);
        }
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _patched = false;
    }

    internal static void RecordBar(RichTextBuilder builder, float ratio, int length, RenderHandler.ColorPalette dark, bool fullLength = true)
    {
        if (builder == null || length <= 0) return;
        lock (BuilderBars)
        {
            BuilderBars.GetOrCreateValue(builder).Add(new BarRecord(ratio, length, dark, fullLength));
        }
    }

    // ---------------- 折行预算：本作的条不改变整段 tooltip 的折行宽度 ----------------
    //
    // 原版 AddProgressBar 会把条标成不折行片段；Build 里只要出现任何不折行片段，中日韩的折行预算
    // 就从 maxCharWidth×0.66 放宽到 ×0.875，整段文字（描述、价值构成……）都会被拉长。
    // 本作的条在逻辑层也走 AddProgressBar（第三方因此能从同一入口读比例），这个副作用留在渲染层抵消：
    // 构建前若不折行片段全是本作写的条，临时把 maxCharWidth 收窄到 ×0.875 正好等于原来的 ×0.66，构建后还原。
    // 原版或别的模组写的条照旧放宽，不改它们的行为；条本身仍是不折行片段，不会被拆开。
    //
    // 判断"全是本作的条"不读私有片段（那需要对嵌套类型做反射，会让模组扫描器要求额外能力），
    // 而是按 builder 计数：游戏只有三个公开入口会写不折行片段，都在这里计入 All；
    // 本作写的条另记 Own。Add(builder) 并入对方的计数，Clear 清零；All == Own 才收窄。

    private sealed class BarCount
    {
        internal int All;
        internal int Own;
    }

    private static readonly ConditionalWeakTable<RichTextBuilder, BarCount> Counts = new();

    /// <summary>刚由 <c>AddProgressBar</c> 写出的那条是本作的。紧跟在调用之后调用。</summary>
    internal static void MarkOwnBar(RichTextBuilder builder)
    {
        if (builder == null) return;
        Counts.GetOrCreateValue(builder).Own++;
    }

    private static void CountBar(RichTextBuilder __instance)
    {
        if (__instance != null) Counts.GetOrCreateValue(__instance).All++;
    }

    /// <summary>
    /// 与原版 Build 同一套 float 运算：返回最小的宽度 w，使 (int)(w×0.875) 不小于 (int)(maxCharWidth×0.66)。
    /// 0.875 的步长小于 1，向下取整会落到每一个整数上，所以结果的预算恰好等于原来的 0.66 预算。
    /// </summary>
    internal static int NarrowForNoBreak(int maxCharWidth)
    {
        int target = Math.Max(1, (int)((float)maxCharWidth * 0.66f));
        int width = Math.Max(1, (int)Math.Floor(target / 0.875f));
        while (Math.Max(1, (int)((float)width * 0.875f)) < target) width++;
        while (width > 1 && Math.Max(1, (int)((float)(width - 1) * 0.875f)) >= target) width--;
        return width;
    }

    private static void BeforeBuild(RichTextBuilder __instance, out int __state)
    {
        __state = 0;
        try
        {
            // 没写过本作的条只查一次表；有原版或别人的条、或被第三方清空重排过，都保持原版行为。
            if (!Counts.TryGetValue(__instance, out var count) || count.Own <= 0 || count.All != count.Own) return;
            float scale = UISettings.current != null ? UISettings.current.GetCharWidthScale() : 1f;
            if (scale <= 1f) return;                                   // 西文不分两档预算
            int original = __instance.maxCharWidth;
            int narrowed = NarrowForNoBreak(original);
            if (narrowed >= original) return;
            __state = original;
            __instance.maxCharWidth = narrowed;
        }
        catch { __state = 0; }
    }

    private static Exception? RestoreWidth(RichTextBuilder __instance, int __state, Exception? __exception)
    {
        if (__state > 0)
        {
            try { __instance.maxCharWidth = __state; } catch { }
        }
        return __exception;
    }

    private static void BeforeBar(RichTextBuilder __instance, float __0, int __1, RenderHandler.ColorPalette __3, bool __6)
    {
        // 每一条都计数（与高清条开关无关）：折行预算判断要知道是不是全是本作的条。
        CountBar(__instance);
        if (ModSettings.BlockProgressBar && __1 > 0)
        {
            RecordBar(__instance, __0, __1, __3, __6);
        }
    }

    private static void AfterAddBuilder(RichTextBuilder __instance, RichTextBuilder __0)
    {
        if (__0 == null) return;
        if (Counts.TryGetValue(__0, out var other) && (other.All > 0 || other.Own > 0))
        {
            var mine = Counts.GetOrCreateValue(__instance);
            mine.All += other.All;
            mine.Own += other.Own;
        }
        lock (BuilderBars)
        {
            if (BuilderBars.TryGetValue(__0, out var otherBars) && otherBars.Count > 0)
            {
                BuilderBars.GetOrCreateValue(__instance).AddRange(otherBars);
            }
        }
    }

    private static void AfterClear(RichTextBuilder __instance)
    {
        Counts.Remove(__instance);
        lock (BuilderBars)
        {
            if (BuilderBars.TryGetValue(__instance, out var bars)) bars.Clear();
        }
    }

    /// <summary>
    /// 把这一份 builder 记录的条挂到它构建出的 <see cref="RichText"/> 上。
    /// 只有这条关联能让渲染层确认「这段文字里的 [=====  ] 就是我刚算的那条」；
    /// 别的文本没有关联时一律按它自己写的填充格数还原，不借别人的比例。
    /// </summary>
    private static void AfterBuild(RichTextBuilder __instance, ref RichText __result)
    {
        if (__result == null) return;
        lock (BuilderBars)
        {
            if (BuilderBars.TryGetValue(__instance, out var bars) && bars.Count > 0)
            {
                var copy = new List<BarRecord>(bars);
                lock (RichTextBars)
                {
                    if (RichTextBars.TryGetValue(__result, out _)) RichTextBars.Remove(__result);
                    RichTextBars.Add(__result, copy);
                }
            }
        }
    }

    private static void BeforeSetRichText(RichTextElement __instance, ref RichText richText)
    {
        if (!ModSettings.BlockProgressBar || richText == null) return;
        string fg = richText.richTextForeground;
        if (string.IsNullOrEmpty(fg) || fg.IndexOf('[') < 0) return;

        // 只认这个 RichText 自己的记录。没有记录时（别的模组重建的文字、原生条、旧缓存）
        // 就按文字里的填充格数还原：宁可少一位小数，也不能把另一件物品的精确比例套上去。
        List<BarRecord>? bars = null;
        lock (RichTextBars)
        {
            if (RichTextBars.TryGetValue(richText, out var list))
            {
                bars = new List<BarRecord>(list);
            }
        }

        TMP_Text? targetText = null;
        try
        {
            if (_textNodeField != null && _textProp != null)
            {
                object? node = _textNodeField.GetValue(__instance);
                if (node != null) targetText = _textProp.GetValue(node) as TMP_Text;
            }
        }
        catch { }

        string newFg = BarRegex.Replace(fg, match =>
        {
            string inner = match.Groups[1].Value;
            int len = inner.Length;
            if (len < 2 || len > MaxLength) return match.Value;
            int firstSpace = inner.IndexOf(' ');
            if (firstSpace >= 0 && inner.IndexOf('=', firstSpace) >= 0) return match.Value;

            int equals = 0;
            for (int i = 0; i < len; i++) if (inner[i] == '=') equals++;
            float fallbackRatio = (float)equals / len;

            BarRecord? record = MatchBar(bars, len, equals);
            float ratio = record?.Ratio ?? fallbackRatio;
            var dark = record?.Dark ?? RenderHandler.ColorPalette.None;
            bool fullLength = record?.FullLength ?? true;

            string? sprite = fullLength
                ? SpriteProgressBar.TryRender(ratio, len, dark, targetText)
                : null;
            return sprite ?? RenderBlocks(ratio, len, dark, fullLength);
        });

        if (!string.Equals(newFg, fg, StringComparison.Ordinal))
        {
            richText = new RichText(
                richText.nakedText,
                richText.richTextStrikethrough,
                richText.richTextUnderline,
                newFg,
                richText.richTextBackground,
                richText.recommendedWidth,
                richText.recommendedHeight,
                richText.isTruncated
            );
        }
    }

    /// <summary>
    /// 找这条 [=====  ] 对应的记录：长度相同、格数按记录的比例算得出来（原版截断或补满都认）。
    /// 只按长度、或干脆取第一条，会在第三方改写了填充格数后套上别的比例和底色；
    /// 匹配不上就返回 null，由调用方按文字自己的比例画。
    /// </summary>
    private static BarRecord? MatchBar(List<BarRecord>? bars, int length, int equalsCount)
    {
        if (bars == null || bars.Count == 0) return null;

        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            if (b.Length != length || !ProgressFill.Accepts(b.Ratio, b.Length, equalsCount)) continue;
            bars.RemoveAt(i);
            return b;
        }
        return null;
    }

    /// <summary>
    /// 把记录从旧的 <see cref="RichText"/> 带到替换后的新实例上。
    /// 公司名合并之类的改写会重建 RichText，文字里的条没有变，记录跟着走才不会退回按格数还原。
    /// 旧实例没有记录时什么都不做。
    /// </summary>
    internal static void Carry(RichText? from, RichText? to)
    {
        if (from == null || to == null || ReferenceEquals(from, to)) return;
        lock (RichTextBars)
        {
            if (!RichTextBars.TryGetValue(from, out var bars) || bars.Count == 0) return;
            var copy = new List<BarRecord>(bars);
            if (RichTextBars.TryGetValue(to, out _)) RichTextBars.Remove(to);
            RichTextBars.Add(to, copy);
        }
    }

    /// <summary>
    /// 原版 RichTextBuilder 计折行时的可见字符数：跳过 &lt;…&gt; 标签与控制字符。
    /// 与框宽修正共用同一套标签判定，字面尖括号（「不足 1 天」写作 &lt;1 day）按普通字符计。
    /// </summary>
    internal static int VisibleLength(string? text) => TooltipWidthFix.VisibleWidth(text);

    /// <summary>
    /// 完整行所需的 maxCharWidth；反算原版 CJK 的 0.66 折行预算，避免框宽足够却提前断行。
    /// 本作的条虽然是不折行片段，构建时 <see cref="BeforeBuild"/> 会把预算收回 0.66，所以按 0.66 预留是准确值；
    /// 同一段里还有原版的条时预算是 0.875，按 0.66 预留只会更宽松。
    /// </summary>
    internal static int WidthForLine(int visibleLength)
    {
        float scale = UISettings.current != null ? UISettings.current.GetCharWidthScale() : 1f;
        return WidthForLine(visibleLength, scale);
    }

    internal static int WidthForLine(int visibleLength, float scale)
    {
        if (visibleLength <= 0) return 0;
        if (scale <= 1f) return visibleLength;
        int width = (int)Math.Ceiling(visibleLength / 0.66f);
        // 与 Build 的 float 乘法及向下取整保持一致，防止边界舍入少一个字符。
        while ((int)(width * 0.66f) < visibleLength) width++;
        return width;
    }

    /// <summary>格数同原版 floor(比例 × 长度)，只是接近满时按满画（见 <see cref="ProgressFill"/>）。</summary>
    private static string RenderBlocks(float rawRatio, int length, RenderHandler.ColorPalette dark, bool fullLength)
    {
        int filled = ProgressFill.Filled(rawRatio, length);
        int empty = fullLength ? length - filled : 0;

        var sb = new StringBuilder(length + 32);
        if (filled > 0) sb.Append(Block, filled);
        if (empty > 0)
        {
            string rest = new string(Block, empty);
            sb.Append(dark != RenderHandler.ColorPalette.None
                ? RenderHandler.ApplyRichTextColorTag(rest, dark)
                : DimAlpha + rest + "<alpha=#FF>");
        }
        return sb.ToString();
    }
}
