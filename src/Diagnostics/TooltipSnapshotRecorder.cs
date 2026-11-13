#if ITEMDETAILS_SELFTEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 快照录制器。两部分：
///   1. 折行后片段：Build 前缀开一帧，<c>TextSlice.GetString</c> 后缀把片段记进栈顶帧，Build 后缀/终结器收帧。
///      离线夹具不经 Harmony，直接调 <see cref="BeginFrame"/> / <see cref="OnSlice"/> / <see cref="EndFrame"/>。
///   2. 原版带（P2）：builder 各写入入口的最高优先级前缀按顺序记下调用与参数（在本模组任何改写之前），
///      后缀按 <c>__runOriginal</c> 与片段增量判定这次调用被保留、改写还是丢弃。只记最外层调用。
///
/// 补丁只在导出期间装上（<see cref="Install"/> 返回的作用域在导出结束时撤销），只在 SelfTest 构建里编译。
/// </summary>
internal static class TooltipSnapshotRecorder
{
    // ---------------- 折行后片段 ----------------

    internal sealed class Frame
    {
        internal RichTextBuilder Builder = null!;
        internal readonly List<object> Folded = new();
        internal int MaxCharWidth = -1;
        internal int BuildMaxCharWidth = -1;
        internal RichText? Raw;
        internal int RawWidth = -1;
        internal RichText? Final;
        internal string? Company;
        internal int Depth;
    }

    private static readonly List<Frame> Stack = new();
    /// <summary>本次会话里完成的帧（含嵌套 Build），按完成顺序。</summary>
    internal static readonly List<Frame> Completed = new();

    /// <summary>游戏内会话：只有在 <see cref="BeginSession"/> 与 <see cref="EndSession"/> 之间才记录。</summary>
    internal static bool Armed { get; private set; }

    internal static Frame BeginFrame(RichTextBuilder builder)
    {
        var frame = new Frame { Builder = builder, MaxCharWidth = builder.maxCharWidth, Depth = Stack.Count };
        Stack.Add(frame);
        return frame;
    }

    internal static void EndFrame(Frame frame)
    {
        int index = Stack.LastIndexOf(frame);
        if (index >= 0) Stack.RemoveRange(index, Stack.Count - index);
        Completed.Add(frame);
    }

    internal static void OnSlice(object slice)
    {
        if (Stack.Count > 0 && slice != null) Stack[Stack.Count - 1].Folded.Add(slice);
    }

    private static Frame? Top(RichTextBuilder builder)
    {
        if (Stack.Count == 0) return null;
        var top = Stack[Stack.Count - 1];
        return ReferenceEquals(top.Builder, builder) ? top : null;
    }

    internal static void BeginSession()
    {
        Stack.Clear();
        Completed.Clear();
        Calls.Clear();
        BuilderIds.Clear();
        _depth = 0;
        _seq = 0;
        Armed = true;
    }

    internal static void EndSession()
    {
        Armed = false;
        Stack.Clear();
        _depth = 0;
    }

    // ---------------- 原版带（P2） ----------------

    internal sealed class Call
    {
        internal int Seq;
        internal int Builder;
        internal string Method = "";
        internal ParameterInfo[] Params = Array.Empty<ParameterInfo>();
        internal object?[] Args = Array.Empty<object?>();
        internal string Origin = "unknown";
        internal string Caller = "";
        internal string? Fate;
        internal int Before = -1;
        internal int After = -1;
    }

    internal static readonly List<Call> Calls = new();
    private static readonly Dictionary<object, int> BuilderIds = new(new IdentityComparer());
    private static int _depth;
    private static int _seq;

    private sealed class IdentityComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    internal static int BuilderId(RichTextBuilder builder)
    {
        if (!BuilderIds.TryGetValue(builder, out int id))
        {
            id = BuilderIds.Count;
            BuilderIds[builder] = id;
        }
        return id;
    }

    /// <summary>原版带记录的写入入口（RichTextBuilder 的公开实例方法）。</summary>
    private static readonly HashSet<string> Writers = new(StringComparer.Ordinal)
    {
        "Add", "AddLine", "NextLine", "AddEmptyLine", "AddProgressBar", "AddProgressBarOld", "AddProgressTest",
        "AddIcon", "Clear", "AddRichTable",
    };

    // ---------------- 装配 ----------------

    private static FieldInfo? _pendingBuilder, _pendingCompany;

