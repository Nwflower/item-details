using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 渲染层的片段替换：逻辑层只写规范输出，原版窗口构建时才换成呈现用的写法。
///
/// 规范输出是第三方在 GetTooltipBasic 返回时读到的那一份：成分表用全角空格分隔单元格，描述是原文不折行。
/// 原版窗口需要的 &lt;pos&gt; 三列与中日韩描述预折行，按"片段对象身份"挂注解，
/// 在 <c>RichTextBuilder.Build</c> 前把这几片临时换掉，构建完立即还原，builder 本身始终是规范输出。
///
/// 依据（反编译正式版）：<c>Add(RichTextBuilder)</c> 是 <c>slices.AddRange</c>，片段原样流转时注解跟着走；
/// 排版 Mod 重排时会 <c>Clear</c> 再写入自己生成的新片段，注解自然失效，不会改到别人的排版。
///
/// 只访问 <c>RichTextBuilder.slices</c> 这一个字段，片段本身当作不透明对象：替换片段由一个临时 builder
/// 用同样的参数 <c>Add</c> 出来。私有嵌套类型 TextSlice 的成员一概不碰，模组扫描器因此不需要额外能力。
/// </summary>
internal static class RenderSlices
{

    /// <summary>写这一行时用的样式参数；替换片段按同一组参数生成，颜色粗细与原片段一致。</summary>
    internal readonly struct Style
    {
        internal readonly RenderHandler.ColorPalette Color;
        internal readonly bool Bold, Italic, Underline, Strikethrough;
        internal readonly RenderHandler.ColorPalette Background, UnderlineColor, StrikethroughColor;

        internal Style(RenderHandler.ColorPalette color, bool bold = false, bool italic = false,
            bool underline = false, bool strikethrough = false,
            RenderHandler.ColorPalette background = RenderHandler.ColorPalette.None,
            RenderHandler.ColorPalette underlineColor = RenderHandler.ColorPalette.None,
            RenderHandler.ColorPalette strikethroughColor = RenderHandler.ColorPalette.None)
        {
            Color = color;
            Bold = bold;
            Italic = italic;
            Underline = underline;
            Strikethrough = strikethrough;
            Background = background;
            UnderlineColor = underlineColor;
            StrikethroughColor = strikethroughColor;
        }
    }

    /// <summary>一段规范文本写出的片段，以及构建时换成什么。</summary>
    internal sealed class Note
    {
        internal readonly object[] Run;
        internal readonly string Text;
        internal readonly Style Style;
        /// <summary>固定的呈现文本（成分表的 &lt;pos&gt; 列）；null 表示按折行预算决定（描述）。</summary>
        internal readonly string? Fixed;

        // 替换片段按需生成一次；描述随折行宽度变，按宽度缓存最近一份。
        internal object[]? Alt;
        internal int AltLimit = -1;

        internal Note(object[] run, string text, Style style, string? fixedText)
        {
            Run = run;
            Text = text;
            Style = style;
            Fixed = fixedText;
        }
    }

    private sealed class Flag { }

    private static readonly ConditionalWeakTable<object, Note> Notes = new();
    /// <summary>含有注解片段的 builder：构建时没有标记就只查这一次表。</summary>
    private static readonly ConditionalWeakTable<RichTextBuilder, Flag> Marked = new();

    private static FieldInfo? _slices;

    /// <summary>生成替换片段：默认用临时 builder 按同样参数 Add。测试可换成不依赖渲染器的实现。</summary>
    internal static Func<string, Style, object[]?> MakeSlices = DefaultMake;

