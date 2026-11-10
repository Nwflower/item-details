#if ITEMDETAILS_DIAG
using System;
using System.Diagnostics;
using System.Text;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 悬停热路径的分段计时（只进诊断构建）。既有的 [perf] 窗口只量正文追加器、水质模拟和分类标签，
/// 量不到折行、渲染层换片、文本变化引起的重排，也看不出尖峰落在哪一段。这里按阶段量整段墙钟时间：
///
///   帧            两次 ShowTooltip 之间的间隔，即悬停期间的实际帧时间
///   GetTooltip    一次完整构建：原版 + 所有模组的追加与改写 + Build
///   Basic         其中的内容阶段（GetTooltipBasic）
///   Build         其中的折行与拼串阶段（含渲染层换片、折行补丁）
///   ShowTooltip   提交阶段：文本变了才有的 SetRichText 与窗口重排，加上各显示后缀
///   SetRichText   ShowTooltip 内真正触发的次数与耗时；次数接近帧数说明文本每帧在变
///
/// 以上几段包含原版和其他模组的开销，用来判断时间花在哪个阶段；
/// 折行查找、拥有物扫描、名称标签是本作自己的代码，由调用点用 <see cref="Start"/> / <see cref="Stop"/> 计时。
///
/// 只统计连续悬停：悬停中断（两次 ShowTooltip 相隔超过 <see cref="FrameGapLimitMs"/>）时这一段就此结束，
/// 之后到下次悬停之间的样本一律不计。一段悬停满 2 秒输出一行，不足 2 秒的在中断时输出；
/// 行首写明这一行实际覆盖的悬停时长。每段给出 次数/中位/均次/峰值：均次被少数冷启动拉高时，看中位数。
/// </summary>
internal static class HotPathPerf
{
    internal enum Slot { Frame, GetTooltip, Basic, Build, Show, SetRichText, Wrap, OwnedScan, NameLabel }

    private static readonly string[] Names =
        { "帧", "GetTooltip", "Basic", "Build", "ShowTooltip", "SetRichText", "折行查找", "拥有物扫描", "名称标签" };

    private const int SlotCount = 9;
    private const double WindowSeconds = 2.0;
    /// <summary>两次 ShowTooltip 隔得比这更久，就不是连续悬停的相邻两帧。</summary>
    private const double FrameGapLimitMs = 250.0;
    /// <summary>每段留多少个样本算中位数；超出的只进次数、均次与峰值。</summary>
    private const int SampleCap = 1024;

    private static readonly long FrameGapLimitTicks = (long)(FrameGapLimitMs * Stopwatch.Frequency / 1000.0);

    private static readonly int[] Calls = new int[SlotCount];
    private static readonly long[] Ticks = new long[SlotCount];
    private static readonly long[] MaxTicks = new long[SlotCount];
    private static readonly int[] Depth = new int[SlotCount];
    private static readonly long[] Started = new long[SlotCount];
    private static readonly long[][] Samples = NewSamples();

    private static long _lastShow;
    private static string _item = "";
    private static string _peakFrameItem = "";

    private static long[][] NewSamples()
    {
        var all = new long[SlotCount][];
        for (int i = 0; i < SlotCount; i++) all[i] = new long[SampleCap];
        return all;
    }

    internal static void Install(PatchScope scope)
    {
        // 前缀取最高优先级、收尾取最低：尽量把本作和其他模组挂在同一方法上的补丁都包进来
        Wrap(scope, AccessTools.Method(typeof(GameItemElement), "GetTooltip", Type.EmptyTypes),
            nameof(EnterTooltip), nameof(ExitTooltip));
        Wrap(scope, AccessTools.Method(typeof(GameItemElement), "GetTooltipBasic", Type.EmptyTypes),
            nameof(EnterBasic), nameof(ExitBasic));
        Wrap(scope, AccessTools.Method(typeof(RichTextBuilder), "Build", new[] { typeof(int) }),
            nameof(EnterBuild), nameof(ExitBuild));
        Wrap(scope, AccessTools.Method(typeof(OverlayHandler), "ShowTooltip",
                new[] { typeof(UnityEngine.Vector2), typeof(UnityEngine.Vector2), typeof(RichText), typeof(int) }),
            nameof(EnterShow), nameof(ExitShow));
        Wrap(scope, AccessTools.Method(typeof(RichTextElement), "SetRichText",
                new[] { typeof(RichText), typeof(int) }),
            nameof(EnterSetRichText), nameof(ExitSetRichText));
        scope.OnDispose(Reset);
    }