    /// <summary>装上全部录制补丁；返回作用域（导出结束时 Dispose）。<paramref name="report"/> 记录哪些没装上。</summary>
    internal static PatchScope Install(List<string> report)
    {
        var scope = new PatchScope("TooltipSnapshotRecorder");
        string? bind = TooltipSnapshot.Bind();
        if (bind != null) { report.Add("片段绑定失败：" + bind); return scope; }

        var build = AccessTools.Method(typeof(RichTextBuilder), "Build", new[] { typeof(int) });
        var getString = TooltipSnapshot.GetStringMethod;
        _pendingBuilder = AccessTools.Field(typeof(ManufacturerInline), "_pendingBuilder");
        _pendingCompany = AccessTools.Field(typeof(ManufacturerInline), "_pendingCompany");
        if (_pendingBuilder == null || _pendingCompany == null) report.Add("ManufacturerInline 的待并入字段不在，公司名不记录");

        HarmonyMethod M(string name, int priority) =>
            new(typeof(TooltipSnapshotRecorder), name) { priority = priority };

        bool ok = scope.Patch(build, prefix: M(nameof(BuildFirst), Priority.First))
                  & scope.Patch(build, prefix: M(nameof(BuildLast), Priority.Last))
                  & scope.Patch(build, postfix: M(nameof(BuiltFirst), Priority.First))
                  & scope.Patch(build, postfix: M(nameof(BuiltLast), Priority.Last))
                  & scope.Patch(build, finalizer: M(nameof(BuildDone), Priority.Last))
                  & scope.Patch(getString, postfix: M(nameof(AfterGetString), Priority.Last));
        if (!ok) report.Add("折行片段补丁没有全部装上（见 Player.log 的 Patch.TooltipSnapshotRecorder 行）");

        int writers = 0;
        foreach (var method in typeof(RichTextBuilder).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (!Writers.Contains(method.Name)) continue;
            bool patched = scope.Patch(method, prefix: M(nameof(BeforeCall), Priority.First))
                           & scope.Patch(method, postfix: M(nameof(AfterCall), Priority.Last))
                           & scope.Patch(method, finalizer: M(nameof(CallDone), Priority.Last));
            if (patched) writers++;
            else report.Add("原版带入口没装上：" + Signature(method));
        }
        report.Add($"原版带入口：{writers} 个");
        return scope;
    }

    private static string Signature(MethodBase method)
    {
        var ps = method.GetParameters();
        var names = new string[ps.Length];
        for (int i = 0; i < ps.Length; i++) names[i] = ps[i].ParameterType.Name;
        return method.Name + "(" + string.Join(",", names) + ")";
    }

    // ---------------- Build ----------------

    private static void BuildFirst(RichTextBuilder __instance)
    {
        if (Armed && __instance != null) BeginFrame(__instance);
    }

    /// <summary>其它前缀（进度条收窄预算）之后的 maxCharWidth，即 Build 实际使用的值。</summary>
    private static void BuildLast(RichTextBuilder __instance)
    {
        var frame = Armed ? Top(__instance) : null;
        if (frame != null) frame.BuildMaxCharWidth = __instance.maxCharWidth;
    }

    /// <summary>本模组改写结果之前的原始 RichText；框宽修正会原地改它，所以宽度在这里先抄下。</summary>
    private static void BuiltFirst(RichTextBuilder __instance, RichText __result)
    {
        var frame = Armed ? Top(__instance) : null;
        if (frame == null) return;
        frame.Raw = __result;
        frame.RawWidth = __result != null ? __result.recommendedWidth : -1;
        try
        {
            if (_pendingBuilder != null && _pendingCompany != null
                && ReferenceEquals(_pendingBuilder.GetValue(null), __instance))
            {
                string company = _pendingCompany.GetValue(null) as string ?? "";
                if (company.Length > 0) frame.Company = company;
            }
        }
        catch (Exception ex) { Log.Warn("TooltipSnapshot.Company", ex); }
    }

    private static void BuiltLast(RichTextBuilder __instance, RichText __result)
    {
        var frame = Armed ? Top(__instance) : null;
        if (frame != null) frame.Final = __result;
    }

    private static Exception? BuildDone(RichTextBuilder __instance, Exception? __exception)
    {
        var frame = Armed ? Top(__instance) : null;
        if (frame != null) EndFrame(frame);
        return __exception;
    }

    private static void AfterGetString(object __instance)
    {
        if (Armed) OnSlice(__instance);
    }

    // ---------------- 写入入口 ----------------

