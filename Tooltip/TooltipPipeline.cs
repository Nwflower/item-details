using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 统一的 tooltip 构建上下文与行改写。
///
/// 原版整段 tooltip 在 GameItemElement.GetTooltipBasic() 内按顺序拼装：
///   - Prefix/Finalizer 记录当前物品并做深度计数；
///   - RichTextBuilder.AddLine 负责估值行隐藏、描述淡化与价格/卡路里行改写；
///   - RichTextBuilder.AddEmptyLine 跳过价格块前的占位行，避免价格行被缩进。
///
/// 所有状态集中在这里，Finalizer 在正常和异常路径都会清理。这样避免
/// TooltipContext / EstimateLineHider / DescriptionDim / PriceTerms
/// 多套上下文和多组 Harmony 补丁互相依赖。
/// </summary>
internal static class TooltipPipeline
{
    private const string HarmonyId = "item_details.tooltip_pipeline";


    private static Harmony? _harmony;
    private static bool _patched;

    private static int _depth;
    private static GameItem? _current;
    private static bool _suppressEstimate;

    private static string? _estimatePrefix;
    private static string? _estimateLocale;

    // 价格术语本地化：用哨兵数字反查模板，避免硬编码原版文案
    private static string _priceLocale = "";
    private static string? _estHead;    // 「预估价值」前缀（含标点）
    private static string? _estMid;     // 两个数字之间的「基础价值」段（含标点）
    private static string? _buyHead;    // 「购买价格」前缀（含标点）
    private static bool _priceEmbeddedBuy;   // 本次价格块是否已把进价写进括号

    internal static void Apply()
    {
        if (_patched) return;
        try
        {
            var basic = AccessTools.Method(typeof(GameItemElement), "GetTooltipBasic", Type.EmptyTypes);
            var addLine = AccessTools.Method(typeof(RichTextBuilder), "AddLine");
            if (basic == null || addLine == null)
            {
                Log.Warn("TooltipPipeline.Apply", "未找到 GetTooltipBasic/AddLine，tooltip 改写跳过");
                return;
            }

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(basic,
                prefix: new HarmonyMethod(typeof(TooltipPipeline), nameof(BasicPrefix)),
                finalizer: new HarmonyMethod(typeof(TooltipPipeline), nameof(BasicFinalizer)));
            _harmony.Patch(addLine, prefix: new HarmonyMethod(typeof(TooltipPipeline), nameof(AddLinePrefix)));
            // 记录 AddEmptyLine：它内部是 `NextLine(); Add("<size=Npx> </size>")`，
            // 并不结束占位行，于是紧随其后的价格行会与之同行 → 表现为固定缩进。
            var emptyLine = AccessTools.Method(typeof(RichTextBuilder), "AddEmptyLine");
            if (emptyLine != null)
                _harmony.Patch(emptyLine, prefix: new HarmonyMethod(typeof(TooltipPipeline), nameof(AddEmptyLinePrefix)));

            _patched = true;
            Log.Msg("TooltipPipeline: patched GetTooltipBasic + AddLine + AddEmptyLine");
        }
        catch (Exception ex)
        {
            Log.Error("TooltipPipeline.Apply", ex);
        }
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _patched = false;
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

        // 分类必须在任何 Add 之前记录：原版 CreateItemTypeTooltip 在构建早期就把
        // " [类型]" 加进正文，记晚了既抑制不到，也会让浮窗用到上一件物品的残留。
        try { TypeBadge.NoteItem(__instance); }
        catch (Exception ex) { Log.Warn("TooltipPipeline.NoteItem", ex); }

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
        _current = null;
        _suppressEstimate = false;
        // 价格块状态必须逐次重置：否则上一次 tooltip 把进价写进括号后，
        // 下一个物品的独立「购买价格」行会被误删（跨物品、跨启停都会残留）。
        _priceEmbeddedBuy = false;
    }

    private static bool ShouldHideEstimate(GameItemElement item)
    {
        try
        {
            if (!ModSettings.HideEstimate) return false;
            if (!GameRead.IsTag(item, "APPRAISAL_ITEM")) return false;
            // 只有我们确实能给出总价时才隐藏，避免"两边都没有"。
            return ValueBreakdown.Analyze(item) != null;
        }
        catch { return false; }
    }