    internal static void Install(PatchScope scope)
    {
        try
        {
            _slices = AccessTools.Field(typeof(RichTextBuilder), "slices");
            var build = AccessTools.Method(typeof(RichTextBuilder), "Build", new[] { typeof(int) });
            var addBuilder = AccessTools.Method(typeof(RichTextBuilder), "Add", new[] { typeof(RichTextBuilder) });
            var clear = AccessTools.Method(typeof(RichTextBuilder), "Clear", Type.EmptyTypes);
            if (_slices == null || build == null || addBuilder == null || clear == null)
            {
                // 退化为规范输出：成分表按全角空格分列，描述交给原版折行。数据不受影响。
                _slices = null;
                Log.Warn("RenderSlices.Apply", "未找到 slices/Build/Add(builder)/Clear，成分表与描述按规范输出显示");
                return;
            }

            scope.Patch(build,
                prefix: new HarmonyMethod(typeof(RenderSlices), nameof(BeforeBuild)),
                finalizer: new HarmonyMethod(typeof(RenderSlices), nameof(AfterBuild)));
            scope.Patch(addBuilder, postfix: new HarmonyMethod(typeof(RenderSlices), nameof(AfterAddBuilder)));
            scope.Patch(clear, postfix: new HarmonyMethod(typeof(RenderSlices), nameof(AfterClear)));
        }
        catch (Exception ex)
        {
            _slices = null;
            Log.Error("RenderSlices.Apply", ex);
        }
        scope.OnDispose(Remove);
    }

    internal static void Remove()
    {
        _slices = null;
    }

    // ---------------- 逻辑层：写规范输出并挂注解 ----------------

    /// <summary>写一行规范文本，原版窗口构建时换成 <paramref name="render"/>。</summary>
    internal static void AddLine(RichTextBuilder builder, string text, RenderHandler.ColorPalette color, string render)
    {
        int before = Count(builder);
        ModEntry.AddLine(builder, text, color);
        if (before >= 0 && !string.Equals(text, render, StringComparison.Ordinal))
            Annotate(builder, before, text, new Style(color), render);
    }

    /// <summary>当前片段数；拿不到时返回 -1，调用方照常写规范输出、不挂注解。</summary>
    internal static int Count(RichTextBuilder? builder)
    {
        if (builder == null || _slices == null) return -1;
        try { return _slices.GetValue(builder) is IList list ? list.Count : -1; }
        catch { return -1; }
    }

    /// <summary>
    /// 给刚由 <c>Add(text, …)</c> 写出的片段挂注解。<paramref name="before"/> 是写之前的片段数：
    /// 新增片段里前面可能是上一行留下的换行（nextBegin），末尾 <see cref="Pieces"/> 个才是这段文字。
    /// </summary>
    internal static void Annotate(RichTextBuilder builder, int before, string text, Style style, string? fixedText)
    {
        try
        {
            if (_slices == null || !(_slices.GetValue(builder) is IList list)) return;
            int k = Pieces(text);
            int start = list.Count - k;
            if (k <= 0 || start < before) return;
            var run = new object[k];
            for (int i = 0; i < k; i++) run[i] = list[start + i];
            Notes.Remove(run[0]);
            Notes.Add(run[0], new Note(run, text, style, fixedText));
            Marked.GetOrCreateValue(builder);
        }
        catch (Exception ex) { Log.Warn("RenderSlices.Annotate", ex); }
    }

    /// <summary>与原版 Add 同样按 '\n' 切分：非空的段各一片，段与段之间各一片换行。</summary>
    internal static int Pieces(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 0, segment = 0;
        foreach (char c in text!)
        {
            if (c != '\n') { segment++; continue; }
            if (segment > 0) count++;
            count++;
            segment = 0;
        }
        if (segment > 0) count++;
        return count;
    }

    // ---------------- 渲染层：Build 前换入，Build 后还原 ----------------

    private static void BeforeBuild(RichTextBuilder __instance, out object[]? __state)
    {
        __state = null;
        if (_slices == null || __instance == null || !Marked.TryGetValue(__instance, out _)) return;
        try
        {
            if (!(_slices.GetValue(__instance) is IList list)) return;
            __state = SwapIn(list, note => Replacement(__instance, note));
        }
        catch (Exception ex)
        {
            __state = null;
            Log.Warn("RenderSlices.Build", ex);
        }
    }

    private static Exception? AfterBuild(RichTextBuilder __instance, object[]? __state, Exception? __exception)
    {
        if (__state != null && _slices != null)
        {
            try
            {
                if (_slices.GetValue(__instance) is IList list) Restore(list, __state);
            }
            catch (Exception ex) { Log.Warn("RenderSlices.Restore", ex); }
        }
        return __exception;
    }