    /// <summary>__state：-1 未计数；0 计了深度但不是最外层；k&gt;0 为 Calls[k-1]。</summary>
    private static void BeforeCall(RichTextBuilder __instance, MethodBase __originalMethod, object[] __args, out int __state)
    {
        __state = -1;
        if (!Armed || __instance == null) return;
        _depth++;
        __state = 0;
        if (_depth != 1) return;
        try
        {
            var call = new Call
            {
                Seq = ++_seq,
                Builder = BuilderId(__instance),
                Method = __originalMethod?.Name ?? "?",
                Params = __originalMethod?.GetParameters() ?? Array.Empty<ParameterInfo>(),
                Before = TooltipSnapshot.SliceCount(__instance),
            };
            var args = __args ?? Array.Empty<object>();
            call.Args = new object?[args.Length];
            for (int i = 0; i < args.Length; i++) call.Args[i] = Capture(args[i]);
            (call.Caller, call.Origin) = Caller();
            Calls.Add(call);
            __state = Calls.Count;
        }
        catch (Exception ex) { Log.Warn("TooltipSnapshot.BeforeCall", ex); }
    }

    private static void AfterCall(RichTextBuilder __instance, bool __runOriginal, int __state)
    {
        if (__state <= 0 || __state > Calls.Count) return;
        var call = Calls[__state - 1];
        try
        {
            call.After = TooltipSnapshot.SliceCount(__instance);
            call.Fate = Fate(call, __instance, __runOriginal);
        }
        catch (Exception ex) { Log.Warn("TooltipSnapshot.AfterCall", ex); }
    }

    private static Exception? CallDone(Exception? __exception, int __state)
    {
        if (__state >= 0 && _depth > 0) _depth--;
        return __exception;
    }

    /// <summary>参数按值抄下：builder 只记编号与片段数，其余引用类型只记类型名或名字。</summary>
    private static object? Capture(object? arg)
    {
        switch (arg)
        {
            case null: return null;
            case string or bool or int or float or long or double: return arg;
            case char c: return c.ToString();
            case char[] chars: return new string(chars);
            case RenderHandler.ColorPalette color: return color;
            case RichTextBuilder builder: return new BuilderRef(BuilderId(builder), TooltipSnapshot.SliceCount(builder));
            case UnityEngine.Object unity: return new Opaque(unity.GetType().Name, unity != null ? unity.name : null);
            default: return new Opaque(arg.GetType().Name, null);
        }
    }

    internal sealed class BuilderRef
    {
        internal readonly int Id, Slices;
        internal BuilderRef(int id, int slices) { Id = id; Slices = slices; }
    }

    internal sealed class Opaque
    {
        internal readonly string Type;
        internal readonly string? Name;
        internal Opaque(string type, string? name) { Type = type; Name = name; }
    }

    /// <summary>
    /// kept：原方法照常执行且写出的文字就是参数原文；augmented：原文在、但同一次调用里又多写了片段；
    /// rewritten：原方法被跳过但写了替代内容，或执行时文字已被改写；dropped：被跳过且什么都没写。
    /// </summary>
    private static string Fate(Call call, RichTextBuilder builder, bool ran)
    {
        bool grew = call.After > call.Before && call.Before >= 0;
        if (!ran) return grew ? "rewritten" : "dropped";
        if ((call.Method != "Add" && call.Method != "AddLine") || call.Args.Length == 0 || !(call.Args[0] is string text))
            return "kept";
        string expected = text.Replace("\n", "");
        if (expected.Length == 0 || call.Before < 0) return "kept";
        var slices = TooltipSnapshot.Slices(builder);
        if (slices == null || call.After > slices.Count) return "kept";
        var written = new System.Text.StringBuilder();
        for (int i = call.Before; i < call.After; i++)
        {
            object s = slices[i];
            if (TooltipSnapshot.SliceIsNewLine(s)) continue;
            written.Append(TooltipSnapshot.SliceText(s));
        }
        string all = written.ToString();
        if (all.EndsWith(expected, StringComparison.Ordinal)) return "kept";
        return all.Contains(expected) ? "augmented" : "rewritten";
    }

    /// <summary>没能归属的调用栈样本（去重，最多 <see cref="MaxStackSamples"/> 条），写进导出报告供排查。</summary>
    internal static readonly List<string> UnknownStacks = new();
    private static readonly HashSet<string> SeenUnknownStacks = new(StringComparer.Ordinal);
    private const int MaxStackSamples = 12;

