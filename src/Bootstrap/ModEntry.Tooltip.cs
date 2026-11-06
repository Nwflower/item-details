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
            var table = WaterColumns.Build(r, visible: visible);
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
            // 固体化学品（药片、作物、粪便等）：没有容器与体积，所以不画条；
            // 成分与液体容器共用同一份行数据与同一种写法。
            var table = WaterColumns.BuildSolid(r, visible);
            PublishWaterRows(builder, visible);
            // 低于阈值那一行不是成分行，单发一个字段，读取方才能把它归到液体里。
            string? trace = WaterColumns.TraceLine(r);
            if (trace != null) builder.Field("trace", trace);
            AppendLines(builder, table);
            wrote = true;
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

        // 性状逐项一行太占版面，接在表头后写成一行
        if (builder.Capturing)
        {
            var traits = new List<(string Id, string Name, int Rank)>();
            foreach (var line in lines)
                if (line.Data is ValueTuple<string, string, int> trait) traits.Add(trait);
            builder.Field("traits", traits.AsReadOnly());
        }
        var texts = lines.ConvertAll(l => l.Text);
        builder.AddLine(Loc.T("seed.line", "Traits (unanalyzed): {0}", string.Join(Loc.T("list.sep", ", "), texts)),
            RenderHandler.ColorPalette.LightTurquoise);
        return true;
    }

    // ---------------- 含水容器：净化收益（探测版） ----------------
    private bool TryAppendWaterAdvice(TooltipSections.Writer builder, GameItem item)
    {
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
        bool wrote = false;
        foreach (var mtd in shown)
        {
            // 分隔用普通空格而不是全角空格：原版折行只在普通空格处断，
            // 建议行因此能自然折成两行，不会把 tooltip 撑宽。
            if (wrote) builder.Add(" ", RenderHandler.ColorPalette.Gray);
            builder.Add(mtd.Name + " +" + mtd.GainPercentagePoints.ToString("0.00", Loc.Culture) + "%", RenderHandler.ColorPalette.LightTurquoise);
            wrote = true;
        }
        builder.NextLine();

        return true;
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

    /// <summary>输出模块已经定好颜色的一组行；返回是否真的写入了内容。</summary>
    private static bool AppendLines(TooltipSections.Writer builder, List<TooltipLine> lines)
    {
        List<(int Line, string Kind, object Value)>? rowData = null;
        for (int i = 0; i < lines.Count; i++)
        {
            var ln = lines[i];
            if (builder.Capturing && ln.DataKind != null && ln.Data != null)
                (rowData ??= new()).Add((i, ln.DataKind, ln.Data));
            if (ln.HasBar) builder.Bar(ln);
            else if (ln.Render != null) builder.AddLine(ln.Text, ln.Color, ln.Render);
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
            if (ln.Text.Length > 0) builder.Add(ln.Text, ln.Color);
            builder.AddProgressBar(ln.Bar, ln.BarLength, ln.Color, ln.BarDark, end: " ");
            ProgressBarStyle.MarkOwnBar(builder);
            AddLine(builder, ln.Tail ?? "", ln.Color);
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

