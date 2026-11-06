using System;
using System.Collections.Generic;
using System.Text;
using ItemDetails.Integration;

namespace ItemDetails;

/// <summary>
/// Item Details（正式版 / 官方 IMod 路线）
///
/// 在物品 tooltip 里按标签追加真伪、价值构成、容器、状态、水质等信息；
/// 并通过独立补丁处理鉴定标题、双击鉴定/扫描和顾客提示。
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

    /// <summary>
    /// 一个 tooltip 追加器：名字用于日志，信息块用于第三方接管，归属组用于告诉排版方这一块该归到哪个小节，
    /// 执行顺序即数组顺序。新增模块只改这张表，不改分发代码。
    /// </summary>
    private readonly struct RendererDef
    {
        internal readonly string Name;
        internal readonly ItemDetailsTooltipSection Section;
        internal readonly string Group;
        internal readonly Func<ModEntry, TooltipSections.Writer, GameItem, bool> Append;

        internal RendererDef(string name, ItemDetailsTooltipSection section, string group,
            Func<ModEntry, TooltipSections.Writer, GameItem, bool> append)
        {
            Name = name;
            Section = section;
            Group = group;
            Append = append;
        }
    }

    /// <summary>
    /// tooltip 追加器按固定顺序执行；新增模块只改这里和对应方法。
    /// storage 同时含压缩率和内容物，在采集器内分别判断，因此这里标为 None。
    /// 第三列是归属组（见 SDK 的 <see cref="ItemDetails.Integration.TooltipGroupIds"/>）：区块快照里的 group 字段取自它，
    /// 排版方按组归类，不必为每个新 id 补映射。
    /// </summary>
    private static readonly RendererDef[] Renderers =
    {
        new("auth", ItemDetailsTooltipSection.Authenticity, TooltipGroupIds.Appraisal, (m, b, i) => m.TryAppendAuthenticity(b, i)),
        new("value", ItemDetailsTooltipSection.ValueBreakdown, TooltipGroupIds.Appraisal, (m, b, i) => m.TryAppendValue(b, i)),
        new("ribwich", ItemDetailsTooltipSection.Authenticity, TooltipGroupIds.Appraisal, (m, b, i) => m.TryAppendRibwichReasons(b, i)),
        new("injected-value", ItemDetailsTooltipSection.ValueBreakdown, TooltipGroupIds.Appraisal, (m, b, i) => m.TryAppendInjectedValue(b, i)),
        new("berry", ItemDetailsTooltipSection.BerryQuality, TooltipGroupIds.Food, (m, b, i) => m.TryAppendBerry(b, i)),
        new("hidden", ItemDetailsTooltipSection.HiddenInfo, TooltipGroupIds.Condition, (m, b, i) => m.TryAppendHidden(b, i)),
        new("value-enhance", ItemDetailsTooltipSection.ValueEnhance, TooltipGroupIds.Price, (m, b, i) => m.TryAppendValueEnhance(b, i)),
        new("feature-reveal", ItemDetailsTooltipSection.HiddenInfo, TooltipGroupIds.Condition, (m, b, i) => m.TryAppendFeatureReveal(b, i)),
        new("storage", ItemDetailsTooltipSection.None, TooltipGroupIds.Contents, (m, b, i) => m.TryAppendStorage(b, i)),
        new("machine-interior", ItemDetailsTooltipSection.MachineInterior, TooltipGroupIds.Machine, (m, b, i) => m.TryAppendMachineInterior(b, i)),
        new("module-learning", ItemDetailsTooltipSection.ModuleLearning, TooltipGroupIds.Module, (m, b, i) => m.TryAppendModuleLearning(b, i)),
        new("conditions", ItemDetailsTooltipSection.ConditionEffects, TooltipGroupIds.Condition, (m, b, i) => m.TryAppendConditions(b, i)),
        new("contraband", ItemDetailsTooltipSection.Contraband, TooltipGroupIds.Price, (m, b, i) => m.TryAppendContraband(b, i)),
        new("lottery", ItemDetailsTooltipSection.Lottery, TooltipGroupIds.Lottery, (m, b, i) => m.TryAppendLottery(b, i)),
        new("water", ItemDetailsTooltipSection.LiquidAnalysis, TooltipGroupIds.Liquid, (m, b, i) => m.TryAppendWater(b, i)),
        new("sealed-box", ItemDetailsTooltipSection.Contents, TooltipGroupIds.Contents, (m, b, i) => m.TryAppendSealedBox(b, i)),
        new("scratcher", ItemDetailsTooltipSection.Scratcher, TooltipGroupIds.Lottery, (m, b, i) => m.TryAppendScratcher(b, i)),
        new("seed", ItemDetailsTooltipSection.Seed, TooltipGroupIds.Plant, (m, b, i) => m.TryAppendSeed(b, i)),
        new("water-advice", ItemDetailsTooltipSection.WaterAdvice, TooltipGroupIds.Liquid, (m, b, i) => m.TryAppendWaterAdvice(b, i)),
        new("wine", ItemDetailsTooltipSection.Wine, TooltipGroupIds.Brew, (m, b, i) => m.TryAppendWine(b, i)),
        new("meat", ItemDetailsTooltipSection.Meat, TooltipGroupIds.Food, (m, b, i) => m.TryAppendMeat(b, i)),
        new("market", ItemDetailsTooltipSection.Market, TooltipGroupIds.Price, (m, b, i) => m.TryAppendMarket(b, i)),
    };

    /// <summary>连续失败到这个次数就停用该模块：坏的逻辑不该每次悬停都拖累整段 tooltip。</summary>
    private const int RendererFailureLimit = 3;

    private readonly int[] _rendererFailures = new int[Renderers.Length];

    /// <summary>已装配的作用域：每个功能一个，停用时逆序 Dispose，补丁与清理动作成对撤销。</summary>
    private readonly List<PatchScope> _scopes = new();

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
        // 一次性测试套件：ModData/item_details/testkit.flag（内容为槽位 ID）
        try { TestKit.MaybeStart(manifest.ID); }
        catch (Exception ex) { Log.Error("TestKit", ex); }
        // tooltip 正文导出：ModData/item_details/tooltipdump.flag（内容为槽位 ID）
        try { TooltipDump.MaybeStart(manifest.ID); }
        catch (Exception ex) { Log.Error("TooltipDump", ex); }
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

        Try("LiquidQueries", LiquidQueries.Enable);
        Try("TooltipSections", TooltipSections.Enable);
        Try("Subscribe", () => ModHook.OnCreateTooltipLate += cb);

        // 关键：所有 Harmony 装配推迟到游戏语言真正应用之后再执行。
        // 在 ModLoader.Awake 阶段就 Patch 会 Compile 目标方法，Mono 于 JIT 期执行其引用的
        // 类型静态构造：`GameItemElement.GetTooltipBasic` 引用 `WaterFeatureHelper`/`WaterHelper`，
        // 于是水质档位名在 locale 仍是默认 en 时被固化，随存档保存后交易面板显示英文，
        // `WaterHelper.WillAcceptWater` 也因常量与动态中文名不一致而永远拒收水。
        // 详见 src/Infrastructure/DeferredPatchRunner.cs 与 docs/experience/harmony-patch-jit-triggers-localization-ctor.md。
        DeferredPatchRunner.Schedule("ItemDetails.Patches", ApplyPatches);
    }

    /// <summary>语言就绪后一次性装配全部补丁与后台组件。</summary>
    private void ApplyPatches()
    {
        Patch("AppraisalTitlePatch", AppraisalTitlePatch.Install);
        Patch("ResultPrefill", ResultPrefill.Install);
        Try("CustomerAlertTicker", CustomerAlertTicker.Install);
        Patch("CustomerHistory", CustomerHistory.Install);
        Patch("LabelImpact", LabelImpact.Install);
        Patch("DoubleClickInspect", DoubleClickInspect.InstallPatches);
        // 双击判定的 ticker 兜底不是补丁，装配失败也不影响补丁类的那一半。
        Try("DoubleClickInspect.Ticker", DoubleClickInspect.Install);
        Patch("ContextMenuScan", ContextMenuScan.Install);
        Patch("TooltipPipeline", TooltipPipeline.Install);
        Patch("MachineBattery", MachineBatteryInfo.Install);
        Patch("MachineSpeed", MachineSpeedLine.Install);
        Patch("MachineHydroponic", MachineHydroponicLine.Install);
        Patch("ProbeDestroyGuard", ProbeDestroyGuard.Install);
        Patch("WaterWarmup", WaterWarmupPatch.Install);
        Patch("WaterContainerLine", WaterContainerLine.Install);
        Patch("ConfigSectionRefresh", ConfigSectionRefresh.Install);
        Patch("TypeBadge", TypeBadge.Install);
        Patch("ContentsGrid", ContentsGrid.Install);
        Patch("ProgressBarStyle", ProgressBarStyle.Install);
        Patch("RenderSlices", RenderSlices.Install);
        Patch("TooltipWidthFix", TooltipWidthFix.Install);
        Patch("ManufacturerInline", ManufacturerInline.Install);
        Patch("ItemNameLabel", ItemNameLabel.Install);
        // 兜底：别的模组（实测 Pen_mod_QoL_Tweaks / BrewingExpansion）可能在 Awake 阶段
        // Patch 了会 JIT 到 WaterFeatureHelper / ItemConditionList 的方法，把水质档位名
        // 冻成英文。装配完毕后统一按当前语言重写这两张静态表，确保品相与出售判定正确。
        Try("WaterStaticLocalizationFix", WaterStaticLocalizationFix.Refresh);
        // F8 控制台的西文字体没有中文字形；把游戏自带中文字体登记成 TMP 全局回退，
        // 避免中文日志每行都触发 “character not found” 警告并显示成空框。
        Try("ChineseFontFallback", ChineseFontFallback.Apply);
    }

    public void OnDisable()
    {
        Quiet(() =>
        {
            if (_cb != null) ModHook.OnCreateTooltipLate -= _cb;
        });
        _cb = null;
        Quiet(LiquidQueries.Disable);
        Quiet(TooltipSections.Disable);
        // 若语言还没就绪就被停用，取消尚未执行的延迟装配，避免停用后仍偷偷 Patch。
        DeferredPatchRunner.Cancel();
#if ITEMDETAILS_SELFTEST
        if (ReferenceEquals(Active, this)) Active = null;
#endif

        // 先撤销全部补丁与各功能的清理动作（逆序，与装配对称），再清业务缓存：
        // 都在同一主线程且中间不跑游戏代码，顺序本身无关，但先撤补丁可保证清理期间没有回调进来。
        for (int i = _scopes.Count - 1; i >= 0; i--) Quiet(_scopes[i].Dispose);
        _scopes.Clear();

        Quiet(CustomerAlertTicker.Stop);
        Quiet(WaterAdvice.Stop);
        Quiet(ItemValueCache.Reset);
        Quiet(ValueEnhance.Stop);
        Quiet(StorageInfo.Stop);
        Quiet(SealedBoxInfo.Stop);
        Quiet(MachineInterior.Stop);
        Quiet(ModuleStatEffects.Stop);
        Quiet(ScannerInventory.Stop);
        Quiet(ChineseFontFallback.Remove);

        Array.Clear(_rendererFailures, 0, _rendererFailures.Length);
        Quiet(Loc.ResetCache);   // 下次启用重新读盘：玩家可能换了语言，或更新了 Localization/*.csv
        _log?.Log("unsubscribed");
        Log.Reset();
    }

    /// <summary>装配一个功能：它自己的补丁与清理登记在独立作用域里，失败只回滚这一个。</summary>
    private void Patch(string name, Action<PatchScope> install)
    {
        var scope = new PatchScope(name);
        try
        {
            install(scope);
            _scopes.Add(scope);
            _log?.Log(name + " ok");
        }
        catch (Exception ex)
        {
            scope.Dispose();   // 已经打上的补丁回滚，不留半条
            Log.Error(name, ex);
        }
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
        TypeBadge.Publish(builder, item);
        if (!ModSettings.AnyEnabled) return;
        var suppressed = ThirdPartyProviders.TooltipSuppressions(item);
#if ITEMDETAILS_DIAG
        double totalMs = 0;
#endif

        for (int i = 0; i < Renderers.Length; i++)
        {
            RendererDef def = Renderers[i];
            if (_rendererFailures[i] >= RendererFailureLimit) continue;
            if ((suppressed & def.Section) != 0) continue;

#if ITEMDETAILS_DIAG
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            try
            {
                var output = new TooltipSections.Writer(builder, item, def.Name, def.Group);
                if (def.Append(this, output, item)) output.Finish();
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
