using System;

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
            mod.RefreshLocale();

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
        new("generic", (m, b, i) => m.TryAppendGeneric(b, i)),
        new("berry", (m, b, i) => m.TryAppendBerry(b, i)),
        new("hidden", (m, b, i) => m.TryAppendHidden(b, i)),
        new("value-enhance", (m, b, i) => m.TryAppendValueEnhance(b, i)),
        new("feature-reveal", (m, b, i) => m.TryAppendFeatureReveal(b, i)),
        new("storage", (m, b, i) => m.TryAppendStorage(b, i)),
        new("conditions", (m, b, i) => m.TryAppendConditions(b, i)),
        new("module-effects", (m, b, i) => m.TryAppendModuleEffects(b, i)),
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
        Active = this;
        Log.Reset();   // 上次启停留下的失败计数不再沿用，重新按本次会话限流
        Array.Clear(_rendererFailures, 0, _rendererFailures.Length);

        var cb = OnTooltipLate;
        _cb = cb;

        Try("Subscribe", () => ModHook.OnCreateTooltipLate += cb);
        Try("AppraisalTitlePatch", AppraisalTitlePatch.Apply);
        Try("CustomerAlertTicker", CustomerAlertTicker.Install);
        Try("LabelImpact", LabelImpact.Apply);
        Try("DoubleClickInspect", () =>
        {
            DoubleClickInspect.Apply();
            DoubleClickInspect.Install();
        });
        Try("TooltipPipeline", TooltipPipeline.Apply);
        Try("ConfigSectionRefresh", ConfigSectionRefresh.Apply);
        Try("TypeBadge", TypeBadge.Apply);
    }

    public void OnDisable()
    {
        Quiet(() =>
        {
            if (_cb != null) ModHook.OnCreateTooltipLate -= _cb;
        });
        _cb = null;
        if (ReferenceEquals(Active, this)) Active = null;

        Quiet(AppraisalTitlePatch.Remove);
        Quiet(CustomerAlertTicker.Stop);
        Quiet(WaterAdvice.Stop);
        Quiet(LabelImpact.Remove);
        Quiet(DoubleClickInspect.Remove);
        Quiet(TooltipPipeline.Remove);
        Quiet(ConfigSectionRefresh.Remove);
        Quiet(TypeBadge.Remove);

        Array.Clear(_rendererFailures, 0, _rendererFailures.Length);
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
    private bool _zh;   // 每次构建 tooltip 前由 RefreshLocale 同步

    /// <summary>每次构建 tooltip 前同步一次语言；判定与缓存都交给 <see cref="Locales"/>。</summary>
    private void RefreshLocale() => _zh = Locales.IsChinese;

    private string L(string zh, string en) => _zh ? zh : en;

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

        try { RefreshLocale(); }
        catch (Exception ex) { Log.Warn("OnTooltipLate.Locale", ex); }


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
        for (int i = 0; i < Renderers.Length; i++)
        {
            if (_rendererFailures[i] >= RendererFailureLimit) continue;

            RendererDef def = Renderers[i];
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
        }
    }
}