    /// <summary>
    /// 调用方：从录制补丁往外数，越过录制器、Harmony 与 RichTextBuilder 自身之后的第一帧。
    ///
    /// 被 Harmony 打过补丁的方法在 Mono 上以动态方法运行：有的帧拿不到声明类型，
    /// 有的声明类型是 MonoMod.Utils.DynamicMethodDefinition，方法名形如 RichTextBuilder.AddLine_Patch6。
    /// 这类帧只可能是两种东西：RichTextBuilder 写入方法的补丁包装（名字里带 RichTextBuilder，跳过），
    /// 或某个被打过补丁的原版方法的副本（原版方法体就在里面，记 vanilla，名字照录）。
    /// 模组自己的补丁方法是普通方法，有声明类型，按程序集归属。
    /// 上一版要求"先见到 RichTextBuilder 帧再认调用方"，结果 17431 次调用全部记成 unknown；
    /// 只处理无类型帧又会把这些 DynamicMethodDefinition 帧全部误归到 0Harmony。
    /// </summary>
    private static (string Caller, string Origin) Caller()
    {
        StackFrame[]? frames = null;
        try { frames = new StackTrace(2, false).GetFrames(); }
        catch (Exception ex) { Log.Warn("TooltipSnapshot.Caller", ex); }
        if (frames == null) return ("", "unknown");
        var ours = typeof(TooltipSnapshotRecorder).Assembly;
        var game = typeof(RichTextBuilder).Assembly;
        foreach (var f in frames)
        {
            MethodBase? m = f.GetMethod();
            if (m == null) continue;
            Type? type = m.DeclaringType;
            string name = m.Name ?? "";
            if (type == typeof(TooltipSnapshotRecorder) || type == typeof(RichTextBuilder)) continue;
            if (type == null || type.FullName == "MonoMod.Utils.DynamicMethodDefinition")
            {
                if (name.IndexOf(nameof(RichTextBuilder), StringComparison.Ordinal) >= 0) continue;
                return (name, "vanilla");
            }
            if (type.Assembly == typeof(Harmony).Assembly) continue;
            if (type.Assembly == typeof(object).Assembly || type.Assembly == typeof(UnityEngine.Object).Assembly) continue;
            // 一路数到了导出驱动本身：中间的帧全是拿不到方法的动态方法，也就是被打过补丁的原版方法。
            if (type == typeof(TooltipSnapshotExport) || type.DeclaringType == typeof(TooltipSnapshotExport))
                return ("(patched vanilla method)", "vanilla");
            string caller = type.FullName + "." + name;
            if (type.Assembly == ours) return (caller, "item_details");
            if (type.Assembly == game) return (caller, "vanilla");
            return (caller, "other:" + type.Assembly.GetName().Name);
        }
        if (UnknownStacks.Count < MaxStackSamples)
        {
            var sb = new System.Text.StringBuilder();
            int shown = 0;
            foreach (var f in frames)
            {
                MethodBase? m = f.GetMethod();
                sb.Append(shown++ == 0 ? "" : " <- ")
                  .Append(m == null ? "(null)" : (m.DeclaringType?.FullName ?? "(dyn)") + "." + m.Name);
                if (shown >= 10) break;
            }
            string sample = sb.ToString();
            if (SeenUnknownStacks.Add(sample)) UnknownStacks.Add(sample);
        }
        return ("", "unknown");
    }

    /// <summary>写出本次会话的 calls 数组。</summary>
    internal static void WriteCalls(JsonOut w, List<Call> calls)
    {
        w.BeginArray();
        foreach (var call in calls)
        {
            w.BeginObject();
            w.Prop("seq").Value(call.Seq);
            w.Prop("builder").Value(call.Builder);
            w.Prop("method").Value(call.Method);
            w.Prop("args").BeginObject();
            for (int i = 0; i < call.Args.Length; i++)
            {
                string name = i < call.Params.Length ? call.Params[i].Name ?? ("arg" + i) : "arg" + i;
                w.Prop(name);
                WriteArg(w, call.Args[i]);
            }
            w.EndObject();
            w.Prop("origin").Value(call.Origin);
            if (call.Caller.Length > 0) w.Prop("caller").Value(call.Caller);
            if (call.Fate != null) w.Prop("fate").Value(call.Fate);
            w.EndObject();
        }
        w.EndArray();
    }

    private static void WriteArg(JsonOut w, object? arg)
    {
        switch (arg)
        {
            case null: w.Value((string?)null); break;
            case string s: w.Value(s); break;
            case bool b: w.Value(b); break;
            case int n: w.Value(n); break;
            case long l: w.Value(l); break;
            case float f: w.Value(f); break;
            case double d: w.Value(d); break;
            case RenderHandler.ColorPalette color:
                w.Value(color == RenderHandler.ColorPalette.None ? "None"
                    : Enum.GetName(typeof(RenderHandler.ColorPalette), color) ?? TooltipSnapshot.Hex(color));
                break;
            case BuilderRef r:
                w.BeginObject().Prop("builder").Value(r.Id).Prop("slices").Value(r.Slices).EndObject();
                break;
            case Opaque o:
                w.BeginObject().Prop("type").Value(o.Type);
                if (o.Name != null) w.Prop("name").Value(o.Name);
                w.EndObject();
                break;
            default: w.Value(Convert.ToString(arg, CultureInfo.InvariantCulture)); break;
        }
    }
}
#endif
