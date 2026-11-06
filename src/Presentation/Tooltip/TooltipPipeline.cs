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
///   - RichTextBuilder.AddLine 的前缀只记"这一行显示成什么"，后缀把原版刚写出的片段挂成渲染注解
///     （标签短名、液量单位、价格术语、卡路里、模组批注、估价行隐藏）；builder 里始终是原版原文，
///     第三方在 GetTooltipBasic 返回时读到的是规范输出，不是本作的写法；
///   - RichTextBuilder.AddEmptyLine 跳过价格块前的占位行，避免价格行被缩进。
///
/// 例外：价格行的合并（售价 + 进价 + 标价拼成一行）与放大镜提示的替换仍在逻辑层直接写，
/// 因为前者要重排整行、后者描述的是本作改过的操作，原版文案本身已经不成立。
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
        _lineParts = null;
        _lineAppend = false;
        _lineHide = false;
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

    // ---------------- 原版行：显示写法 ----------------
    //
    // 原版写出的文本是"规范输出"：第三方在 GetTooltipBasic 返回时读到的就是它，本作不替换成自己的写法。
    // 玩家看到的版本由渲染层在 Build 前换入，所以这里只记"这一行显示成什么"，一句都不改 __0。

    private static RichTextBuilder? _lineBuilder;
    private static int _lineBefore = -1;
    private static int _lineDepth;   // 本作自己写行时可能重入；只有最外层那一行登记显示写法
    private static RenderSlices.Part[]? _lineParts;
    private static bool _lineAppend;
    private static bool _lineHide;
    private static bool _lineWrap;
    private static bool _afterValueEnhance;

    private static void BeginLine(RichTextBuilder builder)
    {
        _lineBuilder = builder;
        _lineBefore = RenderSlices.Count(builder);
        _lineParts = null;
        _lineAppend = false;
        _lineHide = false;
        _lineWrap = false;
        _afterValueEnhance = false;
    }

    /// <summary>整行改成这几段（原文不进显示）。</summary>
    private static void Show(params RenderSlices.Part[] parts) => _lineParts = parts;

    /// <summary>原文照显示，这几段接在行尾。</summary>
    private static void Append(RenderSlices.Part[] parts)
    {
        _lineParts = parts;
        _lineAppend = true;
    }

    private static RenderSlices.Part Part(string text, RenderHandler.ColorPalette color)
        => new(text, new RenderSlices.Style(color));

    /// <summary>返回 false 表示丢弃这一行（整行由我方另行写出）。</summary>
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

            // 放大镜提示换成双击提示：这条提示描述的是本 Mod 改过的操作，原版文案对任何读取方都不成立，
            // 属于行为提示而非信息改写，留在逻辑层直接换。
            if (!DoubleClickInspect.TryRewriteHint(ref __0, ref __2, ref __4, __instance)) return false;

            // 第三方行说明：规则表目前为空；将来加规则时改为 Append，不要在这里改 __0。
            LineAnnotations.TryAppend(ref __0, cur);

            // 标签行：原版原文留在 builder 里，原版窗口构建时换短名并补价格影响
            string? label = LabelImpact.Display(__0, __instance, cur);
            if (label != null) Show(Part(label, __2));

            // 估价行：第三方仍能读到原版那行，原版窗口里由我方「价值」区块取代
            if (_suppressEstimate)
            {
                string prefix = EstimatePrefix();
                if (prefix.Length > 0 && __0.StartsWith(prefix, StringComparison.Ordinal))
                {
                    _lineHide = true;
                    return true;
                }
            }

            var originalColor = __2;
            if (ModSettings.DimDescription
                && !string.IsNullOrEmpty(cur.shortDescription)
                && string.Equals(__0, cur.shortDescription, StringComparison.Ordinal))
            {
                __2 = RenderHandler.ColorPalette.Gray;
            }

            // 描述与背景故事写原文；原版窗口构建时若折行预算被别人的条放宽，渲染层再按常规宽度折行
            if (NoteDescription(__0, cur)) _lineWrap = true;
            if (__2 != originalColor)
                TooltipSections.ObserveText(__instance, cur, "description", __0, __2, TooltipGroupIds.Description);

            // 价格术语本地化：预估价值→售价、基础价值→标价、购买价格→进价
            if (!PriceTerms(__0, __instance, __2)) return false;
            CalorieLine(__0, __instance, cur);
            LiquidVolume(__0, __instance, __2, cur);

            // 模组加成行：-性能: +16% → -性能: +16%(+136毫升/天)，原文保留、批注接在行尾
            string? effect = ModuleEffectAnnotation.Annotation(__0, __instance, __2, cur);
            if (effect != null) Append(new[] { Part(effect, RenderHandler.ColorPalette.Gray) });

            // 模块物品的空「模组效果：」表头（三项加成全 0）：原文留给第三方，原版窗口不显示
            if (ModuleEffectAnnotation.ShouldDrop(__0, cur)) _lineHide = true;

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
    /// </summary>
    /// <summary>显示写法；null 表示这一行按原版显示。</summary>
    private static string? LiquidVolumeText(string text, GameItem item)
    {
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
                bool liters = Volume.UseLiters(max);
                return " " + WaterLayout.Capacity(cur, max, liters, Loc.Culture)
                     + (r.Tier.Length > 0 ? " " + r.Tier : "");
            }
        }

        if (!ModSettings.ContainerUnits || !Volume.UseLiters(cur)) return null;
        return " " + Volume.Number(cur, true, Loc.Culture) + " L / " + Volume.Number(max, true, Loc.Culture) + " L";
    }

    private static void LiquidVolume(string text, RichTextBuilder builder,
        RenderHandler.ColorPalette color, GameItem item)
    {
        string? display = LiquidVolumeText(text, item);
        if (display == null) return;
        Show(Part(display, color));
        TooltipSections.ObserveText(builder, item, "liquid-volume", display, color, TooltipGroupIds.Liquid);
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
            return string.Equals(text, item.shortDescription, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(item.flavorText)
                    && string.Equals(text, " " + item.flavorText, StringComparison.Ordinal));
        }
        catch { return false; }
    }

    /// <summary>原版行写完：把前缀记下的显示写法挂成渲染注解。builder 本身始终是规范输出。</summary>
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
        var parts = _lineParts;
        int before = _lineBefore;
        bool hide = _lineHide, append = _lineAppend, wrap = _lineWrap, value = _afterValueEnhance;
        _lineParts = null;
        _lineAppend = false;
        _lineHide = false;
        _lineWrap = false;
        _afterValueEnhance = false;

        try
        {
            if (ReferenceEquals(builder, __instance) && __0 != null && before >= 0)
            {
                if (hide) RenderSlices.Hide(builder, before);
                else if (parts != null && parts.Length > 0)
                {
                    if (append) RenderSlices.Append(builder, before, __0, parts);
                    else RenderSlices.Replace(builder, before, __0, parts);
                }
                else if (wrap)
                {
                    RenderSlices.Annotate(builder, before, __0,
                        new RenderSlices.Style(__2, __3, __4, __5, __6, __7, __8, __9), fixedText: null);
                }
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
    private static bool PriceTerms(string text, RichTextBuilder builder, RenderHandler.ColorPalette color)
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

                // 构造成功就由渲染层把这一行换成售价/进价/标价；原版行留在 builder 里给第三方
                if (cur != null && TryBuildPriceLine(cur, builder, color)) return true;

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

            // 「购买价格: N」——若价格行已把进价写进括号，原版窗口里就不重复显示
            if (!string.IsNullOrEmpty(_buyHead) && text.StartsWith(_buyHead!, StringComparison.Ordinal))
            {
                if (_priceEmbeddedBuy)
                {
                    _lineHide = true;
                    return true;
                }
                string shown = Loc.T("price.buy", "Buy: ") + text.Substring(_buyHead!.Length);
                if (_current != null)
                    TooltipSections.ObserveText(builder, _current, "purchase", shown, color, TooltipGroupIds.Price);
                if (_perCellAfterBuy)
                {
                    _perCellAfterBuy = false;
                    _afterValueEnhance = true;
                }
                Show(Part(shown, color));
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
    private static void CalorieLine(string text, RichTextBuilder builder, GameItem item)
    {
        try
        {
            if (!ModSettings.TooltipFormatting || string.IsNullOrEmpty(text)) return;
            if (!GameRead.IsTag(item, "CALORIE_VALUE_TAG")) return;

            int cal = 0;
            try { cal = item.GetTagReadonly("CALORIE_VALUE_TAG").GetInt(); } catch { return; }
            if (cal <= 0) return;

            if (text.IndexOf(cal.ToString(), StringComparison.Ordinal) < 0) return;
            string key = GameRead.IsTag(item, "MEAT_FOOD_TAG") ? "mech_food_calories_meat" : "mech_food_calories";
            string vanilla;
            try { vanilla = " " + LocHelper.GetLocalizedMechanic(key, cal); } catch { return; }
            if (!string.Equals(text, vanilla, StringComparison.Ordinal)) return;
            int idx = text.IndexOf(cal.ToString(), StringComparison.Ordinal);

            // 单位后缀 = 数字之后、行尾之前的部分（去掉首尾空白）
            string tail = text.Substring(idx + cal.ToString().Length).Trim();
            if (tail.Length > 6) return;            // 不是纯卡路里行
            if (tail.Length == 0) tail = Loc.T("calorie.unit", "kcal");

            // 去掉后缀里的括号补充（如「（肉类）」），比率只用核心单位
            int cut = tail.IndexOfAny(new[] { '（', '(' });
            string core = cut > 0 ? tail.Substring(0, cut).Trim() : tail;
            if (core.Length == 0) core = tail;

            string head = Loc.T("calorie.line", "Energy: {0} {1}", cal, tail);
            var output = new TooltipSections.Writer(builder, item, "calories", TooltipGroupIds.Food);
            output.Field("calories", cal);
            output.Add(head, RenderHandler.ColorPalette.LightCyan);

            string? extra = null;
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
                        extra = Loc.T("calorie.per_credit", " ({0} {1}/{2})",
                                      per.ToString("0.#", Loc.Culture), core, GeneralHelper.CreditIcon());
                        output.Add(extra, RenderHandler.ColorPalette.Gray);
                    }
                }
                catch { }
            }

            // 行本身是原版写的：只对外提供数据，不再替它写出，显示走渲染注解。
            output.Observe();

            var cyan = Part(head, RenderHandler.ColorPalette.LightCyan);
            if (extra != null) Show(cyan, Part(extra, RenderHandler.ColorPalette.Gray));
            else Show(cyan);
        }
        catch { }
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
    /// 返回 true 表示已登记显示写法：原版那行留在 builder 里（第三方读到的是原版价格文案），
    /// 原版窗口构建时换成这里拼的行。
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

            // 显示写法：售价（进价 差价）+ 标价；超宽时标价另起一行。原版那行留在 builder 里不动。
            var parts = new List<RenderSlices.Part>(5) { Part(sellPart, color) };
            output.Add(sellPart, color);
            if (buy != 0)
            {
                parts.Add(Part(buyHead, RenderHandler.ColorPalette.Gray));
                parts.Add(Part(buy.ToString(), RenderHandler.ColorPalette.Gray));
                if (diff != 0) parts.Add(Part(diffTxt, diffColor));
                parts.Add(Part(buyTail, RenderHandler.ColorPalette.Gray));
                output.Add(buyHead, RenderHandler.ColorPalette.Gray);
                output.Add(buy.ToString(), RenderHandler.ColorPalette.Gray);
                if (diff != 0) output.Add(diffTxt, diffColor);
                output.Add(buyTail, RenderHandler.ColorPalette.Gray);
            }

            if (listPart.Length > 0)
            {
                if (splitLines)
                {
                    parts.Add(Part("\n" + listPart.TrimStart(), color));
                    output.NextLine();
                    output.Add(listPart.TrimStart(), color);
                }
                else
                {
                    parts.Add(Part(listPart, color));
                    output.Add(listPart, color);
                }
            }

            // 行本身是原版的：只对外提供数据，显示走渲染注解
            output.Observe();
            Show(parts.ToArray());
            _priceEmbeddedBuy = buy != 0;

            // 性价比紧跟价格行，与它的来源放在一起
            _afterValueEnhance = true;
            return true;
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
