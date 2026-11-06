using System;
using ItemDetails.Integration;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 统一的 tooltip 构建上下文与行改写。
///
/// 原版整段 tooltip 在 GameItemElement.GetTooltipBasic() 内按顺序拼装：
///   - Prefix/Finalizer 记录当前物品并做深度计数；
///   - RichTextBuilder.AddLine 的前缀按改写是否携带信息分工：
///       带信息的（标签短名与价格影响、卡路里、模组批注、带水质档位的液量、合并价格行）直接写进 builder，
///       区块 runs 与正文是同一份文本，重排方拿到的就是这些信息；
///       只换写法的（购买价格→进价、ml→L、描述折行）正文留原版，后缀挂渲染注解，原版窗口 Build 前换入；
///       估价行、空「模组效果：」表头、已并入价格行的购买价格行直接丢弃；
///   - RichTextBuilder.AddEmptyLine 跳过价格块前的占位行，避免价格行被缩进。
///
/// 描述淡化只改颜色、放大镜提示描述的是本作改过的操作，两者留在逻辑层直接改。
///
/// 所有状态集中在这里，Finalizer 在正常和异常路径都会清理。这样避免
/// TooltipContext / EstimateLineHider / DescriptionDim / PriceTerms
/// 多套上下文和多组 Harmony 补丁互相依赖。
/// </summary>
internal static class TooltipPipeline
{
    private static int _depth;
    private static int _buildId;
    private static GameItem? _current;
    private static bool _suppressEstimate;

    private static string? _estimatePrefix;
    private static string? _estimateLocale;

    /// <summary>当前是否在 GetTooltipBasic 构建物品 tooltip 期间（含其中的官方钩子与原版 helper）。</summary>
    internal static bool InTooltip => _depth > 0;

    // 价格术语本地化：用哨兵数字反查模板，避免硬编码原版文案
    private static string _priceLocale = "";
    private static string? _estHead;    // 「预估价值」前缀（含标点）
    private static string? _estMid;     // 两个数字之间的「基础价值」段（含标点）
    private static string? _buyHead;    // 「购买价格」前缀（含标点）
    private static string? _totalHead;  // 堆叠物品「总价」前缀（含标点）
    private static bool _priceEmbeddedBuy;   // 本次价格块是否已把进价写进括号
    private static bool _perCellAfterBuy;    // 堆叠物品：性价比等进价行写完再补

    /// <summary>
    /// 用作用域登记 tooltip 主链路的三个补丁；停用时由作用域撤销，并清空本次构建的状态。
    /// </summary>
    internal static void Install(PatchScope scope)
    {
        var basic = AccessTools.Method(typeof(GameItemElement), "GetTooltipBasic", Type.EmptyTypes);
        var addLine = AccessTools.Method(typeof(RichTextBuilder), "AddLine");
        if (basic == null || addLine == null)
        {
            Log.Warn("TooltipPipeline.Apply", "未找到 GetTooltipBasic/AddLine，tooltip 改写跳过");
            return;
        }

        scope.Patch(basic,
            prefix: new HarmonyMethod(typeof(TooltipPipeline), nameof(BasicPrefix)),
            finalizer: new HarmonyMethod(typeof(TooltipPipeline), nameof(BasicFinalizer)));
        scope.Patch(addLine,
            prefix: new HarmonyMethod(typeof(TooltipPipeline), nameof(AddLinePrefix)),
            postfix: new HarmonyMethod(typeof(TooltipPipeline), nameof(AddLinePostfix)));
        // 记录 AddEmptyLine：它内部是 `NextLine(); Add("<size=Npx> </size>")`，
        // 并不结束占位行，于是紧随其后的价格行会与之同行 → 表现为固定缩进。
        scope.Patch(AccessTools.Method(typeof(RichTextBuilder), "AddEmptyLine"),
            prefix: new HarmonyMethod(typeof(TooltipPipeline), nameof(AddEmptyLinePrefix)));

        scope.OnDispose(ResetHost);
        Log.Msg("TooltipPipeline: patched GetTooltipBasic + AddLine + AddEmptyLine");
    }

