using System;
using System.Text;

namespace ItemDetails;

/// <summary>
/// Item Details（正式版 / 官方 IMod 路线）
///
/// 在物品 tooltip 里按标签追加真伪、价值构成、容器、状态、水质等信息；
/// 并通过独立补丁处理鉴定标题、双击鉴定和顾客提示。
///
/// 主 tooltip 使用官方钩子 ModHook.OnCreateTooltipLate
/// （event Action&lt;RichTextBuilder, GameItem&gt;），只读原版数据。
/// 注意：Init/OnEnable 在主菜单阶段调用，此时存档尚未加载，只做钩子订阅，不访问游戏管理器。
///
/// 文件分工：本文件只放生命周期、钩子订阅与渲染器边界；各追加器在
/// <c>ModEntry.Tooltip.cs</c>，改 tooltip 内容不需要碰这里。
/// </summary>
public sealed partial class ModEntry : IMod
{
    private ModLog? _log;
    private Action<RichTextBuilder, GameItem>? _cb;

#if ITEMDETAILS_SELFTEST
    /// <summary>当前生效的实例，供无头快照测试复用同一套渲染器。</summary>
    internal static ModEntry? Active { get; private set; }

    /// <summary>
    /// 无头快照：对任意物品跑一遍全部渲染器，返回纯文本（已剥富文本标记）。
    /// 用于自动化测试 tooltip 内容，不必悬停、不必看屏幕。
    /// </summary>
    internal static string? RenderTooltipText(GameItem? item, int width = 60)
    {
        try
        {
            var mod = Active;
            if (mod == null || item == null) return null;

            var builder = new RichTextBuilder(width);
            mod.RunRenderers(builder, item);
            return builder.Build()?.nakedText;
        }
        catch (Exception ex)
        {
            Log.Warn("RenderTooltipText", ex);
            return null;
        }
    }
#endif

    /// <summary>一个 tooltip 追加器：名字只用于日志定位，执行顺序即 <see cref="Renderers"/> 的顺序。</summary>
    private readonly struct RendererDef
    {
        internal readonly string Name;
        internal readonly Action<ModEntry, RichTextBuilder, GameItem> Append;

        internal RendererDef(string name, Action<ModEntry, RichTextBuilder, GameItem> append)
        {
            Name = name;
            Append = append;
        }
    }

    /// <summary>
    /// tooltip 追加器按固定顺序执行；新增模块只改这里和对应方法。
    /// 用 Action 而不是 Func&lt;bool&gt;：追加成功与否没有调用方消费，
    /// 返回值只会变成没人读的摆设（旧版就是如此）。
    /// </summary>
    private static readonly RendererDef[] Renderers =
    {
        new("auth", (m, b, i) => m.TryAppendAuthenticity(b, i)),
        new("value", (m, b, i) => m.TryAppendValue(b, i)),
        new("ribwich", (m, b, i) => m.TryAppendRibwichReasons(b, i)),
        new("injected-value", (m, b, i) => m.TryAppendInjectedValue(b, i)),
        new("berry", (m, b, i) => m.TryAppendBerry(b, i)),
        new("hidden", (m, b, i) => m.TryAppendHidden(b, i)),
        new("value-enhance", (m, b, i) => m.TryAppendValueEnhance(b, i)),
        new("feature-reveal", (m, b, i) => m.TryAppendFeatureReveal(b, i)),
        new("storage", (m, b, i) => m.TryAppendStorage(b, i)),
        new("machine-interior", (m, b, i) => m.TryAppendMachineInterior(b, i)),
        new("module-learning", (m, b, i) => m.TryAppendModuleLearning(b, i)),
        new("conditions", (m, b, i) => m.TryAppendConditions(b, i)),
        new("contraband", (m, b, i) => m.TryAppendContraband(b, i)),
        new("lottery", (m, b, i) => m.TryAppendLottery(b, i)),
        new("water", (m, b, i) => m.TryAppendWater(b, i)),
        new("sealed-box", (m, b, i) => m.TryAppendSealedBox(b, i)),
        new("scratcher", (m, b, i) => m.TryAppendScratcher(b, i)),
        new("seed", (m, b, i) => m.TryAppendSeed(b, i)),
        new("water-advice", (m, b, i) => m.TryAppendWaterAdvice(b, i)),
        new("wine", (m, b, i) => m.TryAppendWine(b, i)),
        new("market", (m, b, i) => m.TryAppendMarket(b, i)),
    };

    /// <summary>连续失败到这个次数就停用该模块：坏的逻辑不该每次悬停都拖累整段 tooltip。</summary>
    private const int RendererFailureLimit = 3;

    private readonly int[] _rendererFailures = new int[Renderers.Length];