    /// <summary>
    /// 把带注解的连续片段换成替换片段；返回换之前的完整内容供还原，什么都没换时返回 null。
    /// 注解的片段不连续或顺序变了（被第三方挪动过）就不换。
    /// </summary>
    internal static object[]? SwapIn(IList list, Func<Note, object[]?> replacement)
    {
        List<object>? rebuilt = null;
        for (int i = 0; i < list.Count; i++)
        {
            object slice = list[i];
            if (slice != null && Notes.TryGetValue(slice, out var note) && Contiguous(list, i, note.Run))
            {
                var alt = replacement(note);
                if (alt != null && alt.Length > 0)
                {
                    if (rebuilt == null)
                    {
                        rebuilt = new List<object>(list.Count + 8);
                        for (int j = 0; j < i; j++) rebuilt.Add(list[j]);
                    }
                    rebuilt.AddRange(alt);
                    i += note.Run.Length - 1;
                    continue;
                }
            }
            rebuilt?.Add(slice!);
        }
        if (rebuilt == null) return null;

        var saved = new object[list.Count];
        list.CopyTo(saved, 0);
        try
        {
            list.Clear();
            foreach (var s in rebuilt) list.Add(s);
        }
        catch
        {
            Restore(list, saved);
            throw;
        }
        return saved;
    }

    internal static void Restore(IList list, object[] saved)
    {
        list.Clear();
        foreach (var s in saved) list.Add(s);
    }

    private static bool Contiguous(IList list, int start, object[] run)
    {
        if (start + run.Length > list.Count) return false;
        for (int j = 0; j < run.Length; j++)
            if (!ReferenceEquals(list[start + j], run[j])) return false;
        return true;
    }

    private static object[]? Replacement(RichTextBuilder builder, Note note)
    {
        if (note.Fixed != null)
            return note.Alt ??= MakeSlices(note.Fixed, note.Style);

        // 描述：字号缩小一点，和上面的数据行拉开层次；中日韩且这段 tooltip 的折行预算被别人的条放宽到 0.875 时，
        // 还要按原版 0.66 预折行。缩小只动渲染写法，规范文本仍是原文。
        float scale = UISettings.current != null ? UISettings.current.GetCharWidthScale() : 1f;
        int limit = scale > 1f && ProgressBarStyle.WidensBudget(builder)
            ? Math.Max(8, (int)(builder.maxCharWidth * 0.66f)) : 0;
        bool wrap = limit > 0 && note.Text.Length > limit;
        if (!wrap && !ShrinkDescription) return null;
        int key = wrap ? limit : -2;
        if (note.AltLimit != key)
        {
            string body = wrap ? TextWrap.Wrap(note.Text, limit) : note.Text;
            note.Alt = MakeSlices(ShrinkDescription ? Shrink(body) : body, note.Style);
            note.AltLimit = key;
        }
        return note.Alt;
    }

    /// <summary>描述缩小到正文的这个比例（约小 1–2 号）。</summary>
    private const string DescriptionSize = "88%";
    private static bool ShrinkDescription => ModSettings.TooltipFormatting;

    /// <summary>逐行包上字号标签：折行后的每一行各自闭合，避免标签跨过换行被切成不成对的片段。</summary>
    private static string Shrink(string text)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Length > 0) lines[i] = "<size=" + DescriptionSize + ">" + lines[i] + "</size>";
        return string.Join("\n", lines);
    }

    private static object[]? DefaultMake(string text, Style style)
    {
        if (_slices == null) return null;
        var scratch = new RichTextBuilder(int.MaxValue / 2);
        scratch.Add(text, style.Color, "", style.Bold, style.Italic, style.Underline, style.Strikethrough,
            style.Background, style.UnderlineColor, style.StrikethroughColor);
        if (!(_slices.GetValue(scratch) is IList list) || list.Count == 0) return null;
        var slices = new object[list.Count];
        list.CopyTo(slices, 0);
        return slices;
    }

    private static void AfterAddBuilder(RichTextBuilder __instance, RichTextBuilder __0)
    {
        if (__0 != null && Marked.TryGetValue(__0, out _)) Marked.GetOrCreateValue(__instance);
    }

    private static void AfterClear(RichTextBuilder __instance) => Marked.Remove(__instance);
}