    private static void ResetHost()
    {
        Reset();
        _depth = 0;
        _estimatePrefix = null;
        _estimateLocale = null;
    }

    // ---------------- tooltip 生命周期 ----------------

    private static void BasicPrefix(GameItemElement __instance)
    {
        _depth++;
        if (_depth > 1) return;   // 嵌套构建时保留外层上下文

        Reset();
        _current = __instance;
        _buildId++;

        // 分类必须在任何 Add 之前记录：原版 CreateItemTypeTooltip 在构建早期就把
        // " [类型]" 加进正文，记晚了既抑制不到，也会让浮窗用到上一件物品的残留。
        try { TypeBadge.NoteItem(__instance); }
        catch (Exception ex) { Log.Warn("TooltipPipeline.NoteItem", ex); }
        ContentsGrid.Begin();

        try
        {
            _suppressEstimate = ShouldHideEstimate(__instance);
        }
        catch (Exception ex)
        {
            Log.Warn("TooltipPipeline.Prefix", ex);
            _suppressEstimate = false;
        }
    }

    private static void BasicFinalizer(Exception __exception)
    {
        if (_depth > 0) _depth--;
        if (_depth == 0) Reset();
    }

    private static void Reset()
    {
        LiquidQueries.EndTooltip();
        TooltipSections.EndTooltip();
        _current = null;
        _suppressEstimate = false;
        // 价格块状态必须逐次重置：否则上一次 tooltip 把进价写进括号后，
        // 下一个物品的独立「购买价格」行会被误删（跨物品、跨启停都会残留）。
        _priceEmbeddedBuy = false;
        _perCellAfterBuy = false;
        _lineBuilder = null;
        _lineBefore = -1;
        _lineSwap = null;
        _lineWrap = false;
        _lineDepth = 0;
        _afterValueEnhance = false;
    }

    private static bool ShouldHideEstimate(GameItemElement item)
    {
        try
        {
            if (!ModSettings.HideEstimate || !ModSettings.Value) return false;
            if (!GameRead.IsTag(item, "APPRAISAL_ITEM")) return false;
            // 显式提供方接管原版类型时，也只有它实际给出总价才隐藏，避免"两边都没有"。
            if (ThirdPartyProviders.HasAppraisal(item)) return ThirdPartyProviders.Appraisal(item) != null;
            return ValueBreakdown.Analyze(item) != null;
        }
        catch { return false; }
    }

    // ---------------- 行改写 ----------------

    // 这一行的构建状态：前缀记下、后缀用掉即清空。
    //
    // 规则：带信息的改写一律写进 builder 本身（正文与区块 runs 是同一份文本，重排方拿到的就是这些信息）；
    // 渲染层只处理"换写法不换信息"的三种——价格术语换词、ml→L、描述折行。
    // 估价行与空表头不属于信息，直接丢弃，重排方不会看到重复内容。

    private static RichTextBuilder? _lineBuilder;
    private static int _lineBefore = -1;
    private static int _lineDepth;   // 本作自己写行时可能重入；只有最外层那一行登记
    private static string? _lineSwap;
    private static bool _lineWrap;
    private static bool _afterValueEnhance;

    private static void BeginLine(RichTextBuilder builder)
    {
        _lineBuilder = builder;
        _lineBefore = RenderSlices.Count(builder);
        _lineSwap = null;
        _lineWrap = false;
        _afterValueEnhance = false;
    }

    /// <summary>原版窗口里把这一行换成 <paramref name="text"/>；正文与区块 runs 仍是原版原文。</summary>
    private static void Swap(string text) => _lineSwap = text;