    public void Init(ModManifest manifest)
    {
        _log = new ModLog(manifest);
        ModSettings.Init(manifest.ID);
        _log.Log($"{manifest.Name} v{manifest.ModVersion} initialized.");

        // 无头自检：命令行 -itemdetails-selftest，或工具放入的一次性 selftest.flag；
        // 正常游玩两者都没有，不受影响。仅 -p:SelfTest=true 构建时编入。
#if ITEMDETAILS_SELFTEST
        try { SelfTest.MaybeStart(manifest.ID); }
        catch (Exception ex) { Log.Error("SelfTest", ex); }
#endif
    }

    public void OnEnable()
    {
#if ITEMDETAILS_SELFTEST
        Active = this;
#endif
        Log.Reset();   // 上次启停留下的失败计数不再沿用，重新按本次会话限流
        Array.Clear(_rendererFailures, 0, _rendererFailures.Length);

        var cb = OnTooltipLate;
        _cb = cb;

        Try("Subscribe", () => ModHook.OnCreateTooltipLate += cb);
        Try("AppraisalTitlePatch", AppraisalTitlePatch.Apply);
        Try("ResultPrefill", ResultPrefill.Apply);
        Try("CustomerAlertTicker", CustomerAlertTicker.Install);
        Try("CustomerHistory", CustomerHistory.Install);
        Try("LabelImpact", LabelImpact.Apply);
        Try("DoubleClickInspect", () =>
        {
            DoubleClickInspect.Apply();
            DoubleClickInspect.Install();
        });
        Try("TooltipPipeline", TooltipPipeline.Apply);
        Try("MachineBattery", MachineBatteryInfo.Apply);
        Try("ProbeDestroyGuard", ProbeDestroyGuard.Apply);
        Try("WaterWarmup", WaterWarmupPatch.Apply);
        Try("ConfigSectionRefresh", ConfigSectionRefresh.Apply);
        Try("TypeBadge", TypeBadge.Apply);
        Try("ProgressBarStyle", ProgressBarStyle.Apply);
        Try("ItemNameLabel", ItemNameLabel.Apply);
    }

    public void OnDisable()
    {
        Quiet(() =>
        {
            if (_cb != null) ModHook.OnCreateTooltipLate -= _cb;
        });
        _cb = null;
#if ITEMDETAILS_SELFTEST
        if (ReferenceEquals(Active, this)) Active = null;
#endif

        Quiet(AppraisalTitlePatch.Remove);
        Quiet(ResultPrefill.Remove);
        Quiet(CustomerAlertTicker.Stop);
        Quiet(CustomerHistory.Stop);
        Quiet(WaterWarmupPatch.Remove);
        Quiet(WaterAdvice.Stop);
        Quiet(ProbeDestroyGuard.Remove);
        Quiet(ItemValueCache.Reset);
        Quiet(ValueEnhance.Stop);
        Quiet(StorageInfo.Stop);
        Quiet(MachineInterior.Stop);
        Quiet(ModuleStatEffects.Stop);
        Quiet(SealedBoxInfo.Stop);
        Quiet(LabelImpact.Remove);
        Quiet(DoubleClickInspect.Remove);
        Quiet(TooltipPipeline.Remove);
        Quiet(MachineBatteryInfo.Remove);
        Quiet(ConfigSectionRefresh.Remove);
        Quiet(TypeBadge.Remove);
        Quiet(ProgressBarStyle.Remove);
        Quiet(ItemNameLabel.Remove);

        Array.Clear(_rendererFailures, 0, _rendererFailures.Length);
        Quiet(Loc.ResetCache);   // 下次启用重新读盘：玩家可能换了语言，或更新了 Localization/*.csv
        _log?.Log("unsubscribed");
        Log.Reset();
    }

    /// <summary>装配步骤互不影响：一步失败只记一条日志，其它功能照常启用。</summary>
    private void Try(string name, Action action)
    {
        try
        {
            action();
            _log?.Log(name + " ok");
        }
        catch (Exception ex)
        {
            Log.Error(name, ex);
        }
    }

    /// <summary>拆卸路径不关心失败，失败也不该拦住后续清理。</summary>
    private static void Quiet(Action action)
    {
        try { action(); } catch { }
    }

    // ---------------- 语言 ----------------
    // 语言判定与文案选择都在 <see cref="Loc"/>：tooltip 构建期间由 Loc 内部按语言码缓存，
    // 这里不再需要把 bool zh 穿进每个模块（旧版就是那么做的，新增模块必漏）。

    /// <summary>含水容器：由水质模块统一呈现，其它物品维度模块对它豁免。</summary>
    private static bool IsLiquid(GameItem item)
    {
        try { return item.IsTag("LIQUID_CONTAINER_TAG"); }
        catch { return false; }
    }