    // ---------------- 行改写 ----------------

    /// <summary>返回 false 表示丢弃这一行。</summary>
    private static bool AddLinePrefix(ref string __0, ref RenderHandler.ColorPalette __2, RichTextBuilder __instance)
    {
        try
        {
            var cur = _current;
            if (cur == null || __0 == null) return true;

            if (_suppressEstimate)
            {
                string prefix = EstimatePrefix();
                if (prefix.Length > 0 && __0.StartsWith(prefix, StringComparison.Ordinal))
                    return false;
            }

            if (ModSettings.DimDescription
                && !string.IsNullOrEmpty(cur.shortDescription)
                && string.Equals(__0, cur.shortDescription, StringComparison.Ordinal))
            {
                __2 = RenderHandler.ColorPalette.Gray;
            }

            // 价格术语本地化：预估价值→售价、基础价值→标价、购买价格→进价
            if (!RewritePriceTerms(ref __0, __instance, __2)) return false;
            if (!RewriteCalorieLine(ref __0, __instance)) return false;

            // 模组加成行：-性能: +16% → -性能: +16%(+136毫升/天)
            if (ModuleEffectAnnotation.TryApply(ref __0, __instance, __2, cur)) return false;

            // 模块物品的空「模组效果：」表头（三项加成全 0）直接丢掉
            if (ModuleEffectAnnotation.ShouldDrop(__0, cur)) return false;

            return true;
        }
        catch { return true; }
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
            if (heightPixels == PriceBlockSpacerHeight
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
            EnsurePriceTerms();
            if (text == null) return true;

            // 「预估价值: A, 基础价值: B」→ 售价 +（进价 差价）+ 标价
            if (!string.IsNullOrEmpty(_estHead) && !string.IsNullOrEmpty(_estMid)
                && text.StartsWith(_estHead!, StringComparison.Ordinal))
            {
                var cur = _current;

                if (cur != null && TryBuildPriceLine(cur, builder, color))
                {
                    return false;   // 已自行输出，丢弃原行
                }

                // 构造失败就保留原版文案，不再"只换词"糊一个自己没验证的版本
                Log.Warn("TooltipPipeline.PriceTerms", "价格行构造失败，保留原版文案");
                return true;
            }

            // 「购买价格: N」——若价格行已把进价写进括号，则整行丢弃，避免重复
            if (!string.IsNullOrEmpty(_buyHead) && text.StartsWith(_buyHead!, StringComparison.Ordinal))
            {
                if (_priceEmbeddedBuy) return false;
                text = Loc.T("price.buy", "Buy: ") + text.Substring(_buyHead!.Length);
            }
            return true;
        }
        catch { return true; }
    }

    /// <summary>
    /// 卡路里行补「每格」：原版是 ` " " + mech_food_calories(value)`，
    /// 追加灰色 `(N千卡/格)`，单位直接沿用该行已有的后缀（自动适配语言）。
    /// </summary>
    private static bool RewriteCalorieLine(ref string text, RichTextBuilder builder)
    {
        try
        {
            var item = _current;
            if (item == null || string.IsNullOrEmpty(text)) return true;
            if (!item.IsTag("CALORIE_VALUE_TAG")) return true;

            int cal = 0;
            try { cal = item.GetTagReadonly("CALORIE_VALUE_TAG").GetInt(); } catch { return true; }
            if (cal <= 0) return true;

            // 只在"含该数字的短行"上动手，避免误伤描述文本
            if (text.Length > 24) return true;
            int idx = text.IndexOf(cal.ToString(), StringComparison.Ordinal);
            if (idx < 0) return true;

            // 单位后缀 = 数字之后、行尾之前的部分（去掉首尾空白）
            string tail = text.Substring(idx + cal.ToString().Length).Trim();
            if (tail.Length > 6) return true;            // 不是纯卡路里行
            if (tail.Length == 0) tail = Loc.T("calorie.unit", "kcal");

            // 去掉后缀里的括号补充（如「（肉类）」），每格只用核心单位
            int cut = tail.IndexOfAny(new[] { '（', '(' });
            string core = cut > 0 ? tail.Substring(0, cut).Trim() : tail;
            if (core.Length == 0) core = tail;

            string head = Loc.T("calorie.line", "Energy: {0} {1}", cal, tail);
            builder.Add(head, RenderHandler.ColorPalette.LightCyan);

            if (ModSettings.ValueEnhance)
            {
                int cells = 0;
                try { cells = GameRead.OccupiedCells(item.modifiedShape); } catch { }
                if (cells <= 0) { try { cells = GameRead.OccupiedCells(item.shape); } catch { } }
                if (cells > 0)
                {
                    double per = (double)cal / cells;
                    builder.Add(Loc.T("calorie.per_cell", " ({0} {1}/cell)",
                                      per.ToString("0.#"), core),
                                RenderHandler.ColorPalette.Gray);
                }
            }

            builder.NextLine();
            return false;   // 已自行输出，丢弃原行
        }
        catch { return true; }
    }
    /// <summary>当前是否正在构建物品 tooltip（供其它模块判断"这次 Add 属于 tooltip"）。</summary>
    internal static bool IsBuilding => _current != null;

