using System;
using System.Collections.Generic;
using System.Text;

namespace ItemDetails;

/// <summary>
/// 各 tooltip 追加器。顺序由 <c>ModEntry.Renderers</c> 决定，本文件只负责"某一类物品多写几行"。
///
/// 约定：模块自己判断开关与适用性，不适用就返回 false 且不写任何内容；
/// 真正的异常交给 <c>ModEntry.RunRenderers</c> 的边界统一处理，方法内不吞错。
/// </summary>
public sealed partial class ModEntry
{
    // ---------------- 真伪 ----------------
    private bool TryAppendAuthenticity(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Auth) return false;
        var r = Authenticity.Analyze(item);
        if (!r.HasData) return false;

        if (r.Faults > 0)
        {
            string names = string.Join(_zh ? "、" : ", ", _zh ? r.FaultNamesZh : r.FaultNamesEn);
            AddLine(builder, L($"✘ 鉴定：疑似赝品（{names}）", $"✘ Suspected counterfeit ({names})"),
                RenderHandler.ColorPalette.LightRed);
        }
        else if (r.IsGenuine)
        {
            AddLine(builder, L("✔ 鉴定：正品", "✔ Genuine"), RenderHandler.ColorPalette.Green);
        }
        else
        {
            // 有检查项读取失败且没有发现瑕疵 → 数据不完整，不下"正品"结论
            Log.Warn("Auth.Incomplete", $"只读到 {r.ReadableFlags}/{r.ExpectedFlags} 项，跳过真伪结论");
            return false;
        }
        return true;
    }

    // ---------------- 价值构成（机械手/汉堡/等离子） ----------------
    private bool TryAppendValue(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Value) return false;
        if (!GameRead.IsTag(item, "APPRAISAL_ITEM")) return false;

        var r = ValueBreakdown.Analyze(item);
        if (r == null) return false;

        // 总价与完美区间合并成一行，省一行留白。Value 已把完美区间并入同一开关。
        // Analyze 已校验 r.Total == 游戏真实值，不再重复调用 TrueValue。
        string totalLine = L($"总价：{r.Total}", $"Total: {r.Total}");
        var (lo, hi) = AppraisalRanges.Perfect(r.Total);
        totalLine += L($" ｜ 完美 {lo}–{hi}", $" | perfect {lo}-{hi}");
        AddLine(builder, totalLine, RenderHandler.ColorPalette.Green);

        AppendParts(builder, r);
        return true;
    }

    /// <summary>
    /// 来源明细单行输出；标签已缩短，避免原版定宽折行把数字拆断。
    ///
    /// 符号与分隔符合一：正值写「+加固60」，负值写「-破裂150」并用红字。
    /// 旧写法把符号塞进数值（「+破裂-150」），一个负项会出现两个符号，既难读也占宽。
    /// </summary>
    private void AppendParts(RichTextBuilder builder, ValueBreakdown.Result r)
    {
        builder.Add(L("来源：", "From: "), RenderHandler.ColorPalette.White);

        foreach (var p in r.Parts)
        {
            string label = _zh ? ShortenLabel(p.Zh) : p.En;

            if (p.IsBase)
            {
                builder.Add($"{label}{p.Amount}", RenderHandler.ColorPalette.White);
                continue;
            }

            if (p.Amount < 0)
                builder.Add($"-{label}{-p.Amount}", RenderHandler.ColorPalette.LightRed);
            else
                builder.Add($"+{label}{p.Amount}", RenderHandler.ColorPalette.White);
        }

        builder.NextLine();
    }

    /// <summary>
    /// 来源明细的短标签：只为省横向空间，数字不变。
    /// 「基础价格300+轻度磨损-45」→「基础300+轻磨-45」，一行能多放两项。
    /// </summary>
    private static string ShortenLabel(string zh) => zh switch
    {
        "基础价格" => "基础",
        "轻度磨损" => "轻磨",
        "重度磨损" => "重磨",
        "外壳破裂" => "破裂",
        "螺丝外露" => "外露",
        "加固处理" => "加固",
        "高级颜色" => "高色",
        "低级颜色" => "低色",
        "规格不明接口" => "不明接口",
        "装甲组件" => "装甲",
        "刀刃组件" => "刀刃",
        "战术型组合" => "战术组合",
        "豪华型组合" => "豪华组合",
        "王尔德载具加成" => "王尔德",
        "中华船舶加成" => "中华船舶",
        "共和国海军加成" => "共和国海军",
        "点火实验室加成" => "点火实验室",
        "双层肉饼" => "双肉饼",
        "首月/末月" => "首末月",
        "第一批次" => "首批次",
        "已开封或赝品（固定价）" => "已开封/赝品",
        _ => zh,
    };

    // ---------------- 赝品肋明治：具体原因 ----------------
    private bool TryAppendRibwichReasons(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Value) return false;

        var lines = RibwichCounterfeit.Collect(item, _zh);
        if (lines.Count == 0) return false;

        AddLine(builder, L("赝品原因：", "Why counterfeit:"), RenderHandler.ColorPalette.LightRed);
        foreach (string s in lines)
            AddLine(builder, "• " + s, RenderHandler.ColorPalette.LightRed);
        return true;
    }

    // ---------------- 泛型分解（未知/模组新增的鉴定类型） ----------------
    private bool TryAppendGeneric(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Value) return false;

        var lines = GenericBreakdown.Collect(item, _zh);
        if (lines.Count == 0) return false;

        AddLine(builder, L("价值分解（实测）：", "Value breakdown (measured):"),
            RenderHandler.ColorPalette.Yellow);
        return AppendLines(builder, lines, RenderHandler.ColorPalette.White);
    }

    // ---------------- 荧光莓 ----------------
    private bool TryAppendBerry(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Plant) return false;

        var q = GameRead.ClassifyBerry(item);
        if (q == BerryQuality.NotBerry) return false;

        string? name = BerryQualityClassifier.DisplayName(q, _zh);
        if (name == null) return false;

        AddLine(builder, L($"荧光莓品质：{name}", $"Berry quality: {name}"),
            q == BerryQuality.Normal ? RenderHandler.ColorPalette.Green : RenderHandler.ColorPalette.Orange);
        return true;
    }

    // ---------------- P2 隐藏信息（真实耐久/纯度） ----------------
    private bool TryAppendHidden(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.HiddenTruth) return false;
        if (IsLiquid(item)) return false;   // 水质模块已汇总
        return AppendLines(builder, HiddenInfo.Collect(item, _zh));
    }

    // ---------------- P3 价值增强（总价拆解/性价比） ----------------
    private bool TryAppendValueEnhance(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.ValueEnhance) return false;
        // 鉴定物品已有「总价/完美区间/来源」，性价比是按顾客报价算的，混在一起容易误读
        if (GameRead.IsTag(item, "APPRAISAL_ITEM")) return false;
        if (IsLiquid(item)) return false;   // 含水容器的性价比意义不大，且挤占版面
        return AppendLines(builder, ValueEnhance.Collect(item, _zh));
    }

    // ---------------- P1 隐藏特性揭示（真假条件） ----------------
    private bool TryAppendFeatureReveal(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.HiddenTruth) return false;
        if (IsLiquid(item)) return false;   // 水质模块已汇总真假
        return AppendLines(builder, FeatureReveal.Collect(item, _zh));
    }

    // ---------------- 储存容器：压缩率 + 内容物 ----------------
    private bool TryAppendStorage(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Container) return false;
        return AppendLines(builder, StorageInfo.Collect(item, _zh));
    }

    // ---------------- 瑕疵/状态对价值的影响 ----------------
    private bool TryAppendConditions(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.PriceImpact) return false;
        if (IsLiquid(item)) return false;   // 水质模块已汇总，避免重复
        return AppendLines(builder, ConditionEffects.Collect(item, _zh));
    }

    // ---------------- 违禁等级与黑市溢价 ----------------
    private bool TryAppendContraband(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Contraband) return false;

        var r = ContrabandInfo.Analyze(item);
        if (!r.HasData) return false;
        // 原版 ContrabandHelper 已经显示违禁等级，这里只补它没有的黑市溢价。
        if (r.MarkupPercent < 0) return false;

        AddLine(builder,
            L($"黑市溢价：+{r.MarkupPercent}%", $"Black market: +{r.MarkupPercent}%"),
            RenderHandler.ColorPalette.LightTurquoise);
        return true;
    }

    // ---------------- 彩票开奖结果 ----------------
    private bool TryAppendLottery(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Lottery) return false;
        return AppendLines(builder, LotteryInfo.Collect(item, _zh));
    }

    // ---------------- 水质与杂质 ----------------
    private bool TryAppendWater(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.WaterPurity) return false;
        // AquaSense 是专职水质模组：它已经输出「水质详情 + 液体成分 + 容量条」，
        // 这里再列一遍就是重复（见 ModCompat）。净化收益建议由 water-advice 单独输出。
        if (ModCompat.AquaSenseActive) return false;

        var r = WaterInfo.Analyze(item);
        if (!r.HasData) return false;

        double purityPct = r.Purity / 100.0;
        int impurity = WaterInfo.ImpurityPermille(r);
        double impurityPct = impurity / 100.0;

        // 第一行：实测纯度 + 游戏档位名。
        // 标签行由 LabelImpact 原样输出，此处不重复标签信息。
        string head = _zh
            ? $"水质：{purityPct:0.#}% {r.Tier}"
            : $"Water: {purityPct:0.#}% {r.Tier}";

        var headColor = r.Purity >= WaterInfo.PurityGood ? RenderHandler.ColorPalette.LightGreen
                      : r.Purity >= WaterInfo.PurityFair ? RenderHandler.ColorPalette.LightYellow
                      : RenderHandler.ColorPalette.LightRed;

        // 标签写着的水质与实测档位不一致时，用灰字标注
        bool mismatch = !string.IsNullOrEmpty(r.Label)
                     && !string.IsNullOrEmpty(r.Tier)
                     && !string.Equals(r.Label, r.Tier, StringComparison.Ordinal);

        builder.Add(head.Trim(), headColor);
        if (mismatch)
            builder.Add(_zh ? "（不匹配）" : " (label mismatch)", RenderHandler.ColorPalette.Gray);
        builder.NextLine();

        // 第二行：杂质构成
        var sb = new StringBuilder();
        sb.Append(_zh ? $"杂质 {impurityPct:0.#}%" : $"Impurities {impurityPct:0.#}%");
        if (r.Impurities.Count == 0)
        {
            sb.Append(_zh ? "：无" : ": none");
        }
        else
        {
            // 只列非零杂质：少于 MinVisibleParts 的按 0.# 显示会变成 "0ml"，直接跳过
            sb.Append(_zh ? "：" : ": ");
            bool any = false;
            for (int i = 0; i < r.Impurities.Count; i++)
            {
                if (r.Impurities[i].Parts < WaterInfo.MinVisibleParts) continue;
                if (any) sb.Append(_zh ? "、" : ", ");
                any = true;
                sb.Append(r.Impurities[i].Name).Append(' ').Append(WaterInfo.Ml(r.Impurities[i].Parts)).Append("ml");
            }
            if (!any) sb.Append(_zh ? "微量（低于显示阈值）" : "trace (below display threshold)");
        }

        AddLine(builder, sb.ToString(),
            impurity == 0 ? RenderHandler.ColorPalette.LightGreen : RenderHandler.ColorPalette.LightRed);
        return true;
    }

    // ---------------- 密封箱预览（透视，默认关闭） ----------------
    private bool TryAppendSealedBox(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.SealedBox) return false;
        return AppendLines(builder, SealedBoxInfo.Collect(item, _zh));
    }

    // ---------------- 刮刮卡结果（透视） ----------------
    private bool TryAppendScratcher(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Scratcher) return false;
        return AppendLines(builder, ScratcherInfo.Collect(item, _zh));
    }

    // ---------------- 种子性状（透视） ----------------
    private bool TryAppendSeed(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Plant) return false;

        var lines = SeedInfo.Collect(item, _zh);
        if (lines.Count == 0) return false;

        AddLine(builder, L("性状（未分析）：", "Traits (unanalysed):"),
            RenderHandler.ColorPalette.LightTurquoise);
        return AppendLines(builder, lines);
    }

    // ---------------- 含水容器：净化收益（探测版） ----------------
    private bool TryAppendWaterAdvice(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.WaterPurity) return false;

        // 按玩家实际拥有的手段逐项列出纯度提升（每种都在独立深拷贝探针上模拟）
        var methods = WaterAdvice.SimulateMethods(item);
        if (methods.Count == 0) return false;

        bool wrote = false;
        foreach (var mtd in methods)
        {
            if (wrote) builder.Add("　", RenderHandler.ColorPalette.Gray);
            builder.Add($"{mtd.Name} +{mtd.GainPct:0.00}%", RenderHandler.ColorPalette.LightTurquoise);
            wrote = true;
        }
        builder.NextLine();

        return true;
    }

    // ---------------- 酒：分数 / 成分 / 赝品（透视） ----------------
    private bool TryAppendWine(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Wine) return false;
        var lines = WineInfo.Collect(item, _zh);
        if (lines.Count == 0) return false;
        AppendLines(builder, lines);
        return true;
    }

    // ---------------- 市场修正（店铺事件对价格的百分比影响） ----------------
    private bool TryAppendMarket(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.PriceImpact) return false;
        return AppendLines(builder, MarketModifier.Collect(item, _zh), RenderHandler.ColorPalette.Orange);
    }

    // ---------------- 模组效果的实际变更（基准 → 当前） ----------------
    /// <summary>
    /// 机器装了模块时，原版只写「性能/效率/质量 +16%」。这里补一行实际值：
    /// 处理速度 BASE_PROCESSING_SPEED_TAG → CURRENT_PROCESSING_SPEED_TAG，
    /// 耗电 MACHINERY_DRAWN_POWER_BASE → machinery_drawn_power。
    /// 两个数都是 `ModuleHelper.ApplyBasicModuleEffect` 算完写在物品标签上的，
    /// 本模组只做搬运，不自己复算公式（宁可少一行，也不写错一个数）。
    /// </summary>
    private bool TryAppendModuleEffects(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.ModuleEffects) return false;

        bool receiving;
        try { receiving = GameRead.IsTag(item, "IS_RECEIVING_BONUS_FROM_MODULE_BOOL"); }
        catch { return false; }
        if (!receiving) return false;

        var parts = new List<string>(2);
        AddDelta(parts, _zh ? "处理速度" : "Speed", item,
            "BASE_PROCESSING_SPEED_TAG", "CURRENT_PROCESSING_SPEED_TAG");
        AddDelta(parts, _zh ? "耗电" : "Power", item,
            "MACHINERY_DRAWN_POWER_BASE", "machinery_drawn_power");
        if (parts.Count == 0) return false;

        AddLine(builder, (_zh ? "模组实际效果：" : "Module effect: ")
                        + string.Join(_zh ? " ｜ " : " | ", parts),
            RenderHandler.ColorPalette.LightTurquoise);
        return true;
    }

    /// <summary>基准与当前都读得到、且确实不同时才输出一段「基准 → 当前」。</summary>
    private static void AddDelta(List<string> parts, string label, GameItem item, string baseTag, string currentTag)
    {
        var b = GameRead.TagInt(item, baseTag);
        var c = GameRead.TagInt(item, currentTag);
        if (b is not int from || c is not int to) return;
        if (from == to) return;
        parts.Add($"{label} {from} → {to}");
    }

    // ---------------- 工具 ----------------

    /// <summary>输出模块已经定好颜色的一组行；返回是否真的写入了内容。</summary>
    private static bool AppendLines(RichTextBuilder builder, List<TooltipLine> lines)
    {
        foreach (var ln in lines)
            AddLine(builder, ln.Text, ln.Color);
        return lines.Count > 0;
    }

    /// <summary>输出同色的一组纯文本行（未做本地化的模块用）。</summary>
    private static bool AppendLines(RichTextBuilder builder, List<string> lines, RenderHandler.ColorPalette color)
    {
        foreach (string s in lines)
            AddLine(builder, s, color);
        return lines.Count > 0;
    }

    private static void AddLine(RichTextBuilder builder, string text, RenderHandler.ColorPalette color)
    {
        try
        {
            builder.AddLine(text, false, color, false, false, false, false,
                RenderHandler.ColorPalette.None, RenderHandler.ColorPalette.None, RenderHandler.ColorPalette.None);
            Log.MarkOk("AddLine");   // 恢复正常后清掉失败计数，下次再坏会重新报第一次
        }
        catch (Exception ex)
        {
            Log.Warn("AddLine", ex);
        }
    }
}