    private static void Wrap(PatchScope scope, System.Reflection.MethodBase? target, string enter, string exit)
    {
        if (!scope.Patch(target,
                prefix: new HarmonyMethod(typeof(HotPathPerf), enter) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(HotPathPerf), exit) { priority = Priority.Last }))
            Log.Warn("HotPathPerf", "计时补丁没打上：" + enter);
    }

    // ---------------- 调用点计时（本作自己的代码） ----------------

    internal static long Start() => Stopwatch.GetTimestamp();

    internal static void Stop(Slot slot, long started)
    {
        long now = Stopwatch.GetTimestamp();
        if (!Hovering(now)) return;   // 不悬停时也在跑的代码（如名称标签的定时刷新）不算进悬停开销
        Add((int)slot, now - started);
    }

    /// <summary>
    /// 是否处在一段连续悬停里。一帧的顺序是 GetTooltip → ShowTooltip，所以构建进行中也算：
    /// 新一段悬停的第一次构建发生在它的第一次 ShowTooltip 之前。
    /// </summary>
    private static bool Hovering(long now)
        => Depth[(int)Slot.GetTooltip] > 0 || Depth[(int)Slot.Show] > 0
           || (_lastShow != 0 && now - _lastShow <= FrameGapLimitTicks);

    // ---------------- 阶段计时（补丁） ----------------

    private static void EnterTooltip(GameItemElement __instance)
    {
        if (!Enter(Slot.GetTooltip)) return;
        // 上一段悬停已经中断：先把它收尾，这次构建记到新的一段里
        long now = Started[(int)Slot.GetTooltip];
        if (_lastShow != 0 && now - _lastShow > FrameGapLimitTicks)
        {
            EndStreak();
            _lastShow = 0;   // 已收尾：紧接着的 ShowTooltip 不再按中断处理，否则会清掉这次构建的样本
        }
        try { _item = __instance != null ? __instance.identifier ?? "" : ""; } catch { _item = ""; }
    }

    private static void ExitTooltip() => Exit(Slot.GetTooltip);

    private static void EnterBasic() => Enter(Slot.Basic);
    private static void ExitBasic() => Exit(Slot.Basic);

    // Build 到处都在用（日志、面板、按钮说明）；只算物品 tooltip 构建期间的
    private static void EnterBuild()
    {
        if (Depth[(int)Slot.GetTooltip] > 0 || Depth[(int)Slot.Build] > 0) Enter(Slot.Build);
    }

    private static void ExitBuild()
    {
        if (Depth[(int)Slot.Build] > 0) Exit(Slot.Build);
    }

    private static void EnterShow()
    {
        if (!Enter(Slot.Show)) return;
        long now = Started[(int)Slot.Show];
        if (_lastShow != 0)
        {
            long gap = now - _lastShow;
            if (gap <= FrameGapLimitTicks)
            {
                if (gap > MaxTicks[(int)Slot.Frame]) _peakFrameItem = _item;
                Add((int)Slot.Frame, gap);
            }
            else
            {
                // 不经 GetTooltip 的 tooltip（非物品）也会走到这里：同样按新的一段处理
                EndStreak();
            }
        }
        _lastShow = now;
    }

    private static void ExitShow()
    {
        Exit(Slot.Show);
        // 悬停时长按帧间隔累计，中断的空档不在里面
        if (Depth[(int)Slot.Show] == 0
            && Ticks[(int)Slot.Frame] / (double)Stopwatch.Frequency >= WindowSeconds)
            Flush();
    }

    // 同理，只算 tooltip 提交期间的 SetRichText
    private static void EnterSetRichText()
    {
        if (Depth[(int)Slot.Show] > 0 || Depth[(int)Slot.SetRichText] > 0) Enter(Slot.SetRichText);
    }

    private static void ExitSetRichText()
    {
        if (Depth[(int)Slot.SetRichText] > 0) Exit(Slot.SetRichText);
    }

    /// <summary>嵌套调用只算最外层；返回这次是不是最外层。</summary>
    private static bool Enter(Slot slot)
    {
        int i = (int)slot;
        if (Depth[i]++ > 0) return false;
        Started[i] = Stopwatch.GetTimestamp();
        return true;
    }

    private static void Exit(Slot slot)
    {
        int i = (int)slot;
        if (Depth[i] <= 0) { Depth[i] = 0; return; }
        if (--Depth[i] == 0) Add(i, Stopwatch.GetTimestamp() - Started[i]);
    }

    private static void Add(int slot, long ticks)
    {
        int n = Calls[slot]++;
        if (n < SampleCap) Samples[slot][n] = ticks;
        Ticks[slot] += ticks;
        if (ticks > MaxTicks[slot]) MaxTicks[slot] = ticks;
    }

    // ---------------- 输出 ----------------

    /// <summary>一段悬停中断了：有帧就输出这一段（哪怕不足 2 秒），然后清零重新开始。</summary>
    private static void EndStreak()
    {
        if (Calls[(int)Slot.Frame] > 0) Flush();
        else Clear();
    }

    private static void Flush()
    {
        double hoverSeconds = Ticks[(int)Slot.Frame] / (double)Stopwatch.Frequency;
        var line = new StringBuilder("[perf] 热路径（悬停 ")
            .Append(hoverSeconds.ToString("0.0")).Append(" 秒，次数/中位/均次/峰值 ms）:");
        for (int i = 0; i < SlotCount; i++)
        {
            if (Calls[i] == 0) continue;
            line.Append(' ').Append(Names[i]).Append('=').Append(Calls[i]).Append('/')
                .Append(Ms(Median(i))).Append('/')
                .Append(Ms(Ticks[i] / (double)Calls[i])).Append('/').Append(Ms(MaxTicks[i]));
        }
        line.Append("；物品=").Append(_item);
        if (_peakFrameItem.Length > 0 && _peakFrameItem != _item) line.Append("，峰值帧物品=").Append(_peakFrameItem);
        Log.Msg(line.ToString());
        Clear();
    }

    /// <summary>已留样本的中位数（原地排序；随后就清零，顺序不必保留）。</summary>
    private static double Median(int slot)
    {
        int n = Math.Min(Calls[slot], SampleCap);
        if (n == 0) return 0.0;
        long[] samples = Samples[slot];
        Array.Sort(samples, 0, n);
        return n % 2 == 1 ? samples[n / 2] : (samples[n / 2 - 1] + samples[n / 2]) / 2.0;
    }

    private static string Ms(double ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("0.00");

    /// <summary>清掉累计值；进行中的调用深度与起点不动，否则正在计时的那一次会对不上。</summary>
    private static void Clear()
    {
        Array.Clear(Calls, 0, SlotCount);
        Array.Clear(Ticks, 0, SlotCount);
        Array.Clear(MaxTicks, 0, SlotCount);
        _peakFrameItem = "";
    }

    private static void Reset()
    {
        Clear();
        Array.Clear(Depth, 0, SlotCount);
        _lastShow = 0;
        _item = "";
    }
}
#endif