    /// <summary>
    /// 拼出价格行：`售价：69（进价55+14） 标价60`
    /// 括号及其内容灰色，差价正红负绿（正=卖得比买价高）。
    /// 数字直接读物品，不做字符串解析。
    /// </summary>
    private static bool TryBuildPriceLine(GameItem item, RichTextBuilder builder, RenderHandler.ColorPalette color)
    {
        try
        {
            var value = ItemValueCache.Get(item);
            long sell = value.Sell;
            long list = value.List;
            if (sell <= 0 && list <= 0) return false;

            int buy = 0;
            try { buy = GameRead.TagInt(item, "BOUGHT_PRICE_TAG") ?? 0; }
            catch { buy = 0; }

            // 分段上色（不拼内联 <color>）：内联标签在长行被折行时会被转义成纯文本。
            string sellPart = Loc.T("price.sell", "Sell: {0}", sell);
            string buyHead = Loc.T("price.cost_open", " (Cost ");
            string buyTail = Loc.T("price.cost_close", ")");
            int diff = (int)(sell - buy);
            string diffTxt = diff.ToString("+0;-0;0");
            var diffColor = diff >= 0
                ? RenderHandler.ColorPalette.LightGreen   // 正 → 绿（赚）
                : RenderHandler.ColorPalette.LightRed;    // 负 → 红（亏）
            string listPart = Loc.T("price.list", " List: {0}", list);

            // 宽度预算：超过就拆两行，避免被 tooltip 截断
            int width = DisplayWidth(sellPart) + DisplayWidth(listPart);
            if (buy != 0)
            {
                width += DisplayWidth(buyHead) + buy.ToString().Length
                       + DisplayWidth(buyTail);
                if (diff != 0) width += diffTxt.Length;
            }

            const int MaxWidth = 30;
            bool splitLines = width > MaxWidth;

            builder.Add(sellPart, color);
            if (buy != 0)
            {
                builder.Add(buyHead, RenderHandler.ColorPalette.Gray);
                builder.Add(buy.ToString(), RenderHandler.ColorPalette.Gray);
                if (diff != 0) builder.Add(diffTxt, diffColor);
                builder.Add(buyTail, RenderHandler.ColorPalette.Gray);
            }

            if (splitLines)
            {
                builder.NextLine();
                builder.Add(listPart.TrimStart(), color);
            }
            else
            {
                builder.Add(listPart, color);
            }

            builder.NextLine();
            _priceEmbeddedBuy = buy != 0;
            return true;   // 已自行输出，调用方丢弃原行
        }
        catch (Exception ex)
        {
            Log.Warn("TooltipPipeline.PriceLine", ex);
            return false;
        }
    }

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

            _priceLocale = locale;
        }
        catch (Exception ex)
        {
            Log.Warn("TooltipPipeline.PriceTerms", ex);
            _priceLocale = locale;
        }
    }
}