    /// <summary>返回 false 表示丢弃这一行（整行由我方写出）。</summary>
    private static bool AddLinePrefix(ref string __0, ref RenderHandler.ColorPalette __2, ref bool __4,
        RichTextBuilder __instance)
    {
        try
        {
            _lineDepth++;
            var cur = _current;
            if (cur == null || __0 == null) return true;
            if (_lineDepth > 1) return true;   // 本作自己写行时的嵌套调用，不重复登记

            BeginLine(__instance);

            // 放大镜提示换成双击提示：这条提示描述的是本 Mod 改过的操作，原版文案对任何读取方都不成立。
            if (!DoubleClickInspect.TryRewriteHint(ref __0, ref __2, ref __4, __instance)) return false;
            LineAnnotations.TryAppend(ref __0, cur);

            // 标签行：短名与价格影响一起写进正文，重排方看到的就是这两项
            LabelImpact.TryRewrite(ref __0, __instance, cur);

            // 估价行：正文里不留；原版窗口里由我方「价值」区块取代
            if (_suppressEstimate)
            {
                string prefix = EstimatePrefix();
                if (prefix.Length > 0 && __0.StartsWith(prefix, StringComparison.Ordinal)) return false;
            }

            var originalColor = __2;
            string originalDescription = __0;
            if (ModSettings.DimDescription
                && !string.IsNullOrEmpty(cur.shortDescription)
                && string.Equals(__0, cur.shortDescription, StringComparison.Ordinal))
            {
                __2 = RenderHandler.ColorPalette.Gray;
            }

            // 描述与背景故事写原文；原版窗口构建时若折行预算被别人的条放宽，渲染层再按常规宽度折行
            // 自学习强化机：Pen 的「当晚训练：X 次」并入质量第三列（质量行构建时取这一行的文字），这里不再单独显示
            if (SelfLearningMachine.CaptureTraining(__instance, __0, cur) && ModSettings.ModuleEffects
                && !TooltipSections.HasListeners)
                return false;   // 有第三方读区块时保留这一行：它们只读规范文本，质量列里只有兜底的额外次数
            // 自学习强化机：说明讲的效率、质量、概率都已写进色块第三列，整段不再显示
            if (SelfLearningMachine.ShouldHideFlavor(__0, cur)) return false;
            bool isDescription = NoteDescription(__0, cur);
            if (isDescription) _lineWrap = true;
            if (__2 != originalColor || __0 != originalDescription)
                TooltipSections.ObserveText(__instance, cur, "description", __0, __2, TooltipGroupIds.Description);

            // 价格：合并价格行写进正文（可认领），只有「购买价格→进价」这种换词挂注解
            if (!RewritePriceTerms(ref __0, __instance, __2)) return false;
            if (!RewriteCalorieLine(ref __0, __instance)) return false;

            // 液量：带水质档位的写进正文；只有 ml→L 的换显示
            RewriteLiquidVolume(ref __0, __instance, __2, cur);

            // 模组加成行：正文与灰色批注都写进 builder
            if (ModuleEffectAnnotation.TryApply(ref __0, __instance, __2, cur)) return false;

            // 模块物品的空「模组效果：」表头（三项加成全 0）整行丢掉
            if (ModuleEffectAnnotation.ShouldDrop(__0, cur)) return false;

            // 机器的「模组效果:」表头：三项加成各带色标后不再需要
            if (ModuleEffectAnnotation.ShouldDropMachineHeader(__0, cur)) return false;
            // 净水器原版单独的「纯度上限」：保留，后面补上限对应的水质档位
            ModuleEffectAnnotation.AnnotatePurityLine(ref __0, cur);

            return true;
        }
        catch { return true; }
    }

