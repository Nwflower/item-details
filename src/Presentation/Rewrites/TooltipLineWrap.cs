using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 中文、日文物品 tooltip 的断点选择，规则见 <see cref="TooltipLineBreak"/>。
/// 只在 <c>GameItemElement.GetTooltip</c> / <c>GetTooltipAdvanced</c> 期间生效：它们内部调用
/// <c>Build</c>，别处（对话、面板、按钮说明）走原版折行。折行预算和框宽都不变。
///
/// 原版 <c>Build</c> 会吞掉 <c>FindBestSlice</c> 返回位置上的字符，所以它只返回空格断点；
/// 其余断点让它返回 (-1,-1)，交给不吞字的 <c>FindCharBreak</c> 返回。
/// </summary>
internal static class TooltipLineWrap
{
    private static int _depth;
    private static FieldInfo? _text, _newLine, _noBreak;

    internal static void Install(PatchScope scope)
    {
        // 片段类型是私有嵌套类，写不了 typeof。类型名要写成程序集限定的字面量并直接接 GetField：
        // 旧版扫描器（scanner version 3，游戏 100-Rev12 及更早）只认 Type.GetType("字面量") 这一种写法，
        // AccessTools.TypeByName / Inner 都会被判成 AccessTools.Field on a computed type（reflection-external）。
        try
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _text = Type.GetType("RichTextBuilder+TextSlice, Assembly-CSharp").GetField("text", any);
            _newLine = Type.GetType("RichTextBuilder+TextSlice, Assembly-CSharp").GetField("isNewLine", any);
            _noBreak = Type.GetType("RichTextBuilder+TextSlice, Assembly-CSharp").GetField("noBreak", any);
        }
        catch (NullReferenceException)
        {
            _text = _newLine = _noBreak = null;
        }
        var best = AccessTools.Method(typeof(RichTextBuilder), "FindBestSlice");
        var charBreak = AccessTools.Method(typeof(RichTextBuilder), "FindCharBreak");
        var tooltip = AccessTools.Method(typeof(GameItemElement), "GetTooltip", Type.EmptyTypes);
        var advanced = AccessTools.Method(typeof(GameItemElement), "GetTooltipAdvanced", Type.EmptyTypes);
        if (_text == null || _newLine == null || _noBreak == null
            || best == null || charBreak == null || tooltip == null || advanced == null)
        {
            Log.Warn("TooltipLineWrap", "未找到折行方法或片段字段，折行保持原版");
            return;
        }

        scope.Patch(best, prefix: new HarmonyMethod(typeof(TooltipLineWrap), nameof(BeforeBest)));
        scope.Patch(charBreak, prefix: new HarmonyMethod(typeof(TooltipLineWrap), nameof(BeforeCharBreak)));
        foreach (var method in new[] { tooltip, advanced })
            scope.Patch(method,
                prefix: new HarmonyMethod(typeof(TooltipLineWrap), nameof(Enter)),
                finalizer: new HarmonyMethod(typeof(TooltipLineWrap), nameof(Exit)));
        scope.OnDispose(() => { _depth = 0; _memoSlices = null; });
    }

    private static void Enter(out bool __state)
    {
        _depth++;
        __state = true;
    }

    private static void Exit(bool __state)
    {
        if (__state) _depth--;
        _memoSlices = null;   // 不让上一次构建的片段表活过这次 tooltip
    }

    // 原版在 FindBestSlice 返回 (-1,-1) 后紧接着用同一组参数调 FindCharBreak（见 RichTextBuilder.Build），
    // 中间不改片段表。两个前缀要的是同一个断点，所以前者算完留给后者，用一次就作废，不重复读片段。
    private static object? _memoSlices;
    private static int _memoBegin, _memoEnd, _memoBudget;
    private static bool _memoFound;
    private static BreakPoint _memoPoint;

    // 片段类型是私有嵌套类，参数只能写成 object
    private static bool BeforeBest(object slices, int beginIndex, int endIndex, int maxLength, ref (int, int) __result)
    {
        bool found = TryFind(slices, beginIndex, endIndex, maxLength, out var point);
        _memoSlices = slices;
        _memoBegin = beginIndex;
        _memoEnd = endIndex;
        _memoBudget = maxLength;
        _memoFound = found;
        _memoPoint = point;
        if (!found) return true;
        __result = point.IsSpace ? (point.Slice, point.Index) : (-1, -1);
        return false;
    }

    private static bool BeforeCharBreak(object slices, int beginIndex, int endIndex, int maxLength, ref (int, int) __result)
    {
        bool found;
        BreakPoint point;
        if (ReferenceEquals(_memoSlices, slices) && _memoBegin == beginIndex && _memoEnd == endIndex
            && _memoBudget == maxLength)
        {
            found = _memoFound;
            point = _memoPoint;
        }
        else found = TryFind(slices, beginIndex, endIndex, maxLength, out point);
        _memoSlices = null;
        if (!found || point.IsSpace) return true;
        __result = (point.Slice, point.Index);
        return false;
    }

    private static bool TryFind(object slices, int begin, int end, int budget, out BreakPoint point)
    {
        point = default;
        if (_depth <= 0 || !ModSettings.TooltipFormatting || !BreaksAnywhere(Loc.Code)) return false;
#if ITEMDETAILS_DIAG
        long diagStarted = HotPathPerf.Start();
#endif
        try
        {
            if (!(slices is IList list)) return false;
            int first = Math.Max(0, begin);
            int last = Math.Min(end, list.Count - 1);
            var range = new List<BreakSlice>(Math.Max(0, last - first + 1));
            for (int i = first; i <= last; i++)
            {
                object slice = list[i];
                range.Add(new BreakSlice((string)_text!.GetValue(slice),
                    (bool)_newLine!.GetValue(slice), (bool)_noBreak!.GetValue(slice)));
            }
            if (!TooltipLineBreak.Find(range, budget, out var found)) return false;
            point = new BreakPoint(found.Slice + first, found.Index, found.IsSpace);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("TooltipLineWrap", ex);
            return false;
        }
#if ITEMDETAILS_DIAG
        finally { HotPathPerf.Stop(HotPathPerf.Slot.Wrap, diagStarted); }
#endif
    }

    /// <summary>韩文虽然也按双倍字宽算，但靠空格分词，不属于这一类。</summary>
    internal static bool BreaksAnywhere(string? code)
        => code != null && (code.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                            || code.StartsWith("ja", StringComparison.OrdinalIgnoreCase));
}
