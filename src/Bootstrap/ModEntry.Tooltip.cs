using System;
using System.Collections.Generic;
using ItemDetails.Integration;

namespace ItemDetails;

/// <summary>
/// 各 tooltip 追加器。顺序由 <c>ModEntry.Renderers</c> 决定，本文件只负责"某一类物品多写几行"。
/// 行怎么写出去在 <see cref="LineOutput"/>；液体与种子的区块较长，分别在
/// <see cref="WaterSections"/> 与 <see cref="SeedTraitSection"/>，这里只留入口。
///
/// 约定：模块自己判断开关与适用性，不适用就返回 false 且不写任何内容；
/// 真正的异常交给 <c>ModEntry.RunRenderers</c> 的边界统一处理，方法内不吞错。
/// </summary>
public sealed partial class ModEntry
{
    // ---------------- 真伪 ----------------
    private bool TryAppendAuthenticity(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.Auth) return false;
        var r = Authenticity.Analyze(item);
        if (!r.HasData) return false;

        if (r.Verdict != AuthVerdict.Genuine || r.IsGenuine)
        {
            builder.Field("genuine", r.IsGenuine);
            if (builder.Capturing) builder.Field("faults", r.FaultNames.AsReadOnly());
        }
        if (r.Verdict != AuthVerdict.Genuine)
        {
            string verdict = Authenticity.VerdictName(r);
            string names = string.Join(Loc.T("list.sep", ", "), r.FaultNames);
            string line = r.Faults == 0 ? "× " + verdict
                : r.Verdict == AuthVerdict.Counterfeit ? Loc.T("auth.counterfeit", "× Counterfeit ({0})", names)
                : Loc.T("auth.defect", "× {0} ({1})", verdict, names);
            builder.AddLine(line, RenderHandler.ColorPalette.LightRed);
        }
        else if (r.IsGenuine)
        {
            builder.AddLine(Loc.T("auth.genuine", "√ Genuine"), RenderHandler.ColorPalette.Green);
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
    private bool TryAppendValue(TooltipSections.Writer builder, GameItem item)
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
        builder.Field("total", r.Total);
        builder.Field("perfectMin", lo);
        builder.Field("perfectMax", hi);
        totalLine += Loc.T("value.perfect_range", " | perfect {0}-{1}", lo, hi);
        builder.AddLine(totalLine, RenderHandler.ColorPalette.Green);

        AppendParts(builder, r);
        return true;
    }

    /// <summary>
    /// 来源明细单行输出；标签已缩短，避免原版定宽折行把数字拆断。
    ///
    /// 符号与分隔符合一：正值写「+加固60」，负值写「-破裂150」并用红字。
    /// 旧写法把符号塞进数值（「+破裂-150」），一个负项会出现两个符号，既难读也占宽。
    /// </summary>
    private void AppendParts(TooltipSections.Writer builder, ValueBreakdown.Result r)
    {
        builder.Add(Loc.T("value.from", "From: "), RenderHandler.ColorPalette.White);

        var parts = builder.Capturing ? new List<(string Label, int Amount, bool IsBase)>() : null;
        foreach (var p in r.Parts)
        {
            // 文案由纯公式层给出「键 + 英文兜底 + 参数」，这里只做解析。
            string label = Loc.T(p.Key, p.En, p.Args);
            parts?.Add((label, p.Amount, p.IsBase));

            if (p.IsBase)
            {
                builder.Add(Part(label, p.Amount), RenderHandler.ColorPalette.White);
                continue;
            }

            // 符号紧跟标签，标签与数字之间的间隔交给词条：西文要空格，中日韩不要
            if (p.Amount < 0)
                builder.Add("-" + Part(label, -p.Amount), RenderHandler.ColorPalette.LightRed);
            else
                builder.Add("+" + Part(label, p.Amount), RenderHandler.ColorPalette.White);
        }

        if (parts != null) builder.Field("parts", parts.AsReadOnly());
        builder.NextLine();
    }

    /// <summary>
    /// 一项来源的写法：标签 + 数值。西文标签与数字之间要空格（<c>Base 100</c>），
    /// 中日韩不要（<c>基础100</c>），所以间隔写在词条 <c>value.part</c> 里
    /// （英文 <c>{0} {1}</c>、中日韩 <c>{0}{1}</c>），不在代码里按语言分支。
    /// 第三方注入的分解（<see cref="TryAppendInjectedValue"/>）走同一个词条，两条路径写法一致。
    /// </summary>
    private static string Part(string label, object amount)
        => Loc.T("value.part", "{0} {1}", label, amount);

    // ---------------- 赝品肋明治：具体原因 ----------------
    private bool TryAppendRibwichReasons(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.Auth) return false;   // 赝品原因属于真伪鉴定