    /// <summary>原版液体容器行：` " " + F0 + " ml / " + F0 + " ml"`，单位写死 ml、不走本地化。</summary>
    private static readonly Regex LiquidLine = new(@"^ (\d+) ml / (\d+) ml$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 改写原版液体容器行：
    ///   - 液体组分分析开启时 → ` 2000 / 2000 mL 优质水`（数字与水质档位），与前面的「容器」标签和体积条连成一行；
    ///   - 否则沿用「容器单位优化」：超过 3 L 时把 ml 换成 L。
    /// 两种写法都不本地化单位符号（mL / L 各语言一致），与原版一致。
    ///
    /// 带档位的写进正文（runs 与正文一致）；只有单位换算的挂注解，正文留给第三方。
    /// </summary>
    private static void RewriteLiquidVolume(ref string text, RichTextBuilder builder,
        RenderHandler.ColorPalette color, GameItem item)
    {
        string? display = LiquidVolumeText(text, item, out bool carriesTier);
        if (display == null) return;

        if (carriesTier)
        {
            text = display;
            TooltipSections.ObserveText(builder, item, "liquid-volume", text, color, TooltipGroupIds.Liquid);
            return;
        }

        Swap(display);
        TooltipSections.ObserveText(builder, item, "liquid-volume", text, color, TooltipGroupIds.Liquid);
    }

    /// <summary>显示写法；null 表示这一行按原版显示。<paramref name="carriesTier"/> 为真表示带水质档位信息。</summary>
    private static string? LiquidVolumeText(string text, GameItem item, out bool carriesTier)
    {
        carriesTier = false;
        if (!text.EndsWith(" ml", StringComparison.Ordinal)) return null;
        if (!GameRead.IsTag(item, "LIQUID_CONTAINER_TAG")) return null;
        var m = LiquidLine.Match(text);
        if (!m.Success
            || !int.TryParse(m.Groups[1].Value, out int cur)
            || !int.TryParse(m.Groups[2].Value, out int max)) return null;

        if (!GameRead.IsTag(item, "WINE_CONTAINER_TAG"))
        {
            var r = LiquidQueries.Analyze(item);
            if (r != null)
            {
                bool liters = ModSettings.ContainerUnits && Volume.UseLiters(max);
                carriesTier = true;
                return " " + WaterLayout.Capacity(cur, max, liters, Loc.Culture)
                     + (r.Tier.Length > 0 ? " " + r.Tier : "");
            }
        }

        if (!ModSettings.ContainerUnits || !Volume.UseLiters(cur)) return null;
        return " " + Volume.Number(cur, true, Loc.Culture) + " L / " + Volume.Number(max, true, Loc.Culture) + " L";
    }

    /// <summary>
    /// 原版 Build 的折行宽度：中日韩（字宽缩放 &gt; 1）是 maxCharWidth × 0.66，
    /// 但只要有进度条等 noBreak 片段就整段放宽到 × 0.875，描述也跟着变成一长行把 tooltip 撑宽。
    /// 这里只认原版的两行描述（shortDescription、" " + flavorText），返回 true 让后缀挂折行注解；
    /// 文字保持原文（第三方读到的就是原文），由 <see cref="RenderSlices"/> 在原版窗口构建时按 0.66 预折行。
    /// 含富文本标签的不动，避免切断标签。
    /// </summary>
    private static bool NoteDescription(string text, GameItem item)
    {
        try
        {
            if (!ModSettings.TooltipFormatting || text.Length == 0 || text.IndexOf('<') >= 0) return false;
            if (ModuleItemRows.IsEffectDescription(text)) return true;
            return string.Equals(text, item.shortDescription, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(item.flavorText)
                    && string.Equals(text, " " + item.flavorText, StringComparison.Ordinal));
        }
        catch { return false; }
    }

    /// <summary>原版行写完：把前缀记下的换写法/折行挂成渲染注解。带信息的行不经过这里，它们已写进正文。</summary>
    private static void AddLinePostfix(RichTextBuilder __instance, string __0, RenderHandler.ColorPalette __2,
        bool __3, bool __4, bool __5, bool __6, RenderHandler.ColorPalette __7, RenderHandler.ColorPalette __8,
        RenderHandler.ColorPalette __9)
    {
        if (_lineDepth > 0) _lineDepth--;
        if (_lineDepth != 0) return;   // 内层收尾，外层那一行的状态还留着

        var builder = _lineBuilder;
        _lineBuilder = null;
        if (builder == null) return;

        var cur = _current;
        string? swap = _lineSwap;
        int before = _lineBefore;
        bool wrap = _lineWrap, value = _afterValueEnhance;
        _lineSwap = null;
        _lineWrap = false;
        _afterValueEnhance = false;

        try
        {
            // 前缀返回 false 时原方法不执行、一个片段也没写；这时不能按"末尾几片"去找这一行，
            // 否则会改到上一行。写入数量没变就什么都不做。
            if (ReferenceEquals(builder, __instance) && __0 != null && before >= 0
                && (swap != null || wrap) && RenderSlices.Count(builder) > before)
            {
                RenderSlices.Annotate(builder, before, __0,
                    new RenderSlices.Style(__2, __3, __4, __5, __6, __7, __8, __9), swap);
            }

            if (value && cur != null) ValueEnhance.TryWrite(__instance, cur);
        }
        catch (Exception ex) { Log.Warn("TooltipPipeline.Line", ex); }
    }

    /// <summary>
    /// 原版在价格块前调用 AddEmptyLine(14)，但它不结束占位行，
    /// 会让价格行与之同行（表现为固定缩进）；若再用 NextLine 收尾又会多出空行。
    /// 因此：该物品确实会显示价格行时，只跳过这个 14px 价格占位行——
    /// 其它高度/用途的 AddEmptyLine 保持原样。
    /// </summary>
    private static bool AddEmptyLinePrefix(int heightPixels)
    {
        try
        {
            if (ModSettings.TooltipFormatting && heightPixels == PriceBlockSpacerHeight
                && _current != null && WillShowPriceLine(_current))
                return false;
        }
        catch { }
        return true;
    }

    /// <summary>原版 GetTooltipBasic 里价格块前的占位行高度。</summary>
    private const int PriceBlockSpacerHeight = 14;

    private static bool WillShowPriceLine(GameItem item)
    {
        try
        {
            var value = ItemValueCache.Get(item);
            long sell = value.Sell;
            long list = value.List;
            return sell > 0 || list > 0;
        }
        catch { return false; }
    }

    // ---------------- 本地化前缀 ----------------

    private static string EstimatePrefix()
    {
        string locale = Loc.Code;
        if (_estimatePrefix != null && _estimateLocale == locale) return _estimatePrefix;
        try
        {
            const string probe = "123456789";
            string sample = LocHelper.GetLocalizedMechanic("mech_tooltip_est_value", probe, "987654321");
            int idx = sample.IndexOf(probe, StringComparison.Ordinal);
            _estimatePrefix = idx > 0 ? sample.Substring(0, idx) : "";
        }
        catch (Exception ex)
        {
            Log.Warn("TooltipPipeline.Estimate", ex);
            _estimatePrefix = "";
        }
        _estimateLocale = locale;
        return _estimatePrefix;
    }

    // ---------------- 价格术语本地化 ----------------

    /// <summary>
    /// 把原版三种价格说法换成本作玩家惯用词：
    ///   预估价值 → 售价、基础价值 → 标价、购买价格 → 进价
    /// 原版文案不硬编码：用哨兵数字格式化一次本地化模板，反查出前缀与中间段，
    /// 换语言后会自动重建。
    /// </summary>
    private static bool RewritePriceTerms(ref string text, RichTextBuilder builder, RenderHandler.ColorPalette color)
    {
        try
        {
            if (!ModSettings.TooltipFormatting) return true;
            EnsurePriceTerms();
            if (text == null) return true;

            // 「预估价值: A, 基础价值: B」→ 售价 +（进价 差价）+ 标价
            if (!string.IsNullOrEmpty(_estHead) && !string.IsNullOrEmpty(_estMid)
                && text.StartsWith(_estHead!, StringComparison.Ordinal))
            {
                var cur = _current;

                // 合并价格行写进正文：第三方（含按「售价…（进价…） 标价…」解析的估价单）读到的是同一份
                if (cur != null && TryBuildPriceLine(cur, builder, color))
                {
                    return false;   // 已自行输出，丢弃原行
                }

                // 构造失败就保留原版文案，不再"只换词"糊一个自己没验证的版本
                Log.Warn("TooltipPipeline.PriceTerms", "价格行构造失败，保留原版文案");
                return true;
            }

            // 堆叠物品原版写「单价 / 总价」两行，不经过价格行改写；性价比补在价格块末尾，
            // 后面还有进价行就等进价行写完。
            if (!string.IsNullOrEmpty(_totalHead) && text.StartsWith(_totalHead!, StringComparison.Ordinal)
                && _current != null && ValueEnhance.Applies(_current))
            {
                if (HasBuyPrice(_current)) _perCellAfterBuy = true;
                else _afterValueEnhance = true;   // 原行照常写，性价比由后缀补在它后面
                return true;
            }

            // 「购买价格: N」——换词不换信息，正文留原版，只有原版窗口里显示「进价」；
            // 若合并价格行已把进价写进括号，这一行整行丢掉。
            if (!string.IsNullOrEmpty(_buyHead) && text.StartsWith(_buyHead!, StringComparison.Ordinal))
            {
                if (_priceEmbeddedBuy) return false;
                string shown = Loc.T("price.buy", "Buy: ") + text.Substring(_buyHead!.Length);
                if (_current != null)
                    TooltipSections.ObserveText(builder, _current, "purchase", text, color, TooltipGroupIds.Price);
                if (_perCellAfterBuy)
                {
                    _perCellAfterBuy = false;
                    _afterValueEnhance = true;
                }
                Swap(shown);
            }
            return true;
        }
        catch { return true; }
    }

    /// <summary>
    /// 卡路里行补「每信用点」：原版是 ` " " + mech_food_calories(value)`，
    /// 追加灰色 `(N千卡/信用点图标)`，货币图标复用游戏原生接口。
    /// 分母取物品当前价值（与价格行、性价比同一份快照）：食物占格大小参差，
    /// 「每格」区分不出 worth，按价折算才能横向比较该吃还是该卖。
    /// 只认与原版原文完全一致的行：官方钩子在 GetTooltipBasic 内触发，其它模组写的
    /// 短行（如「保质期 120」）也会经过这里，按数字模糊匹配会把它们误改成卡路里行。
    /// </summary>
    private static bool RewriteCalorieLine(ref string text, RichTextBuilder builder)
    {
        try
        {
            var item = _current;
            if (!ModSettings.TooltipFormatting || item == null || string.IsNullOrEmpty(text)) return true;
            if (!GameRead.IsTag(item, "CALORIE_VALUE_TAG")) return true;

            int cal = 0;
            try { cal = item.GetTagReadonly("CALORIE_VALUE_TAG").GetInt(); } catch { return true; }
            if (cal <= 0) return true;

            if (text.IndexOf(cal.ToString(), StringComparison.Ordinal) < 0) return true;
            string key = GameRead.IsTag(item, "MEAT_FOOD_TAG") ? "mech_food_calories_meat" : "mech_food_calories";
            string vanilla;
            try { vanilla = " " + LocHelper.GetLocalizedMechanic(key, cal); } catch { return true; }
            if (!string.Equals(text, vanilla, StringComparison.Ordinal)) return true;
            int idx = text.IndexOf(cal.ToString(), StringComparison.Ordinal);

            // 单位后缀 = 数字之后、行尾之前的部分（去掉首尾空白）
            string tail = text.Substring(idx + cal.ToString().Length).Trim();
            if (tail.Length > 6) return true;            // 不是纯卡路里行
            if (tail.Length == 0) tail = Loc.T("calorie.unit", "kcal");

            // 去掉后缀里的括号补充（如「（肉类）」），比率只用核心单位
            int cut = tail.IndexOfAny(new[] { '（', '(' });
            string core = cut > 0 ? tail.Substring(0, cut).Trim() : tail;
            if (core.Length == 0) core = tail;

            string head = Loc.T("calorie.line", "Energy: {0} {1}", cal, tail);
            var output = new TooltipSections.Writer(builder, item, "calories", TooltipGroupIds.Food);
            output.Field("calories", cal);
            output.Add(head, RenderHandler.ColorPalette.LightCyan);

            if (ModSettings.ValueEnhance)
            {
                // 单独兜底：价值读不到只丢这段灰色注释，不动已写好的能量行
                try
                {
                    long value = ItemValueCache.Get(item).Sell;
                    if (value > 0)
                    {
                        double per = (double)cal / value;
                        output.Field("caloriesPerCredit", per);
                        output.Add(Loc.T("calorie.per_credit", " ({0} {1}/{2})",
                                          per.ToString("0.#", Loc.Culture), core, GeneralHelper.CreditIcon()),
                                    RenderHandler.ColorPalette.Gray);
                    }
                }
                catch { }
            }

            output.NextLine();
            output.Emit();
            return false;   // 已自行输出，丢弃原行
        }
        catch { return true; }
    }
    /// <summary>当前是否正在构建物品 tooltip（供其它模块判断"这次 Add 属于 tooltip"）。</summary>
    internal static bool IsBuilding => _current != null;

    /// <summary>正在构建 tooltip 的物品（未在构建时为 null）。</summary>
    internal static GameItem? Current => _current;

    /// <summary>每次外层 tooltip 构建递增，供其它模块记"本次 tooltip 已写过某行"。</summary>
    internal static int BuildId => _buildId;

    /// <summary>
    /// 拼出价格行：`售价：69（进价55+14） 标价60`
    /// 括号及其内容灰色，差价正红负绿（正=卖得比买价高）。
    /// 数字直接读物品，不做字符串解析。
    ///
    /// 标价只在它与售价不同（<c>list != sell</c>）时写：没有价格修正的物品两个数本就相等，
    /// 再写一遍只是把同一个数字重复两次。差异来源由价格影响行（标签/状态）与市场行解释。
    ///
    /// 返回 true 表示合并行已写进 builder（经 <c>Emit</c>，可被认领），调用方丢弃原版那行；
    /// SectionedTooltips 的估价单按这一行的格式解析，改格式前先与对方核对。
    /// </summary>
    private static bool TryBuildPriceLine(GameItem item, RichTextBuilder builder, RenderHandler.ColorPalette color)
    {
        try
        {
            var value = ItemValueCache.Get(item);
            long sell = value.Sell;
            long list = value.List;
            if (sell <= 0 && list <= 0) return false;

            int buy = BuyPrice(item);

            // 分段上色（不拼内联 <color>）：内联标签在长行被折行时会被转义成纯文本。
            string sellPart = Loc.T("price.sell", "Sell: {0}", sell);
            string buyHead = Loc.T("price.cost_open", " (Cost ");
            string buyTail = Loc.T("price.cost_close", ")");
            int diff = (int)(sell - buy);
            string diffTxt = diff.ToString("+0;-0;0");
            var diffColor = diff >= 0
                ? RenderHandler.ColorPalette.LightGreen   // 正 → 绿（赚）
                : RenderHandler.ColorPalette.LightRed;    // 负 → 红（亏）
            string listPart = list != sell ? Loc.T("price.list", " List: {0}", list) : "";

            // 宽度预算：超过就拆两行，避免被 tooltip 截断
            int width = DisplayWidth(sellPart) + DisplayWidth(listPart);
            if (buy != 0)
            {
                width += DisplayWidth(buyHead) + buy.ToString().Length
                       + DisplayWidth(buyTail);
                if (diff != 0) width += diffTxt.Length;
            }

            // 中日韩沿用按显示宽度估的 30（全角算 2）；其余语言原版按字符数在空格处折行，
            // 直接用它的折行宽度，否则西文价格行会过早拆成两行
            float scale = UISettings.current != null ? UISettings.current.GetCharWidthScale() : 1f;
            int maxWidth = scale > 1f ? 30 : builder.maxCharWidth;
            bool splitLines = width > maxWidth;

            var output = new TooltipSections.Writer(builder, item, "price", TooltipGroupIds.Price);
            output.Field("sell", sell);
            output.Field("list", list);
            if (buy != 0) output.Field("buy", buy);

            output.Add(sellPart, color);
            if (buy != 0)
            {
                output.Add(buyHead, RenderHandler.ColorPalette.Gray);
                output.Add(buy.ToString(), RenderHandler.ColorPalette.Gray);
                if (diff != 0) output.Add(diffTxt, diffColor);
                output.Add(buyTail, RenderHandler.ColorPalette.Gray);
            }

            if (listPart.Length > 0)
            {
                if (splitLines)
                {
                    output.NextLine();
                    output.Add(listPart.TrimStart(), color);
                }
                else
                {
                    output.Add(listPart, color);
                }
            }

            output.NextLine();
            output.Emit();
            _priceEmbeddedBuy = buy != 0;

            // 性价比紧跟价格行，与它的来源放在一起
            ValueEnhance.TryWrite(builder, item);
            return true;   // 已自行输出，调用方丢弃原行
        }
        catch (Exception ex)
        {
            Log.Warn("TooltipPipeline.PriceLine", ex);
            return false;
        }
    }

    private static int BuyPrice(GameItem item)
    {
        try { return GameRead.TagInt(item, "BOUGHT_PRICE_TAG") ?? 0; }
        catch { return 0; }
    }

    /// <summary>与原版同一条件：BOUGHT_PRICE_TAG 非 0 才会写进价行。</summary>
    private static bool HasBuyPrice(GameItem item) => BuyPrice(item) != 0;

    /// <summary>粗略显示宽度：CJK/全角算 2，其余算 1（用于判断是否会超出 tooltip 宽度）。</summary>
    private static int DisplayWidth(string s)
    {
        int w = 0;
        foreach (char c in s)
        {
            if (c == '<') break;                 // 遇到富文本标签就不再计（后续都是标记）
            w += (c >= 0x2E80) ? 2 : 1;
        }
        // 含标签时按去掉标签后的文本重算
        if (s.IndexOf('<') >= 0)
        {
            string plain = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]*>", "");
            w = 0;
            foreach (char c in plain) w += (c >= 0x2E80) ? 2 : 1;
        }
        return w;
    }

