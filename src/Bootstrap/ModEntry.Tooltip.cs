using System;
using System.Collections.Generic;
using ItemDetails.Integration;

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
    private bool TryAppendAuthenticity(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.Auth) return false;
        var r = Authenticity.Analyze(item);
        if (!r.HasData) return false;

        if (r.Faults > 0 || r.IsGenuine)
        {
            builder.Field("genuine", r.IsGenuine && r.Faults == 0);
            if (builder.Capturing) builder.Field("faults", r.FaultNames.AsReadOnly());
        }
        if (r.Faults > 0)
        {
            string names = string.Join(Loc.T("list.sep", ", "), r.FaultNames);
            builder.AddLine(Loc.T("auth.counterfeit", "× Counterfeit ({0})", names),
                RenderHandler.ColorPalette.LightRed);
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
                builder.Add($"{label}{p.Amount}", RenderHandler.ColorPalette.White);
                continue;
            }

            if (p.Amount < 0)
                builder.Add($"-{label}{-p.Amount}", RenderHandler.ColorPalette.LightRed);
            else
                builder.Add($"+{label}{p.Amount}", RenderHandler.ColorPalette.White);
        }

        if (parts != null) builder.Field("parts", parts.AsReadOnly());
        builder.NextLine();
    }

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
            parts.Add($"{p.Label} {amount}");
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
        return AppendLines(builder, HiddenInfo.Collect(item));
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
        return AppendLines(builder, FeatureReveal.Collect(item));
    }

    // ---------------- 储存容器：压缩率 + 内容物图标格 ----------------
    private bool TryAppendStorage(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.ValueEnhance && !ModSettings.Container) return false;
        return AppendLines(builder, StorageInfo.Collect(item,
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
        return AppendLines(builder, MachineInterior.Collect(item,
            listing: ModSettings.Container,
            adapters: ModSettings.Machines,
            hiddenTruth: ModSettings.HiddenTruth,
            purity: LiquidGate.Enabled));
    }

    // ---------------- 自学习算法模块：学习进度 ----------------
    private bool TryAppendModuleLearning(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.ModuleEffects) return false;
        return AppendLines(builder, ModuleLearning.Collect(item, builder));
    }

    // ---------------- 瑕疵/状态对价值的影响 ----------------
    private bool TryAppendConditions(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.PriceImpact) return false;
        if (IsLiquid(item)) return false;   // 水质模块已汇总，避免重复
        return AppendLines(builder, ConditionEffects.Collect(item, skipRevealed: ModSettings.HiddenTruth));
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
        return AppendLines(builder, LotteryInfo.Collect(item, builder));
    }

    // ---------------- 液体组分：水质 / 杂质 / 药物成分 ----------------
    private bool TryAppendWater(TooltipSections.Writer builder, GameItem item)
    {
        var r = LiquidQueries.Analyze(item);
        if (r == null) return false;
        builder.Field("analysis", r.Data);
        // 专属的液体显示方也走区块接管：回调拿到同一份分段数据，返回 true 表示本次由它绘制。
        if (LiquidQueries.TryDisplay(ItemDetailsClient.AnalysisDisplay, item, r.Data)) return true;

        bool wrote = false;

        // 只有接管方在看的时候才留一份行数据，平时不付这份分配。
        var visible = builder.Capturing ? new List<WaterRow>() : null;

        if (!r.IsSolid)
        {
            // 液体容器：表头只画「体积条 + 2000/2000mL 档位」，下面每种成分一行；
            // 单元格按全角空格分隔，原版窗口构建时换成 TMP 的 <pos> 三列（见 WaterColumns）。
            // 净化建议开着时先量出它的右端，成分表的百分比列据此对齐（建议结果按 tooltip 缓存，不会算两遍）
            float adviceEdge = 0f;
            if (ModSettings.WaterAdvice && LiquidQueries.GetAdvice(item) is { Count: > 0 } advice)
                adviceEdge = WaterColumns.AdviceRightEdge(DedupeByGain(advice));
            var table = WaterColumns.Build(r, visible: visible, adviceRightEdge: adviceEdge);
            PublishWaterRows(builder, visible);
            // 折行预算按本表实际最宽的一行算：原版 40（中文折算 26）通常够用，
            // 只有名称特别长的行才放宽，且只放宽到够用。以前一律放到 64，
            // 连净化建议那条长行也不再折行，把整个 tooltip 撑宽。
            RequireTableWidth(builder, table);
            AppendLines(builder, table);
            wrote = true;
        }
        else
        {
            // 固体化学品：没有容器与体积，所以不画条；成分与液体容器共用同一份行数据。
            // 带化学纯度体系的固体，或含药物活性成分的作物才画表，判断见 ShowSolidTable。
            if (ShowSolidTable(item, r))
            {
                // 作物只留活性成分，按 Ctrl 展开其余；化学品（药片、粉末）仍全部列出
                var table = WaterColumns.BuildSolid(r, visible,
                    foldInactive: !GameRead.IsTag(item, "CHEMICAL_PRODUCT_PURITY_TAG"));
                PublishWaterRows(builder, visible);
                // 低于阈值那一行不是成分行，单发一个字段，读取方才能把它归到液体里。
                string? trace = WaterColumns.TraceLine(r);
                if (trace != null) builder.Field("trace", trace);
                AppendLines(builder, table);
                wrote = true;
            }
        }

        string effects = r.Effects.Text;
        if (effects.Length > 0)
        {
            builder.AddLine(Loc.T("chem.effects", "Effects: {0}", effects),
                r.Effects.LethalDose > 0 || r.Effects.Toxic ? RenderHandler.ColorPalette.LightRed : RenderHandler.ColorPalette.LightYellow);
            wrote = true;
        }

        return wrote;
    }

    /// <summary>
    /// 固体物品是否画成分表，满足任一条：
    /// 一、带化学纯度体系的化学品，即游戏用 <c>ChemicalProductHelper.GradeProduct</c>
    /// 写入 CHEMICAL_PRODUCT_PURITY_TAG 的物品（药片、粉末、诺莫注射器）；
    /// 二、成分里有药物活性成分（被植物性状注入成分的营养果等作物）：玩家要看的就是活性成分的量。
    /// 没有活性成分的作物、菌菇、粪便、净水片这些，成分表读到的只是内部随机数，
    /// 画出来既不解释价格也帮不上玩家，只留药效结论。
    /// </summary>
    private static bool ShowSolidTable(GameItem item, LiquidAnalysis analysis)
    {
        if (GameRead.IsTag(item, "CHEMICAL_PRODUCT_PURITY_TAG")) return true;
        foreach (var component in analysis.Components)
            if (component.Role == "active") return true;
        return false;
    }

    /// <summary>
    /// 成分表需要多宽的折行预算：取表里最长一行的可见字符数（跳过富文本标签），
    /// 超过当前预算才放宽，且只放到刚好够用。中日韩按原版 0.66 系数折算。
    /// </summary>
    private static void RequireTableWidth(TooltipSections.Writer builder, List<TooltipLine> table)
    {
        try
        {
            int widest = 0;
            foreach (var line in table)
            {
                int visible = ProgressBarStyle.VisibleLength(line.Text);
                if (line.HasBar)
                    visible += line.BarLength + 1 + ProgressBarStyle.VisibleLength(line.Tail);
                widest = Math.Max(widest, visible);
            }
            if (widest <= 0) return;

            float scale = UISettings.current != null ? UISettings.current.GetCharWidthScale() : 1f;
            int limit = scale > 1f ? (int)(builder.MaxCharWidth * 0.66f) : builder.MaxCharWidth;
            if (widest <= limit) return;
            int need = scale > 1f ? (int)Math.Ceiling(widest / 0.66f) : widest;
            builder.RequireWidth(need + 1);
        }
        catch (Exception ex) { Log.Warn("RequireTableWidth", ex); }
    }

    /// <summary>
    /// 把这一帧实际画出的成分行交给接管方：名称、用量、百分比已经是分好的格子，
    /// 不必再从全角空格或 &lt;pos&gt; 里反推。折叠提示行的用量为空，百分比是折起来的合计。
    /// 只在观察或接管方在场时写入，字段缺失时读取方按「没有表格」处理。
    /// </summary>
    private static void PublishWaterRows(TooltipSections.Writer builder, List<WaterRow>? rows)
    {
        if (rows == null || rows.Count == 0) return;
        var payload = new List<(string Kind, string Name, string Amount, string Percent)>(rows.Count);
        foreach (var row in rows)
            payload.Add((row.Kind == WaterRowKind.Component
                ? WaterRowKinds.Component
                : WaterRowKinds.FoldHint, row.Name, row.Amount, row.Percent));
        IReadOnlyList<(string Kind, string Name, string Amount, string Percent)> published = payload.AsReadOnly();
        builder.Field("rows", published);
    }

    // ---------------- 密封箱预览（透视，默认关闭） ----------------
    private bool TryAppendSealedBox(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.SealedBox) return false;
        return AppendLines(builder, SealedBoxInfo.Collect(item, allowGrid: true, builder: builder.Native));
    }

    // ---------------- 刮刮卡结果（透视） ----------------
    private bool TryAppendScratcher(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.Scratcher) return false;
        return AppendLines(builder, ScratcherInfo.Collect(item));
    }

    // ---------------- 种子性状（透视） ----------------
    private bool TryAppendSeed(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.Plant) return false;

        var lines = SeedInfo.Collect(item);
        if (lines.Count == 0) return false;

        var traits = new List<(string Id, string Name, int Rank)>();
        foreach (var line in lines)
            if (line.Data is ValueTuple<string, string, int> trait) traits.Add(trait);
        if (builder.Capturing) builder.Field("traits", traits.AsReadOnly());

        // 表头 + 每个性状一行：名称靠左，等级靠右（与已分析的基因读数同一风格，只是没有效果说明）。
        // 规范文本里名称与等级用全角空格隔开，<pos> 只在原版窗口构建时换入。
        var color = RenderHandler.ColorPalette.LightTurquoise;
        var rows = new List<TooltipLine>(lines.Count + 1)
        {
            new TooltipLine(Loc.T("seed.header", "Genes (unanalyzed)"), color),
        };
        float nameEm = 0f, rankEm = 0f;
        var ranks = new string[traits.Count];
        for (int i = 0; i < traits.Count; i++)
        {
            ranks[i] = traits[i].Rank > 1 ? Loc.T("seed.rank", "Rank {0}", traits[i].Rank) : "";
            nameEm = Math.Max(nameEm, WaterColumns.TextEm(traits[i].Name));
            rankEm = Math.Max(rankEm, WaterColumns.TextEm(ranks[i]));
        }
        float rightEdge = nameEm + 0.9f + rankEm;
        for (int i = 0; i < traits.Count; i++)
        {
            string text = ranks[i].Length > 0 ? traits[i].Name + '　' + ranks[i] : traits[i].Name;
            var row = new TooltipLine(text, color);
            if (ranks[i].Length > 0)
                row = row.WithRender(traits[i].Name
                    + ModuleEffectAnnotation.Pos(rightEdge - WaterColumns.TextEm(ranks[i])) + ranks[i]);
            rows.Add(row);
        }
        foreach (var line in lines)
            if (line.Data is not ValueTuple<string, string, int>) rows.Add(line);   // 「…另有 N 项」
        return AppendLines(builder, rows);
    }

    // ---------------- 水龙头：出水水质 ----------------
    private bool TryAppendFaucet(TooltipSections.Writer builder, GameItem item)
    {
        if (!FaucetInfo.IsFaucet(item) || !LiquidGate.Enabled) return false;
        var analysis = FaucetInfo.Analyze(item);
        if (analysis == null) return false;
        builder.Field("analysis", analysis.Data);
        var lines = new List<TooltipLine>
        {
            new(Loc.T("faucet.header", "Tap water: {0}", analysis.Tier), RenderHandler.ColorPalette.LightCyan),
        };
        lines.AddRange(WaterColumns.BuildPercentOnly(analysis));
        RequireTableWidth(builder, lines);
        return AppendLines(builder, lines);
    }

    // ---------------- 含水容器：净化收益（探测版） ----------------
    private bool TryAppendWaterAdvice(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.WaterAdvice) return false;
        var methods = LiquidQueries.GetAdvice(item);
        if (methods == null) return false;
        // 过滤结果完全相同的手段只留第一个：滤水器与高级滤水器在这瓶水上收益一样时，
        // 写两遍只是重复同一个数字。接管方拿到的也是去重后的列表。
        var shown = DedupeByGain(methods);
        builder.Field("methods", shown);
        if (LiquidQueries.TryDisplay(ItemDetailsClient.AdviceDisplay, item, shown)) return true;
        if (shown.Count == 0) return builder.Capturing;

        // 建议行不参与「折叠水质成分」：那条折叠只收 1% 以下的小成分，
        // 建议本身是玩家操作水时的依据，始终画出来，不再要求按 Ctrl。
        // 一种手段一行，增益右对齐到成分表的百分比列
        // 折行预算按最长的一行留：名称里常有空格，预算不够时原版会在名称中间折行，右对齐随之错位
        foreach (var m in shown)
            builder.RequireWidth(ProgressBarStyle.WidthForLine(ProgressBarStyle.VisibleLength(m.Name + "　+00.00%")));
        return AppendLines(builder, WaterColumns.AdviceLines(shown, RenderHandler.ColorPalette.LightTurquoise));
    }

    /// <summary>按提升幅度去重：收益相同的手段只留第一个（顺序即扫描顺序）。</summary>
    private static List<(string Name, double GainPercentagePoints)> DedupeByGain(
        IReadOnlyList<(string Name, double GainPercentagePoints)> methods)
    {
        var seen = new HashSet<double>();
        var result = new List<(string Name, double GainPercentagePoints)>(methods.Count);
        foreach (var m in methods)
            if (seen.Add(m.GainPercentagePoints)) result.Add(m);
        return result;
    }

    // ---------------- 酒：分数 / 成分 / 赝品（信息增强） ----------------
    private bool TryAppendWine(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.Wine) return false;
        var lines = WineInfo.Collect(item);
        if (lines.Count == 0) return false;
        AppendLines(builder, lines);
        return true;
    }

    // ---------------- 肉类来源 / 朊病毒（信息增强） ----------------
    private bool TryAppendMeat(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.Meat) return false;
        return AppendLines(builder, MeatInfo.Collect(item));
    }

    // ---------------- 市场修正（店铺事件对价格的百分比影响） ----------------
    private bool TryAppendMarket(TooltipSections.Writer builder, GameItem item)
    {
        if (!ModSettings.PriceImpact) return false;
        return AppendLines(builder, MarketModifier.Collect(item, builder), RenderHandler.ColorPalette.Orange);
    }

    // ---------------- 工具 ----------------

    /// <summary>
    /// 带条的行最多占多少个字符宽：窗口当前预算不够时，在这个范围内允许把窗口撑宽一点
    /// （下面输出时会按整行预留宽度），超过就宁可不画条。
    /// </summary>
    private const int MaxBarLineChars = 34;

    /// <summary>
    /// 充能行的进度条整组处理：每行先算自己放得下的最长条（最短 3 格），整组取最短的那个格数，
    /// 这样条一样长；只要有一行连 3 格也放不下，整组都不画条、只留文字。
    /// 没有充能条时返回 null。
    /// </summary>
    private static Func<TooltipLine, TooltipLine>? GroupBarFit(List<TooltipLine> lines, int budget)
    {
        int group = int.MaxValue;
        foreach (var ln in lines)
        {
            if (!ln.HasBar || ln.DataKind != "recharge") continue;
            int rest = ProgressBarStyle.VisibleLength(ln.Text) + 3 + ProgressBarStyle.VisibleLength(ln.Tail ?? "");
            int fit = 0;
            for (int length = ln.BarLength; length >= 3; length--)
                if (rest + length <= MaxBarLineChars || ProgressBarStyle.WidthForLine(rest + length) <= budget) { fit = length; break; }
            group = Math.Min(group, fit);
        }
        if (group == int.MaxValue) return null;
        return ln => group == 0 ? ln.WithoutBar() : ln.BarLength == group ? ln : ln.WithBarLength(group);
    }

    /// <summary>输出模块已经定好颜色的一组行；返回是否真的写入了内容。</summary>
    private static bool AppendLines(TooltipSections.Writer builder, List<TooltipLine> lines)
    {
        List<(int Line, string Kind, object Value)>? rowData = null;
        Func<TooltipLine, TooltipLine>? barFit = GroupBarFit(lines, builder.MaxCharWidth);
        for (int i = 0; i < lines.Count; i++)
        {
            var ln = lines[i];
            if (ln.HasBar && ln.DataKind == "recharge") ln = barFit == null ? ln : barFit(ln);
            if (builder.Capturing && ln.DataKind != null && ln.Data != null)
                (rowData ??= new()).Add((i, ln.DataKind, ln.Data));
            if (ln.HasBar)
            {
                // 说明 + 条 + 尾注要排在一行：窗口里没有更宽的行时，尾注会被折到下一行且丢掉缩进
                if (ln.Render != null && ln.Render.Contains("<pos=", StringComparison.Ordinal))
                    builder.RequireWidth(ProgressBarStyle.WidthForLine(
                        ProgressBarStyle.VisibleLength(ln.Text) + ln.BarLength + 3
                        + ProgressBarStyle.VisibleLength(ln.Tail ?? "")) + 4);
                builder.Bar(ln);
            }
            else if (ln.Render != null)
            {
                // 两列行：规范文本比当前折行预算长时，原版会在规范文本里断行，渲染写法随之被切断，
                // 玩家就会看到半截 <color=…> 标签。窗口里没有更宽的行（如没装模组）时尤其常见，所以这里按需放宽预算
                if (ln.Render.Contains("<pos=", StringComparison.Ordinal))
                    builder.RequireWidth(ProgressBarStyle.WidthForLine(Math.Max(
                        ProgressBarStyle.VisibleLength(ln.Text), ProgressBarStyle.VisibleLength(ln.Render) + 6)) + 4);
                builder.AddLine(ln.Text, ln.Color, ln.Render);
            }
            else builder.AddLine(ln.Text, ln.Color);
        }
        if (rowData != null) builder.Field("rowData", rowData.AsReadOnly());
        return lines.Count > 0;
    }

    /// <summary>
    /// 输出进度条行：说明 → 条 → 尾注，一条一行。
    /// 条一律交给游戏自己的 <c>AddProgressBar</c>：它写出原版 <c>[=====     ]</c> 并把这条标成不折行片段。
    /// 逻辑层因此不必知道谁来画——原版窗口用渲染层还原成高清条，排版方从同一个入口读比例。
    /// 条的高精度比例与底色由 <see cref="ProgressBarStyle"/> 在同一个入口记录，原版会丢掉底色。
    /// </summary>
    internal static void AddBarLine(RichTextBuilder builder, TooltipLine ln)
    {
        try
        {
            // 行首呈现里可带「\u0007剩余点数」：只给学习条用，名字后面补「还需 N 晚」
            string? headRender = ln.Render;
            int gainPoints = 0;
            if (headRender != null)
            {
                int gm = headRender.IndexOf(MachineAdapters.GainMark);
                if (gm >= 0)
                {
                    int.TryParse(headRender.Substring(gm + 1), out gainPoints);
                    headRender = gm > 0 ? headRender.Substring(0, gm) : null;
                }
            }
            int remaining = 0, progress = 0, progressMax = 0;
            bool estimateDays = false;
            if (headRender != null)
            {
                int mark = headRender.IndexOf(SelfLearningMachine.RemainingMark);
                if (mark >= 0)
                {
                    string[] fields = headRender.Substring(mark + 1).Split(';');
                    int.TryParse(fields[0], out remaining);
                    if (fields.Length >= 4)
                    {
                        int.TryParse(fields[1], out progress);
                        int.TryParse(fields[2], out progressMax);
                        estimateDays = fields[3] == "1";
                    }
                    headRender = mark > 0 ? headRender.Substring(0, mark) : null;
                }
            }
            if (ln.Text.Length > 0)
            {
                // 行首文字的呈现写法（例如缩进）：规范文本不变，原版窗口构建时才换入
                int before = headRender != null ? RenderSlices.Count(builder) : -1;
                builder.Add(ln.Text, ln.Color);
                if (before >= 0)
                    RenderSlices.Annotate(builder, before, ln.Text, new RenderSlices.Style(ln.Color), headRender);
            }
            builder.AddProgressBar(ln.Bar, ln.BarLength, ln.Color, ln.BarDark, end: " ");
            ProgressBarStyle.MarkOwnBar(builder);
            string tail = ln.Tail ?? "";
            if (remaining > 0 && tail.Length > 0)
                RenderSlices.AddLine(builder, tail, ln.Color,
                    tail + "  " + progress + "/" + progressMax
                    + (estimateDays ? " " + SelfLearningMachine.NightsStart + remaining + SelfLearningMachine.NightsEnd : ""));
            else if (MachineAdapters.RechargeTailRender(tail, gainPoints) is string tailRender && tailRender != tail)
                RenderSlices.AddLine(builder, tail, ln.Color, tailRender);
            else
                AddLine(builder, tail, ln.Color);
        }
        catch (Exception ex)
        {
            Log.Warn("AddBarLine", ex);
        }
    }

    /// <summary>
    /// 原地改写行的进度条：条直接放在文字后面，不再另起一行说明。
    /// 调用方负责为完整行（含先前写出的头部）预留宽度；本方法负责收行，不能再追加 NextLine。
    /// </summary>
    internal static void AddInlineBarLine(RichTextBuilder builder, TooltipLine ln)
    {
        try
        {
            if (ln.Text.Length > 0) builder.Add(ln.Text, ln.Color);
            builder.AddProgressBar(ln.Bar, ln.BarLength, ln.Color, ln.BarDark, end: " ");
            ProgressBarStyle.MarkOwnBar(builder);
            if (!string.IsNullOrEmpty(ln.Tail)) builder.Add(ln.Tail, ln.Color);
            builder.NextLine();
        }
        catch (Exception ex)
        {
            Log.Warn("AddInlineBarLine", ex);
        }
    }

    /// <summary>输出同色的一组纯文本行（未做本地化的模块用）。</summary>
    private static bool AppendLines(TooltipSections.Writer builder, List<string> lines, RenderHandler.ColorPalette color)
    {
        foreach (string s in lines)
            builder.AddLine(s, color);
        return lines.Count > 0;
    }

    internal static void AddLine(RichTextBuilder builder, string text, RenderHandler.ColorPalette color)
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