        var lines = RibwichCounterfeit.Collect(item);
        if (lines.Count == 0) return false;
        if (builder.Capturing) builder.Field("reasons", lines.AsReadOnly());

        builder.AddLine(Loc.T("ribwich.header", "Why it's counterfeit:"), RenderHandler.ColorPalette.LightRed);
        foreach (string s in lines)
            builder.AddLine("• " + s, RenderHandler.ColorPalette.LightRed);
        return true;
    }

    // ---------------- 第三方显式提供的鉴定分解 ----------------
    private bool TryAppendInjectedValue(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.Value) return false;

        var r = ThirdPartyProviders.Appraisal(item);
        if (r == null) return false;

        string totalLine = Loc.T("value.total_line", "Total: {0}", r.Total);
        var (lo, hi) = AppraisalRanges.GamePerfect(r.Total);
        builder.Field("total", r.Total);
        builder.Field("perfectMin", lo);
        builder.Field("perfectMax", hi);
        totalLine += Loc.T("value.perfect_range", " | perfect {0}-{1}", lo, hi);
        builder.AddLine(totalLine, RenderHandler.ColorPalette.Green);

        var fields = builder.Capturing ? new List<(string Label, int Amount, bool IsBase)>() : null;
        var parts = new List<string>();
        foreach (var p in r.Parts)
        {
            if (string.IsNullOrWhiteSpace(p.Label)) continue;
            fields?.Add((p.Label, p.Amount, p.IsBase));
            string amount = p.IsBase || p.Amount < 0 ? p.Amount.ToString() : "+" + p.Amount;
            parts.Add(Part(p.Label, amount));
        }
        if (fields != null) builder.Field("parts", fields.AsReadOnly());
        if (parts.Count > 0)
            builder.AddLine(Loc.T("generic.line", "From: {0}", string.Join(Loc.T("list.sep", ", "), parts)),
                RenderHandler.ColorPalette.White);
        return true;
    }

    // ---------------- 果实品质（BERRY_*） ----------------
    private bool TryAppendBerry(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.Plant && !ModSettings.Wine) return false;

        var q = GameRead.ClassifyBerry(item);
        if (q == BerryQuality.NotBerry) return false;

        string? name = BerryQualityClassifier.DisplayName(q);
        bool showQuality = ModSettings.Plant && name != null;
        if (!showQuality && !ModSettings.Wine) return false;

        // 主体取物品自己的名字：BERRY_TAG 不是荧光莓专属，第三方果实只要沿用原版的
        // WineHelper.InitBerry 就会带上它（实测酿酒拓展的 18 种果实），写死"荧光莓"
        // 会把别人的水果一起叫成荧光莓。名字读不到就不写这行，不拿默认值凑结论。
        // 取名走当前语言（item.name 是创建时固化的，见 GameRead.ItemDisplayName）。
        string itemName = GameRead.ItemDisplayName(item);
        if (itemName.Length == 0 || itemName == "?") return false;

        // 较小用紫色：这是「发育不良但还能用」的一档，和发霉/干枯这类坏档区分开。
        var color = q switch
        {
            BerryQuality.Normal => RenderHandler.ColorPalette.Green,
            BerryQuality.Small => RenderHandler.ColorPalette.Purple,
            _ => RenderHandler.ColorPalette.Orange,
        };

        try
        {
            if (showQuality) builder.Field("quality", name!);
            if (showQuality)
                builder.Add(Loc.T("berry.line", "{0} quality: {1}", itemName, name!), color);
            // 酿酒值：这颗莓在 WineHelper.StartFermentation 里给果酿评分加的点数，灰字补在同一行末尾。
            if (ModSettings.Wine)
            {
                int wineScore = GameRead.BerryWineScore(item);
                builder.Field("wineScore", wineScore);
                string score = wineScore.ToString("+0;-0;0");
                builder.Add(showQuality
                        ? Loc.T("berry.wine", " (wine value {0})", score)
                        : Loc.T("berry.wine_value", "Wine value: {0}", score),
                    RenderHandler.ColorPalette.Gray);
            }
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
    private bool TryAppendHidden(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.HiddenTruth) return false;
        if (IsLiquid(item)) return false;   // 水质模块已汇总
        return LineOutput.AppendLines(builder, HiddenInfo.Collect(item));
    }

    // ---------------- 性价比（兜底） ----------------
    // 正常由 TooltipPipeline 写在价格块下一行；价格行没改写时才落到这里，同一次构建只写一次。
    private bool TryAppendValueEnhance(TooltipSections.Writer builder, GameItem item)
        => ValueEnhance.TryWrite(builder, item);

    // ---------------- P1 隐藏特性揭示（真假条件） ----------------
    private bool TryAppendFeatureReveal(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.HiddenTruth) return false;
        if (IsLiquid(item)) return false;   // 水质模块已汇总真假
        return LineOutput.AppendLines(builder, FeatureReveal.Collect(item));
    }

    // ---------------- 储存容器：压缩率 + 内容物图标格 ----------------
    private bool TryAppendStorage(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.ValueEnhance && !ModSettings.Container) return false;
        return LineOutput.AppendLines(builder, StorageInfo.Collect(item,
            includeRatio: ModSettings.ValueEnhance,
            includeContents: ModSettings.Container,
            allowGrid: true, builder: builder.Native));
    }

    // ---------------- 机器与槽位容器内部：滤芯、液体、工具、剩余几晚 ----------------
    private bool TryAppendMachineInterior(TooltipSections.Writer builder, GameItem item)
    {
        // 滤芯耐久归「信息增强」总开关（见 MachineInterior.Collect），所以这里只看它：
        // 容器内容物、机器、电池三项本来就是它的子项，总开关关掉时必然全关。
        if (!ModSettings.InfoEnhance) return false;
        return LineOutput.AppendLines(builder, MachineInterior.Collect(item,
            listing: ModSettings.Container,
            adapters: ModSettings.Machines,
            hiddenTruth: ModSettings.HiddenTruth,
            purity: LiquidGate.Enabled));
    }

    // ---------------- 自学习算法模块：学习进度 ----------------
    private bool TryAppendModuleLearning(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.ModuleEffects) return false;
        return LineOutput.AppendLines(builder, ModuleLearning.Collect(item, builder));
    }

    // ---------------- 瑕疵/状态对价值的影响 ----------------
    private bool TryAppendConditions(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.PriceImpact) return false;
        if (IsLiquid(item)) return false;   // 水质模块已汇总，避免重复
        return LineOutput.AppendLines(builder, ConditionEffects.Collect(item, skipRevealed: ModSettings.HiddenTruth));
    }

    // ---------------- 违禁等级与黑市溢价 ----------------
    private bool TryAppendContraband(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.Contraband) return false;

        var r = ContrabandInfo.Analyze(item);
        if (!r.HasData) return false;
        // 原版 ContrabandHelper 已经显示违禁等级，这里只补它没有的黑市溢价。
        if (r.MarkupPercent < 0) return false;
        builder.Field("markupPercent", r.MarkupPercent);

        builder.AddLine(
            Loc.T("contraband.markup", "Black market: +{0}%", r.MarkupPercent),
            RenderHandler.ColorPalette.LightTurquoise);
        return true;
    }

    // ---------------- 彩票开奖结果 ----------------
    private bool TryAppendLottery(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.Lottery) return false;
        return LineOutput.AppendLines(builder, LotteryInfo.Collect(item, builder));
    }

    // ---------------- 液体组分：水质 / 杂质 / 药物成分 ----------------
    private bool TryAppendWater(TooltipSections.Writer builder, GameItem item)
        => WaterSections.AppendAnalysis(builder, item);

    // ---------------- 密封箱预览（透视，默认关闭） ----------------
    private bool TryAppendSealedBox(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.SealedBox) return false;
        return LineOutput.AppendLines(builder, SealedBoxInfo.Collect(item, allowGrid: true, builder: builder.Native));
    }

    // ---------------- 刮刮卡结果（透视） ----------------
    private bool TryAppendScratcher(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.Scratcher) return false;
        return LineOutput.AppendLines(builder, ScratcherInfo.Collect(item));
    }

    // ---------------- 种子性状（透视） ----------------
    private bool TryAppendSeed(TooltipSections.Writer builder, GameItem item)
        => SeedTraitSection.Append(builder, item);

    // ---------------- 水龙头：出水水质 ----------------
    private bool TryAppendFaucet(TooltipSections.Writer builder, GameItem item)
        => WaterSections.AppendFaucet(builder, item);

    // ---------------- 含水容器：净化收益（探测版） ----------------
    private bool TryAppendWaterAdvice(TooltipSections.Writer builder, GameItem item)
        => WaterSections.AppendAdvice(builder, item);

    // ---------------- 酒：分数 / 成分 / 赝品（信息增强） ----------------
    private bool TryAppendWine(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.Wine) return false;
        var lines = WineInfo.Collect(item);
        if (lines.Count == 0) return false;
        LineOutput.AppendLines(builder, lines);
        return true;
    }

    // ---------------- 肉类来源 / 朊病毒（信息增强） ----------------
    private bool TryAppendMeat(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.Meat) return false;
        return LineOutput.AppendLines(builder, MeatInfo.Collect(item));
    }

    // ---------------- 市场修正（店铺事件对价格的百分比影响） ----------------
    private bool TryAppendMarket(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.PriceImpact) return false;
        return LineOutput.AppendLines(builder, MarketModifier.Collect(item, builder), RenderHandler.ColorPalette.Orange);
    }
}