    private static void EnsurePriceTerms()
    {
        string locale = Loc.Code;
        if (_priceLocale == locale && _estHead != null) return;

        try
        {
            const string p1 = "123456789";
            const string p2 = "987654321";

            string sample = LocHelper.GetLocalizedMechanic("mech_tooltip_est_value", p1, p2);
            int i1 = sample.IndexOf(p1, StringComparison.Ordinal);
            int i2 = sample.IndexOf(p2, StringComparison.Ordinal);
            _estHead = i1 > 0 ? sample.Substring(0, i1) : null;
            _estMid = (i2 > i1 && i1 > 0) ? sample.Substring(i1 + p1.Length, i2 - i1 - p1.Length) : null;

            string buy = LocHelper.GetLocalizedMechanic("mech_tooltip_bought_at", p1);
            int j = buy.IndexOf(p1, StringComparison.Ordinal);
            _buyHead = j > 0 ? buy.Substring(0, j) : null;

            string total = LocHelper.GetLocalizedMechanic("mech_tooltip_total_barter", p1);
            int k = total.IndexOf(p1, StringComparison.Ordinal);
            _totalHead = k > 0 ? total.Substring(0, k) : null;

            _priceLocale = locale;
        }
        catch (Exception ex)
        {
            Log.Warn("TooltipPipeline.PriceTerms", ex);
            _priceLocale = locale;
        }
    }
}