    // ---------------- 钩子回调与渲染器边界 ----------------

    private void OnTooltipLate(RichTextBuilder builder, GameItem item)
    {
        if (builder == null || item == null) return;

        RunRenderers(builder, item);
    }

    /// <summary>
    /// 唯一的渲染边界：一个模块抛错只损失它自己，后面的模块照常执行。
    /// 偶发失败只记一次日志并在下条 tooltip 重试；连续失败
    /// <see cref="RendererFailureLimit"/> 次后本次会话停用该模块，日志点出名字。
    /// 无头快照与实机 tooltip 走同一条路径，避免"测的是一套、跑的是另一套"。
    /// </summary>
    private void RunRenderers(RichTextBuilder builder, GameItem item)
    {
#if ITEMDETAILS_DIAG
        double totalMs = 0;
#endif

        for (int i = 0; i < Renderers.Length; i++)
        {
            if (_rendererFailures[i] >= RendererFailureLimit) continue;

            RendererDef def = Renderers[i];
#if ITEMDETAILS_DIAG
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            try
            {
                def.Append(this, builder, item);
                _rendererFailures[i] = 0;
            }
            catch (Exception ex)
            {
                int failures = ++_rendererFailures[i];
                if (failures == 1)
                {
                    Log.Warn("Tooltip." + def.Name, ex);
                }
                else if (failures == RendererFailureLimit)
                {
                    Log.Error("Tooltip." + def.Name,
                        $"{def.Name} 连续失败 {failures} 次，本次会话停用该模块；" +
                        $"其它模块不受影响。{ex.GetType().Name} - {ex.Message}");
                }
            }

#if ITEMDETAILS_DIAG
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency;
            totalMs += ms;
            _perfModuleCalls[i]++;
            _perfModuleMs[i] += ms;
            if (ms > _perfModuleMaxMs[i]) _perfModuleMaxMs[i] = ms;
#endif
        }

#if ITEMDETAILS_DIAG
        TrackRendererPerf(item, totalMs);
#endif
    }

#if ITEMDETAILS_DIAG
    // 正文独立性能窗口：单次阈值日志会漏掉每次不足 2ms、但每帧都发生的持续开销。
    private readonly int[] _perfModuleCalls = new int[Renderers.Length];
    private readonly double[] _perfModuleMs = new double[Renderers.Length];
    private readonly double[] _perfModuleMaxMs = new double[Renderers.Length];
    private int _perfRuns;
    private double _perfTotalMs;
    private double _perfMaxTotalMs;
    private string _perfMaxItem = "";
    private float _perfLogAt;

    /// <summary>每 2 秒输出总构建与累计最慢的前 4 个模块，避免逐条日志干扰性能。</summary>
    private void TrackRendererPerf(GameItem item, double totalMs)
    {
        _perfRuns++;
        _perfTotalMs += totalMs;
        if (totalMs > _perfMaxTotalMs)
        {
            _perfMaxTotalMs = totalMs;
            _perfMaxItem = item.identifier ?? "";
        }

        float now = UnityEngine.Time.unscaledTime;
        if (_perfLogAt <= 0f) _perfLogAt = now + 2f;
        if (now < _perfLogAt) return;

        var order = new int[Renderers.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => _perfModuleMs[b].CompareTo(_perfModuleMs[a]));

        var detail = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < order.Length && shown < 4; i++)
        {
            int index = order[i];
            int calls = _perfModuleCalls[index];
            if (calls == 0) continue;
            double sum = _perfModuleMs[index];
            detail.Append(' ').Append(Renderers[index].Name).Append('=')
                .Append(sum.ToString("0.00")).Append('/')
                .Append((sum / calls).ToString("0.00")).Append('/')
                .Append(_perfModuleMaxMs[index].ToString("0.00")).Append("ms");
            shown++;
        }

        double average = _perfRuns > 0 ? _perfTotalMs / _perfRuns : 0.0;
        Log.Msg($"[perf] tooltip 正文（2秒）: 构建={_perfRuns}次/{average:0.00}ms均次/" +
                $"{_perfMaxTotalMs:0.00}ms峰值（{_perfMaxItem}）；模块前4（累计/均次/峰值）:{detail}");

        Array.Clear(_perfModuleCalls, 0, _perfModuleCalls.Length);
        Array.Clear(_perfModuleMs, 0, _perfModuleMs.Length);
        Array.Clear(_perfModuleMaxMs, 0, _perfModuleMaxMs.Length);
        _perfRuns = 0;
        _perfTotalMs = 0.0;
        _perfMaxTotalMs = 0.0;
        _perfMaxItem = "";
        _perfLogAt = now + 2f;
    }
#endif
}
