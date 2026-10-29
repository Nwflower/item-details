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
            string names = string.Join(Loc.T("list.sep", ", "), r.FaultNames);
            AddLine(builder, Loc.T("auth.counterfeit", "✘ Suspected counterfeit ({0})", names),
                RenderHandler.ColorPalette.LightRed);
        }
        else if (r.IsGenuine)
        {
            AddLine(builder, Loc.T("auth.genuine", "✔ Genuine"), RenderHandler.ColorPalette.Green);
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
        string totalLine = Loc.T("value.total_line", "Total: {0}", r.Total);
        var (lo, hi) = AppraisalRanges.Perfect(r.Total);
        totalLine += Loc.T("value.perfect_range", " | perfect {0}-{1}", lo, hi);
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
        builder.Add(Loc.T("value.from", "From: "), RenderHandler.ColorPalette.White);

        foreach (var p in r.Parts)
        {
            // 文案由纯公式层给出「键 + 英文兜底 + 参数」，这里只做解析。
            string label = Loc.T(p.Key, p.En, p.Args);

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

    // ---------------- 赝品肋明治：具体原因 ----------------
    private bool TryAppendRibwichReasons(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Value) return false;

        var lines = RibwichCounterfeit.Collect(item);
        if (lines.Count == 0) return false;

        AddLine(builder, Loc.T("ribwich.header", "Why counterfeit:"), RenderHandler.ColorPalette.LightRed);
        foreach (string s in lines)
            AddLine(builder, "• " + s, RenderHandler.ColorPalette.LightRed);
        return true;
    }

    // ---------------- 泛型分解（未知/模组新增的鉴定类型） ----------------
    private bool TryAppendGeneric(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Value) return false;

        var lines = GenericBreakdown.Collect(item);
        if (lines.Count == 0) return false;

        AddLine(builder, Loc.T("generic.header", "Value breakdown (measured):"),
            RenderHandler.ColorPalette.Yellow);
        return AppendLines(builder, lines, RenderHandler.ColorPalette.White);
    }

    // ---------------- 荧光莓 ----------------
    private bool TryAppendBerry(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Plant) return false;

        var q = GameRead.ClassifyBerry(item);
        if (q == BerryQuality.NotBerry) return false;

        string? name = BerryQualityClassifier.DisplayName(q);
        if (name == null) return false;

        AddLine(builder, Loc.T("berry.line", "Berry quality: {0}", name),
            q == BerryQuality.Normal ? RenderHandler.ColorPalette.Green : RenderHandler.ColorPalette.Orange);
        return true;
    }

    // ---------------- P2 隐藏信息（真实耐久/纯度） ----------------
    private bool TryAppendHidden(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.HiddenTruth) return false;
        if (IsLiquid(item)) return false;   // 水质模块已汇总
        return AppendLines(builder, HiddenInfo.Collect(item));
    }

    // ---------------- P3 价值增强（总价拆解/性价比） ----------------
    private bool TryAppendValueEnhance(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.ValueEnhance) return false;
        // 鉴定物品已有「总价/完美区间/来源」，性价比是按顾客报价算的，混在一起容易误读
        if (GameRead.IsTag(item, "APPRAISAL_ITEM")) return false;
        if (IsLiquid(item)) return false;   // 含水容器的性价比意义不大，且挤占版面
        return AppendLines(builder, ValueEnhance.Collect(item));
    }

    // ---------------- P1 隐藏特性揭示（真假条件） ----------------
    private bool TryAppendFeatureReveal(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.HiddenTruth) return false;
        if (IsLiquid(item)) return false;   // 水质模块已汇总真假
        return AppendLines(builder, FeatureReveal.Collect(item));
    }

    // ---------------- 储存容器：压缩率 + 内容物 ----------------
    private bool TryAppendStorage(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Container) return false;
        return AppendLines(builder, StorageInfo.Collect(item));
    }

    // ---------------- 瑕疵/状态对价值的影响 ----------------
    private bool TryAppendConditions(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.PriceImpact) return false;
        if (IsLiquid(item)) return false;   // 水质模块已汇总，避免重复
        return AppendLines(builder, ConditionEffects.Collect(item));
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
            Loc.T("contraband.markup", "Black market: +{0}%", r.MarkupPercent),
            RenderHandler.ColorPalette.LightTurquoise);
        return true;
    }

    // ---------------- 彩票开奖结果 ----------------
    private bool TryAppendLottery(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Lottery) return false;
        return AppendLines(builder, LotteryInfo.Collect(item));
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
        string head = Loc.T("water.head", "Water: {0}% {1}",
            purityPct.ToString("0.#"), r.Tier);

        var headColor = r.Purity >= WaterInfo.PurityGood ? RenderHandler.ColorPalette.LightGreen
                      : r.Purity >= WaterInfo.PurityFair ? RenderHandler.ColorPalette.LightYellow
                      : RenderHandler.ColorPalette.LightRed;

        // 标签写着的水质与实测档位不一致时，用灰字标注
        bool mismatch = !string.IsNullOrEmpty(r.Label)
                     && !string.IsNullOrEmpty(r.Tier)
                     && !string.Equals(r.Label, r.Tier, StringComparison.Ordinal);

        builder.Add(head.Trim(), headColor);
        if (mismatch)
            builder.Add(Loc.T("water.mismatch", " (label mismatch)"), RenderHandler.ColorPalette.Gray);
        builder.NextLine();

        // 第二行：杂质构成
        var sb = new StringBuilder();
        sb.Append(Loc.T("water.impurities", "Impurities {0}%", impurityPct.ToString("0.#")));
        if (r.Impurities.Count == 0)
        {
            sb.Append(Loc.T("water.none", ": none"));
        }
        else
        {
            // 只列非零杂质：少于 MinVisibleParts 的按 0.# 显示会变成 "0ml"，直接跳过
            sb.Append(Loc.T("water.list_head", ": "));
            bool any = false;
            for (int i = 0; i < r.Impurities.Count; i++)
            {
                if (r.Impurities[i].Parts < WaterInfo.MinVisibleParts) continue;
                if (any) sb.Append(Loc.T("list.sep", ", "));
                any = true;
                sb.Append(r.Impurities[i].Name).Append(' ').Append(WaterInfo.Ml(r.Impurities[i].Parts)).Append("ml");
            }
            if (!any) sb.Append(Loc.T("water.trace", "trace (below display threshold)"));
        }

        AddLine(builder, sb.ToString(),
            impurity == 0 ? RenderHandler.ColorPalette.LightGreen : RenderHandler.ColorPalette.LightRed);
        return true;
    }

    // ---------------- 密封箱预览（透视，默认关闭） ----------------
    private bool TryAppendSealedBox(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.SealedBox) return false;
        return AppendLines(builder, SealedBoxInfo.Collect(item));
    }

    // ---------------- 刮刮卡结果（透视） ----------------
    private bool TryAppendScratcher(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Scratcher) return false;
        return AppendLines(builder, ScratcherInfo.Collect(item));
    }

    // ---------------- 种子性状（透视） ----------------
    private bool TryAppendSeed(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Plant) return false;

        var lines = SeedInfo.Collect(item);
        if (lines.Count == 0) return false;

        AddLine(builder, Loc.T("seed.header", "Traits (unanalysed):"),
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
        var lines = WineInfo.Collect(item);
        if (lines.Count == 0) return false;
        AppendLines(builder, lines);
        return true;
    }

    // ---------------- 市场修正（店铺事件对价格的百分比影响） ----------------
    private bool TryAppendMarket(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.PriceImpact) return false;
        return AppendLines(builder, MarketModifier.Collect(item), RenderHandler.ColorPalette.Orange);
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
