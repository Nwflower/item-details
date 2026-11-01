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
            AddLine(builder, Loc.T("auth.counterfeit", "× Counterfeit ({0})", names),
                RenderHandler.ColorPalette.LightRed);
        }
        else if (r.IsGenuine)
        {
            AddLine(builder, Loc.T("auth.genuine", "√ Genuine"), RenderHandler.ColorPalette.Green);
        }
        else
        {
            // 有原版检查项读取失败，且没有发现已知瑕疵 → 不下"正品"结论
            Log.Warn("Auth.Incomplete",
                $"只读到 {r.ReadableFlags}/{r.ExpectedFlags} 项，跳过真伪结论");
            return false;
        }
        return true;
    }

    // ---------------- 价值构成（机械手/汉堡/等离子） ----------------
    private bool TryAppendValue(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Value) return false;
        if (!GameRead.IsTag(item, "APPRAISAL_ITEM")) return false;
        if (ThirdPartyProviders.HasAppraisal(item)) return false;

        var r = ValueBreakdown.Analyze(item);
        if (r == null) return false;

        // 总价与完美区间合并成一行，省一行留白。Value 已把完美区间并入同一开关。
        // Analyze 已校验 r.Total == 游戏真实值，不再重复调用 TrueValue。
        string totalLine = Loc.T("value.total_line", "Total: {0}", r.Total);
        var (lo, hi) = AppraisalRanges.GamePerfect(r.Total);
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
        if (!ModSettings.Auth) return false;   // 赝品原因属于真伪鉴定

        var lines = RibwichCounterfeit.Collect(item);
        if (lines.Count == 0) return false;

        AddLine(builder, Loc.T("ribwich.header", "Why it's counterfeit:"), RenderHandler.ColorPalette.LightRed);
        foreach (string s in lines)
            AddLine(builder, "• " + s, RenderHandler.ColorPalette.LightRed);
        return true;
    }

    // ---------------- 第三方显式提供的鉴定分解 ----------------
    private bool TryAppendInjectedValue(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Value) return false;

        var r = ThirdPartyProviders.Appraisal(item);
        if (r == null) return false;

        string totalLine = Loc.T("value.total_line", "Total: {0}", r.Total);
        var (lo, hi) = AppraisalRanges.GamePerfect(r.Total);
        totalLine += Loc.T("value.perfect_range", " | perfect {0}-{1}", lo, hi);
        AddLine(builder, totalLine, RenderHandler.ColorPalette.Green);

        var parts = new List<string>();
        foreach (var p in r.Parts)
        {
            if (string.IsNullOrWhiteSpace(p.Label)) continue;
            string amount = p.IsBase || p.Amount < 0 ? p.Amount.ToString() : "+" + p.Amount;
            parts.Add($"{p.Label} {amount}");
        }
        if (parts.Count > 0)
            AddLine(builder, Loc.T("generic.line", "From: {0}", string.Join(Loc.T("list.sep", ", "), parts)),
                RenderHandler.ColorPalette.White);
        return true;
    }

    // ---------------- 荧光莓 ----------------
    private bool TryAppendBerry(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Plant) return false;

        var q = GameRead.ClassifyBerry(item);
        if (q == BerryQuality.NotBerry) return false;

        string? name = BerryQualityClassifier.DisplayName(q);
        if (name == null) return false;

        // 较小用紫色：这是「发育不良但还能用」的一档，和发霉/干枯这类坏档区分开。
        var color = q switch
        {
            BerryQuality.Normal => RenderHandler.ColorPalette.Green,
            BerryQuality.Small => RenderHandler.ColorPalette.Purple,
            _ => RenderHandler.ColorPalette.Orange,
        };

        try
        {
            builder.Add(Loc.T("berry.line", "BloomBerry quality: {0}", name), color);
            // 酿酒值：这颗莓在 WineHelper.StartFermentation 里给果酿评分加的点数，灰字补在同一行末尾。
            builder.Add(Loc.T("berry.wine", " (wine value {0})",
                    GameRead.BerryWineScore(item).ToString("+0;-0;0")),
                RenderHandler.ColorPalette.Gray);
            builder.NextLine();
        }
        catch (Exception ex)
        {
            Log.Warn("Berry.AddLine", ex);
            return false;
        }
        return true;
    }

    // ---------------- P2 隐藏信息（真实耐久/纯度） ----------------
    private bool TryAppendHidden(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.HiddenTruth) return false;
        if (IsLiquid(item)) return false;   // 水质模块已汇总
        return AppendLines(builder, HiddenInfo.Collect(item));
    }

    // ---------------- 性价比（兜底） ----------------
    // 正常由 TooltipPipeline 写在价格块下一行；价格行没改写时才落到这里，同一次构建只写一次。
    private bool TryAppendValueEnhance(RichTextBuilder builder, GameItem item)
        => ValueEnhance.TryWrite(builder, item);

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
        if (!ModSettings.ValueEnhance && !ModSettings.Container) return false;
        return AppendLines(builder, StorageInfo.Collect(item,
            includeRatio: ModSettings.ValueEnhance,
            includeContents: ModSettings.Container,
            allowGrid: true));
    }

    // ---------------- 机器与槽位容器内部：滤芯、液体、工具、剩余几晚 ----------------
    private bool TryAppendMachineInterior(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.Container && !ModSettings.InfoEnhance) return false;
        return AppendLines(builder, MachineInterior.Collect(item,
            listing: ModSettings.Container,
            adapters: ModSettings.InfoEnhance,
            hiddenTruth: ModSettings.HiddenTruth,
            purity: LiquidGate.Enabled));
    }

    // ---------------- 自学习算法模块：学习进度 ----------------
    private bool TryAppendModuleLearning(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.ModuleEffects) return false;
        return AppendLines(builder, ModuleLearning.Collect(item));
    }

    // ---------------- 瑕疵/状态对价值的影响 ----------------
    private bool TryAppendConditions(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.PriceImpact) return false;
        if (IsLiquid(item)) return false;   // 水质模块已汇总，避免重复
        return AppendLines(builder, ConditionEffects.Collect(item, skipRevealed: ModSettings.HiddenTruth));
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

    // ---------------- 液体组分：水质 / 杂质 / 药物成分 ----------------
    private bool TryAppendWater(RichTextBuilder builder, GameItem item)
    {
        if (!LiquidGate.Enabled) return false;

        var r = WaterInfo.Analyze(item);
        if (!r.HasData) return false;

        bool wrote = false;

        // 第一行：水质纯度。无水时（纯药物/溶剂混合物）没有“纯度”可言，只列成分。
        if (r.WaterParts > 0)
        {
            double purityPct = r.Purity / 100.0;

            // 水相关百分比统一两位小数；标签行由 LabelImpact 原样输出，此处不重复。
            string head = Loc.T("water.head", "Water: {0}% {1}",
                purityPct.ToString("0.00", Loc.Culture), r.Tier);

            var headColor = r.Purity >= WaterInfo.PurityGood ? RenderHandler.ColorPalette.LightGreen
                          : r.Purity >= WaterInfo.PurityFair ? RenderHandler.ColorPalette.LightYellow
                          : RenderHandler.ColorPalette.LightRed;

            // 标签写着的水质与实测档位不一致时，用灰字标注
            bool mismatch = !string.IsNullOrEmpty(r.Label)
                         && !string.IsNullOrEmpty(r.Tier)
                         && !string.Equals(r.Label, r.Tier, StringComparison.Ordinal);

            builder.Add(head.Trim(), headColor);
            if (mismatch)
                builder.Add(Loc.T("water.mismatch", " (doesn't match label)"), RenderHandler.ColorPalette.Gray);
            builder.NextLine();
            wrote = true;
        }

        // 第二行：药物活性成分。游戏按绝对量判定效果（致死/中毒/兴奋等级/戒断/避孕），
        // 百分比无法表达 600 µl 与 602 µl 的差异，因此这里给精确用量。
        if (r.Actives.Count > 0)
        {
            var actives = new StringBuilder();
            actives.Append(Loc.T("water.actives", "Actives: "));
            for (int i = 0; i < r.Actives.Count; i++)
            {
                if (i > 0) actives.Append(Loc.T("list.sep", ", "));
                var active = r.Actives[i];
                actives.Append(active.Name).Append(' ').Append(WaterInfo.Amount(active.Parts));
            }
            AddLine(builder, actives.ToString(), RenderHandler.ColorPalette.LightYellow);
            wrote = true;
        }

        // 药效结论：与化学扫描仪同一组判定，省得玩家自己对阈值。
        if (r.HasEffects)
        {
            AddLine(builder, Loc.T("chem.effects", "Effects: {0}", ChemEffects(r)),
                r.LethalDose > 0 || r.Toxic ? RenderHandler.ColorPalette.LightRed : RenderHandler.ColorPalette.LightYellow);
            wrote = true;
        }

        // 第三行：杂质与试剂仍按占比；显示阈值只用于这一类，不裁掉小剂量药物。
        // 纯水没有杂质时不写「杂质：无」，第一行的纯度和档位已经说明了。
        if (r.Impurities.Count > 0)
        {
            var sb = new StringBuilder();
            // 无水混合物里酒精/提取液不是“杂质”，仍按占比但换个更准确的说法；
            // 固体化学品、以及只有试剂的液体容器（酸碱溶液瓶）没有活性成分时，
            // 这一行就是它的全部成分。
            if (r.WaterParts > 0) sb.Append(Loc.T("water.impurities", "Impurities: "));
            else if (r.Actives.Count == 0) sb.Append(Loc.T("water.composition", "Composition: "));
            else sb.Append(Loc.T("water.other", "Other: "));
            bool any = false;
            for (int i = 0; i < r.Impurities.Count; i++)
            {
                var part = r.Impurities[i];
                if (part.Parts < WaterInfo.MinVisibleParts) continue;

                double partPct = part.Parts * 100.0 / r.TotalParts;
                // 两位小数下会显示成 0.00% 的项不列，避免“有杂质但看起来是 0”。
                if (partPct < 0.005) continue;

                if (any) sb.Append(' ');
                any = true;
                sb.Append(part.Name).Append(' ').Append(partPct.ToString("0.00", Loc.Culture)).Append('%');
            }
            if (!any) sb.Append(Loc.T("water.trace", "trace (below display threshold)"));

            AddLine(builder, sb.ToString(),
                r.Solid ? RenderHandler.ColorPalette.LightCyan
                : r.TotalParts == r.WaterParts ? RenderHandler.ColorPalette.LightGreen
                : RenderHandler.ColorPalette.LightRed);
            wrote = true;
        }

        return wrote;
    }

    // ---------------- 密封箱预览（透视，默认关闭） ----------------
    private bool TryAppendSealedBox(RichTextBuilder builder, GameItem item)
    {
        if (!ModSettings.SealedBox) return false;
        return AppendLines(builder, SealedBoxInfo.Collect(item, allowGrid: true));
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

        // 性状逐项一行太占版面，接在表头后写成一行
        var texts = lines.ConvertAll(l => l.Text);
        AddLine(builder, Loc.T("seed.line", "Traits (unanalyzed): {0}", string.Join(Loc.T("list.sep", ", "), texts)),
            RenderHandler.ColorPalette.LightTurquoise);
        return true;
    }

    /// <summary>「致死（2 倍致死量）、强烈欣快、戒瘾」</summary>
    private static string ChemEffects(WaterInfo.Result r)
    {
        var parts = new List<string>();
        if (r.LethalDose > 0) parts.Add(Loc.T("chem.lethal", "lethal ({0}x lethal dose)", r.LethalDose));
        else if (r.Toxic) parts.Add(Loc.T("chem.toxic", "toxic"));
        switch (r.Euphoria)
        {
            case 1: parts.Add(Loc.T("chem.euphoria_weak", "weak euphoria")); break;
            case 2: parts.Add(Loc.T("chem.euphoria_mild", "mild euphoria")); break;
            case 3: parts.Add(Loc.T("chem.euphoria_strong", "strong euphoria")); break;
            case 4: parts.Add(Loc.T("chem.euphoria_extreme", "extreme euphoria")); break;
        }
        if (r.Synaprest) parts.Add(Loc.T("chem.synaprest", "anti-addiction"));
        if (r.Contraceptive) parts.Add(Loc.T("chem.contraceptive", "contraceptive"));
        return string.Join(Loc.T("list.sep", ", "), parts);
    }

    // ---------------- 含水容器：净化收益（探测版） ----------------
    private bool TryAppendWaterAdvice(RichTextBuilder builder, GameItem item)
    {
        if (!LiquidGate.Enabled) return false;

        // 按玩家实际拥有的手段逐项列出纯度提升（每种都在独立深拷贝探针上模拟）
        var methods = WaterAdvice.SimulateMethods(item);
        if (methods.Count == 0) return false;

        bool wrote = false;
        foreach (var mtd in methods)
        {
            if (wrote) builder.Add("　", RenderHandler.ColorPalette.Gray);
            builder.Add(mtd.Name + " +" + mtd.GainPct.ToString("0.00", Loc.Culture) + "%", RenderHandler.ColorPalette.LightTurquoise);
            wrote = true;
        }
        builder.NextLine();

        return true;
    }

    // ---------------- 酒：分数 / 成分 / 赝品（信息增强） ----------------
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
        {
            if (ln.HasBar) AddBarLine(builder, ln);
            else AddLine(builder, ln.Text, ln.Color);
        }
        return lines.Count > 0;
    }

    /// <summary>
    /// 输出进度条行：前缀 → 条文本 → 后缀。
    /// 不调用原版 AddProgressBar()，避免该方法添加的 noBreak 标记改变整个 tooltip 在 CJK 下的换行宽度。
    /// </summary>
    private static void AddBarLine(RichTextBuilder builder, TooltipLine ln)
    {
        try
        {
            if (ln.Text.Length > 0) builder.Add(ln.Text, ln.Color);
            string bar = ProgressBarStyle.Format(ln.Bar, ln.BarLength, ln.BarDark, fullLength: true);
            builder.Add(bar, ln.Color, end: " ");
            AddLine(builder, ln.Tail ?? "", ln.Color);
        }
        catch (Exception ex)
        {
            Log.Warn("AddBarLine", ex);
        }
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
