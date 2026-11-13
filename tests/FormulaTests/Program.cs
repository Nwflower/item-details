using System;
using System.Collections.Generic;
using System.Linq;
using ItemDetails;

int failures = 0;
Check("intact plasma breakdown reconciles with native value", () =>
    Reconciles(ValueBreakdown.ComputePlasma(Plasma("NONE", "STANDARD")), 300));
Check("minimum plasma price is explained by its contributions", () =>
    Reconciles(ValueBreakdown.ComputePlasma(Plasma("CRACK", "POOR1")), 90));
Check("minimum hand price is explained by its contributions", () =>
{
    var tags = new Dictionary<string, string>
    {
        ["HAND_MATERIAL"] = "ALUMINUM_ALLOY", ["HAND_INTERFACE"] = "UNSPECIFIED",
        ["HAND_CONDITION"] = "DAMAGE", ["HAND_ADDON_ARMORED"] = "NONE",
        ["HAND_ADDON_BLADED"] = "NONE",
    };
    Reconciles(ValueBreakdown.ComputeHand(key => tags.GetValueOrDefault(key)), 50);
});
Check("furnace favors ore and changes its input/output count with blast module", () =>
{
    Require(FurnacePurity.MaterialOffset(0, false) == 0, "scrap-only base offset");
    Require(FurnacePurity.MaterialOffset(1, false) == 1, "mixed scrap/ore base offset");
    Require(FurnacePurity.MaterialOffset(2, false) == 2, "ore-only base offset");
    Require(FurnacePurity.MaterialOffset(2, true) == 1, "blast must use 3 inputs");
    Require(FurnacePurity.MaterialOffset(3, true) == 2, "blast favors the available ore");
    Require(FurnacePurity.InputCount(true) == 3 && FurnacePurity.OutputCount(true) == 2,
        "blast input/output yield");
});
Check("furnace bonus is tiered; blast penalty comes before flux floor", () =>
{
    Require(FurnacePurity.Offset(99, false, false) == 0, "+99% not a whole tier");
    Require(FurnacePurity.Offset(100, false, false) == 1, "+100% adds a tier");
    Require(FurnacePurity.Offset(200, false, false) == 2, "+200% adds two tiers");
    Require(FurnacePurity.Offset(0, true, false) == -1, "blast penalty");
    Require(FurnacePurity.Offset(0, true, true) == 1, "flux floor after blast penalty");
    Require(FurnacePurity.Offset(100, true, true) == 1, "flux doesn't stack on top");
});
Check("furnace consumes basic flux only when its floor actually applies", () =>
{
    Require(FurnacePurity.ConsumesBasicFlux(0, false, true, false), "no quality bonus");
    Require(!FurnacePurity.ConsumesBasicFlux(100, false, true, false), "native quality already sufficient");
    Require(FurnacePurity.ConsumesBasicFlux(100, true, true, false), "blast needs extra flux");
    Require(!FurnacePurity.ConsumesBasicFlux(200, true, true, false), "blast recovered by quality");
    Require(!FurnacePurity.ConsumesBasicFlux(0, false, true, true), "advanced flux takes priority");
});
Check("furnace next quality threshold accounts for flux and capped output", () =>
{
    Require(FurnacePurity.NextImprovement(125, q => FurnacePurity.Offset(q, false, false)) == 75,
        "next +200% threshold");
    Require(FurnacePurity.NextImprovement(0, q => FurnacePurity.Offset(q, false, true)) == 200,
        "flux already supplies first tier");
    Require(FurnacePurity.NextImprovement(0, q => FurnacePurity.Offset(q, true, true)) == 300,
        "blast and flux require three tiers of quality");
    Require(FurnacePurity.NextImprovement(200, q => Math.Min(10,
        8 + FurnacePurity.Offset(q, false, false))) == -1,
        "already at maximum ingot purity");
});
Check("small liquid amount in a large container stays in milliliters", () =>
{
    if (Volume.UseLiters(1)) throw new Exception("1 ml should remain in milliliters");
    if (Volume.UseLiters(3000)) throw new Exception("exactly 3000 ml should remain in milliliters");
});
Check("liquid amounts above 3 liters use liters", () =>
{
    if (!Volume.UseLiters(3001)) throw new Exception("3001 ml should use liters");
});
Check("component table folds sub-1% rows into a hint with the empty amount cell", () =>
{
    var table = WaterTable.Build(Components(), 1_908_000, expanded: false,
        "Ctrl to expand", parts => parts + "u", System.Globalization.CultureInfo.InvariantCulture);
    Require(table.Rows.Count == 3, "two bulky components plus one hint row");
    Require(table.Rows[0].Kind == WaterRowKind.Component && table.Rows[0].Percent == "78.62%", "water percent");
    Require(table.Rows[1].Kind == WaterRowKind.Component, "chemical pollutant stays visible");
    var hint = table.Rows[2];
    Require(hint.Kind == WaterRowKind.FoldHint, "last row is the fold hint");
    Require(hint.Amount.Length == 0, "hint leaves the amount cell empty");
    Require(hint.Percent == "0.42%", "hint percent is the folded total, got " + hint.Percent);
    Require(table.FoldedCount == 2 && table.FoldedParts == 8_000, "both traces folded");
});
Check("component table lists everything and drops the hint while expanded", () =>
{
    var table = WaterTable.Build(Components(), 1_908_000, expanded: true,
        "Ctrl to expand", parts => parts + "u", System.Globalization.CultureInfo.InvariantCulture);
    Require(table.Rows.Count == 4, "expanded table has no hint row");
    foreach (var row in table.Rows) Require(row.Kind == WaterRowKind.Component, "only component rows");
    Require(table.FoldedCount == 0, "nothing folded while expanded");
});
Check("components fold by the caller's rule and keep the hint", () =>
{
    // 只留第 0 项（活性成分），其余按「非活性」折起
    var table = WaterTable.Build(Components(), 1_908_000, expanded: false,
        "Ctrl to expand", parts => parts + "u", System.Globalization.CultureInfo.InvariantCulture,
        foldable: i => i != 0);
    Require(table.Rows.Count == 2, "one kept component plus the hint, got " + table.Rows.Count);
    Require(table.Rows[0].Kind == WaterRowKind.Component && table.Rows[0].Index == 0, "kept component");
    Require(table.Rows[1].Kind == WaterRowKind.FoldHint && table.FoldedCount == 3, "hint covers the other three");
    var open = WaterTable.Build(Components(), 1_908_000, expanded: true,
        "Ctrl to expand", parts => parts + "u", System.Globalization.CultureInfo.InvariantCulture,
        foldable: i => i != 0);
    Require(open.Rows.Count == 4 && open.FoldedCount == 0, "Ctrl expands everything");
});
Check("phrase wrap breaks at sentence ends and commas, never inside a stat", () =>
{
    string text = "收获后仍会存活，并再次结果。 收获数量 -25%, 生长速度 -20%, 种子产量 -50%";
    string[] lines = TextWrap.WrapPhrases(text, 26).Split('\n');
    Require(lines.Length == 3, "three lines, got " + lines.Length);
    Require(lines[0] == "收获后仍会存活，并再次结果。", "first sentence alone, got " + lines[0]);
    Require(lines[1] == "收获数量 -25%, 生长速度 -20%,", "stats packed at commas, got " + lines[1]);
    Require(lines[2] == "种子产量 -50%", "last stat, got " + lines[2]);
    foreach (var line in lines) Require(line.Length <= 26, "line within limit: " + line);
    Require(TextWrap.WrapPhrases("短文字", 26) == "短文字", "short text untouched");
    string hard = TextWrap.WrapPhrases(new string('字', 60), 26);
    foreach (var line in hard.Split('\n')) Require(line.Length <= 26, "unbroken text still hard-wraps within the limit");
});
Check("description wrap avoids a stray last line by breaking at punctuation first", () =>
{
    void Same(string text, int limit, string expected)
    {
        string actual = TextWrap.Wrap(text, limit).Replace("\n", " | ");
        Require(actual == expected, "got " + actual);
    }
    Same("放入容器后即可产出水。需要能量。夜间运行。\n同时运行多台水分收集器时，每台的产量都会下降。", 22,
        "放入容器后即可产出水。需要能量。夜间运行。 | 同时运行多台水分收集器时， | 每台的产量都会下降。");
    Same("aaaa bbbb, cccc dddd e", 20, "aaaa bbbb, | cccc dddd e");
    // 没有标点时仍收窄重排；数字里的逗号不算标点
    Same(" 一种用于从空间站潮湿环境空气中提取水分的机器。", 22, " 一种用于从空间站潮湿环境空 | 气中提取水分的机器。");
    Same("一二三1,000四五六七八九十一二三四五六七八九十", 22, "一二三1,000四五六七八九十 | 一二三四五六七八九十");
    // 标点太靠前会留下过短的行首，不用它
    Same("一二，三四五六七八九十一二三四五六七八九十一二", 22, "一二，三四五六七八九十一二三 | 四五六七八九十一二");
});
Check("description wrap keeps a whole word even when it starts before the half-line floor", () =>
{
    void Same(string text, int limit, string expected)
    {
        string actual = TextWrap.Wrap(text, limit).Replace("\n", " | ");
        Require(actual == expected, "got " + actual);
    }
    Same(" Handheld customer-analysis unit. Scans posture, speech, and spending tells to generate a real-time negotiation script.", 26,
        " Handheld | customer-analysis unit. | Scans posture, speech, and | spending tells to generate | a real-time negotiation | script.");
    // 单词本身比一行还长才硬断；行首只有空白时退回去也没有意义
    Same("ab aaaaaaaaaaaaaaaa", 10, "ab aaaaaaa | aaaaaaaaa");
    Same(" aaaaaaaaaaaaaaaa", 10, " aaaaaaaaa | aaaaaaa");
});
Check("authenticity verdict follows the real condition before the issue tags", () =>
{
    Require(AuthenticityRules.Decide(null, 0) == AuthVerdict.Genuine, "no feature, no tags");
    Require(AuthenticityRules.Decide(null, 1) == AuthVerdict.Counterfeit, "no feature, tagged");
    Require(AuthenticityRules.Decide("genuineCigarette", 0) == AuthVerdict.Genuine, "real genuine");
    Require(AuthenticityRules.Decide("genuineStamp", 2) == AuthVerdict.Counterfeit, "the confront panel trusts tags");
    Require(AuthenticityRules.Decide("counterfeitCigarette", 0) == AuthVerdict.Counterfeit, "counterfeit without tags");
    Require(AuthenticityRules.Decide("expiredInjector", 1) == AuthVerdict.Expired, "expired is not counterfeit");
    Require(AuthenticityRules.Decide("unusableInjector", 1) == AuthVerdict.Unusable, "unusable is not counterfeit");
    Require(AuthenticityRules.Decide("somethingNew", 0, -30) == AuthVerdict.Other, "unknown condition that lowers value");
    Require(AuthenticityRules.Decide("authenticWatch", 0, 0) == AuthVerdict.Genuine, "an unfamiliar name alone is not a fault");
    Require(AuthenticityRules.Decide("authenticWatch", 1, 0) == AuthVerdict.Counterfeit, "unknown condition with an issue tag");
});
Check("authenticity sniffing relies on the game's naming conventions, not on any mention of issue", () =>
{
    Require(AuthenticityRules.IsAuthCategory("CATEGORY_GENUINE_WATCH"), "third-party category");
    Require(!AuthenticityRules.IsAuthCategory("CATEGORY_GENUINE_"), "prefix alone");
    Require(!AuthenticityRules.IsAuthCategory("CATEGORY_REPLICA"), "other categories");
    Require(!AuthenticityRules.IsAuthCategory(null), "null category");
    Require(AuthenticityRules.IsIssueTag("ISSUE_SERIAL") && AuthenticityRules.IsIssueTag("WATCH_DIAL_ISSUE"), "both shapes");
    Require(!AuthenticityRules.IsIssueTag("TISSUE_PAPER") && !AuthenticityRules.IsIssueTag("TISSUE"), "words containing issue");
    Require(!AuthenticityRules.IsIssueTag("MAGAZINE_ISSUE_NUMBER"), "issue in the middle");
    Require(AuthenticityRules.IssueStem("CIGARETTE_WAX_SEAL_ISSUE", "CATEGORY_GENUINE_CIGARETTE") == "WAX_SEAL", "subject prefix removed");
    Require(AuthenticityRules.IssueStem("ISSUE_SERIAL", "CATEGORY_GENUINE_INJECTOR") == "SERIAL", "leading form");
    Require(AuthenticityRules.IssueStem("WATCH_ISSUE", "CATEGORY_GENUINE_WATCH") == "WATCH", "the stem is never emptied");
    Require(AuthenticityRules.Humanize("WAX_SEAL") == "Wax Seal", "readable fallback");
});
Check("tooltip line breaks in zh and ja fill the line instead of stopping at the first space", () =>
{
    string[] Wrap(int budget, params string[] parts)
    {
        var slices = new List<BreakSlice>();
        foreach (string part in parts) slices.Add(new BreakSlice(part, false, false));
        var lines = new List<string>();
        while (true)
        {
            int total = 0;
            foreach (var s in slices)
                total += System.Text.RegularExpressions.Regex.Replace(s.Text, "<[^>]*>", "").Length;
            if (total <= budget || !TooltipLineBreak.Find(slices, budget, out var p))
            {
                lines.Add(string.Concat(slices.ConvertAll(s => s.Text)));
                return lines.ToArray();
            }
            string line = "";
            for (int i = 0; i < p.Slice; i++) line += slices[i].Text;
            string text = slices[p.Slice].Text;
            lines.Add(line + text.Substring(0, p.Index));
            string rest = text.Substring(p.IsSpace ? p.Index + 1 : p.Index);
            slices.RemoveRange(0, p.Slice + 1);
            slices.Insert(0, new BreakSlice(rest, false, false));
        }
    }
    void Same(string[] actual, params string[] expected)
        => Require(string.Join("|", actual) == string.Join("|", expected), "got " + string.Join("|", actual));

    Same(Wrap(26, "[标签] ", "受营养果种子短缺影响，食物的市场价格发生了变动 (+33%)"),
        "[标签] 受营养果种子短缺影响，", "食物的市场价格发生了变动 (+33%)");
    Same(Wrap(26, "× 赝品（字母印刷问题、颜色问题、防伪标问题、商标问题、包装盒问题）"),
        "× 赝品（字母印刷问题、颜色问题、防伪标问题、", "商标问题、包装盒问题）");
    Same(Wrap(26, "一二三四五六七八九十一二三四五六七八九十一二三四五，六七八（九十）一二三。"),
        "一二三四五六七八九十一二三四五六七八九十一二三四五，", "六七八（九十）一二三。");
    Same(Wrap(26, "<size=88%>一种美味零食按法律分类属于人工香料调味冷冻乳制品不知为何即使不冷藏也不会融化</size>"),
        "<size=88%>一种美味零食按法律分类属于人工香料调味冷冻乳制品不知", "为何即使不冷藏也不会融化</size>");
    Same(Wrap(12, "价格上涨 1,000,000,000 元"), "价格上涨", "1,000,000,000 元");
    Require(!TooltipLineBreak.Find(new[] { new BreakSlice(new string('a', 40), false, false) }, 26, out _),
        "an unbreakable run goes back to the game's own logic");
    Require(!TooltipLineBreak.Find(new[] { new BreakSlice("[==== ====== ==== ======== ========]", false, true) }, 26, out _),
        "never breaks inside a no-break slice");
    var bar = new[] { new BreakSlice("甲乙丙丁戊己庚辛壬癸", false, false), new BreakSlice("[=====]", false, true) };
    Require(TooltipLineBreak.Find(bar, 12, out var before) && before.Slice == 1 && before.Index == 0 && !before.IsSpace,
        "a no-break slice moves to the next line whole");
});
Check("no hint at all when nothing is folded", () =>
{
    var table = WaterTable.Build(BigComponents(), 1_908_000, expanded: false,
        "Ctrl to expand", parts => parts + "u", System.Globalization.CultureInfo.InvariantCulture);
    Require(table.Rows.Count == 2, "only the two visible components");
    foreach (var row in table.Rows) Require(row.Kind == WaterRowKind.Component, "no hint row");
});
Check("solid components reuse the same rows with their own amount format", () =>
{
    var table = WaterTable.Build(Components(), 1_908_000, expanded: false,
        "Ctrl to expand", p => (p / 1000.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " mL",
        System.Globalization.CultureInfo.InvariantCulture);
    Require(table.Rows[0].Amount == "1500.00 mL", "solid amount comes from the injected formatter, got " + table.Rows[0].Amount);
    Require(table.Rows[0].Name == "水" && table.Rows[1].Name == "化学污染物", "names pass through untouched");
});
Check("a near-full bar matches either fill convention", () =>
{
    // 原生 AddProgressBar 截断：0.9996 × 10 只画 9 格；本模组自己的条把接近满的补成 10 格。
    Require(ProgressFill.Native(0.9996f, 10) == 9, "native fill truncates");
    Require(ProgressFill.Filled(0.9996f, 10) == 10, "our bars snap a near-full ratio to full");
    Require(ProgressFill.Accepts(0.9996f, 10, 9), "truncated text still finds its record");
    Require(ProgressFill.Accepts(0.9996f, 10, 10), "snapped text finds it too");
    Require(!ProgressFill.Accepts(0.5f, 10, 9), "a different ratio must not borrow the record");
    Require(ProgressFill.Accepts(0.5f, 10, 5), "a half-filled bar matches");
});
Check("flowing rows always keep three cells, hint included", () =>
{
    var table = WaterTable.Build(Components(), 1_908_000, expanded: false,
        "Ctrl to expand", parts => parts + "u", System.Globalization.CultureInfo.InvariantCulture);
    string[] component = WaterTable.JoinCells(table.Rows[0], '　').Split('　');
    Require(component.Length == 3 && component[1] == "1500000u", "component keeps name, amount, percent");
    string[] hint = WaterTable.JoinCells(table.Rows[table.Rows.Count - 1], '　').Split('　');
    Require(hint.Length == 3, "hint row must not collapse to two cells, got " + hint.Length);
    Require(hint[0] == "Ctrl to expand", "hint text stays in the first cell");
    Require(hint[1] == WaterTable.EmptyCell, "empty amount cell holds the placeholder");
    Require(hint[2] == "0.42%", "percent stays in the last cell");
});

// ============ 价值公式、鉴定区间、赝品原因、堆叠、折行、体积、成分表数值、复数（每条用例一行 PASS）============
// 期望值来自 Probably Stolen 0.46D 反编译的 AppHandHelper / PlasmaHelper / RibwichHelper.GetCorrectValue。
foreach (var c in new (string Name, string Wear, string Crack, string Uncov, string Reinf, string Color, string Seal, int Exp)[]
{
    ("默认 LL+标准色", "NONE", "NONE", "NONE", "NONE", "STANDARD", "LL", 360),                // 300 + 60
    ("重度磨损 无封印", "WEAR_HEAVY", "NONE", "NONE", "NONE", "STANDARD", "NONE", 210),        // 300 - 90，与实机截图一致
    ("轻度磨损", "WEAR", "NONE", "NONE", "NONE", "STANDARD", "NONE", 255),                    // 300 - 45
    ("点火实验室+高级颜色", "NONE", "NONE", "NONE", "NONE", "HIGH_QUALITY", "IGNITION_LAB", 1200), // 300 + 150 + 750
    ("磨损+外露+加固", "WEAR", "NONE", "UNCOVERED", "REINFORCED", "STANDARD", "NONE", 285),   // 300 - 45 - 30 + 60
    ("中华船舶", "NONE", "NONE", "NONE", "NONE", "STANDARD", "ZHONGHUA", 420),                // 300 + 120
    ("共和国海军", "NONE", "NONE", "NONE", "NONE", "STANDARD", "REPUBLIC", 420),              // 300 + 120
    ("LL 配高级颜色 无封印加成", "NONE", "NONE", "NONE", "NONE", "HIGH_QUALITY", "LL", 450),   // 只剩颜色 +150
    ("破裂 共和国不成", "NONE", "CRACK", "NONE", "NONE", "STANDARD", "REPUBLIC", 150),         // 300 - 150
})
    Check("plasma/" + c.Name, () =>
        ExpectTotal(ValueBreakdown.ComputePlasma(PlasmaTags(c.Wear, c.Crack, c.Uncov, c.Reinf, c.Color, c.Seal)), c.Exp));

foreach (var c in new (string Name, string Mat, string Iface, string Cond, string Armored, string Bladed, int Exp)[]
{
    ("白板", "ALUMINUM_ALLOY", "EMG", "NONE", "NONE", "NONE", 100),
    ("战术组合", "CARBON_COMPOSITE", "EMG_PLUS", "NONE", "ARMORED", "BLADED", 425),         // 100 + 50 + 45 + 50 + 80 + 100
    ("豪华组合", "GOLD_PLATED", "NEURAL", "NONE", "NONE", "NONE", 620),                      // 100 + 200 + 120 + 200
    ("规格不明接口 触底", "ALUMINUM_ALLOY", "UNSPECIFIED", "NONE", "NONE", "NONE", 50),      // 100 - 60 → 下限 50
    ("受损 触底", "ALUMINUM_ALLOY", "EMG", "DAMAGE", "NONE", "NONE", 50),                    // 100 - 85 → 下限 50
    ("镀金+刀刃", "GOLD_PLATED", "EMG", "NONE", "NONE", "BLADED", 380),                      // 100 + 200 + 80
})
    Check("hand/" + c.Name, () =>
        ExpectTotal(ValueBreakdown.ComputeHand(HandTags(c.Mat, c.Iface, c.Cond, c.Armored, c.Bladed)), c.Exp));

foreach (var c in new (string Name, int Wrap, int Meat, int Bun, int Pickles, int Month, int Year, int Fac, int Native, int Exp)[]
{
    ("已开封 wrap=1", 1, 0, 0, 0, 5, 430, 3, 40, 40),
    ("普通", 0, 0, 0, 0, 5, 430, 3, 0, 150),
    ("双层肉饼", 0, 1, 0, 0, 5, 430, 3, 0, 950),
    ("豪华版", 0, 0, 1, 1, 5, 430, 3, 0, 450),
    ("首月首批次", 0, 0, 0, 0, 1, 425, 3, 0, 280),
    ("优质包装厂", 0, 0, 0, 0, 5, 430, 1, 0, 200),
    ("劣质包装厂", 0, 0, 0, 0, 5, 430, 7, 0, 100),
    ("赝品(native=40)", 0, 0, 0, 0, 5, 430, 3, 40, 40),
})
    Check("ribwich/" + c.Name, () =>
    {
        var tags = new Dictionary<string, int>
        {
            ["RIBWICH_WRAP_TAG"] = c.Wrap, ["RIBWICH_MEAT_TAG"] = c.Meat,
            ["RIBWICH_BUN_TAG"] = c.Bun, ["RIBWICH_PICKLES_TAG"] = c.Pickles,
            ["RIBWICH_PRODUCTION_MONTH_TAG"] = c.Month, ["RIBWICH_PRODUCTION_YEAR_TAG"] = c.Year,
            ["RIBWICH_FACILITY_NUMBER_TAG"] = c.Fac,
        };
        ExpectTotal(ValueBreakdown.ComputeRibwich(key => tags.TryGetValue(key, out var v) ? v : (int?)null, c.Native), c.Exp);
    });

// 标签缺失或非法值必须判无效
Check("plasma/缺标签应无效", () => Require(!ValueBreakdown.ComputePlasma(key => null).Valid, "should be invalid"));
Check("plasma/非法颜色应无效", () => Require(!ValueBreakdown.ComputePlasma(key =>
    key == "PLASMA_COLOR" ? "WEIRD_COLOR"
    : key is "PLASMA_CASING_CONDITION_WEAR" or "PLASMA_CASING_CONDITION_CRACK"
        or "PLASMA_CASING_CONDITION_UNCOVERED" or "PLASMA_CASING_CONDITION_REINFORCED" ? "NONE"
    : key == "PLASMA_SEAL" ? "NONE" : null).Valid, "should be invalid"));
Check("hand/缺标签应无效", () => Require(!ValueBreakdown.ComputeHand(key => null).Valid, "should be invalid"));
Check("ribwich/缺包装标签应无效", () => Require(!ValueBreakdown.ComputeRibwich(key => null, 0).Valid, "should be invalid"));

// 鉴定区间：完美档 95%–105%，与 RNG.CalculatePercentage 同为截断
CheckRange("range/210", 210, 199, 220);   // 210*95/100=199，210*105/100=220
CheckRange("range/300", 300, 285, 315);
CheckRange("range/100", 100, 95, 105);
CheckRange("range/7", 7, 6, 7);           // 小数值截断：7*95/100=6，7*105/100=7

// 边界一致性：对每个真实价暴力遍历候选值，断言「档位==1」当且仅当「落在显示区间内」
int[] corrects = { 1, 3, 7, 40, 90, 100, 150, 180, 199, 210, 275, 300, 360, 425, 620, 1200 };
foreach (int correct in corrects)
    Check("range/scan-" + correct, () =>
    {
        var (lo, hi) = AppraisalRanges.Perfect(correct);
        Require(lo <= correct && correct <= hi, $"{lo}–{hi} 不含真实价");
        Require(AppraisalRanges.Grade(correct, lo) == 1 && AppraisalRanges.Grade(correct, hi) == 1,
            $"端点 {lo}/{hi} 未判为完美");
        Require(lo <= 0 || AppraisalRanges.Grade(correct, lo - 1) != 1, $"{lo - 1} 也判完美，区间少了一格");
        Require(AppraisalRanges.Grade(correct, hi + 1) != 1, $"{hi + 1} 也判完美，区间少了一格");
        for (int v = 0; v <= hi + 2; v++)
            Require((v >= lo && v <= hi) == (AppraisalRanges.Grade(correct, v) == 1),
                $"value={v} 的档位与显示区间 {lo}–{hi} 不一致");
    });
Check("range/scan-total", () =>
{
    int scanned = 0, mismatch = 0;
    foreach (int correct in corrects)
    {
        var (lo, hi) = AppraisalRanges.Perfect(correct);
        for (int v = 0; v <= hi + 2; v++)
        {
            scanned++;
            if ((v >= lo && v <= hi) != (AppraisalRanges.Grade(correct, v) == 1)) mismatch++;
        }
    }
    Require(scanned > 0 && mismatch == 0, $"{mismatch}/{scanned} 处不一致");
});

// 赝品肋明治原因（与 RibwichHelper.IsCounterfeit 逐条对应）
// 参数：月, 年, 包装厂, 达夫雷, 批次贴纸, 生产日期标记, 包装厂标记, 面包, 泡菜
CheckReasons("ribwich/valid-second-run", 5, 432, 3, 0, 0, 0, 0, 0, 0);
CheckReasons("ribwich/valid-first-run", 5, 427, 3, 0, -1, 0, 0, 0, 0);
CheckReasons("ribwich/first-run-has-sticker", 5, 427, 3, 0, 0, 0, 0, 0, 0, RibwichReasons.Reason.FirstRunHasSticker);
CheckReasons("ribwich/second-run-missing-sticker", 5, 432, 3, 0, -1, 0, 0, 0, 0, RibwichReasons.Reason.SecondRunStickerMissing);
CheckReasons("ribwich/second-run-wrong-sticker", 5, 432, 3, 0, 1, 0, 0, 0, 0, RibwichReasons.Reason.SecondRunStickerWrong);
CheckReasons("ribwich/month-out-of-range", 13, 432, 3, 0, 0, 0, 0, 0, 0, RibwichReasons.Reason.MonthOutOfRange);
CheckReasons("ribwich/year-out-of-range", 5, 430, 3, 0, 0, 0, 0, 0, 0, RibwichReasons.Reason.YearOutOfRange);
CheckReasons("ribwich/production-date-missing", 5, 432, 3, 0, 0, -1, 0, 0, 0, RibwichReasons.Reason.ProductionDateMissing);
CheckReasons("ribwich/facility-zero", 5, 432, 0, 0, 0, 0, 0, 0, 0, RibwichReasons.Reason.FacilityOutOfRange);
CheckReasons("ribwich/facility-36", 5, 432, 36, 0, 0, 0, 0, 0, 0, RibwichReasons.Reason.FacilityOutOfRange);
CheckReasons("ribwich/facility-mark-missing", 5, 432, 3, 0, 0, 0, -1, 0, 0, RibwichReasons.Reason.FacilityMissing);
CheckReasons("ribwich/davre-missing", 5, 432, 3, -1, 0, 0, 0, 0, 0, RibwichReasons.Reason.DavreMissing);
CheckReasons("ribwich/davre-wrong", 5, 432, 3, 2, 0, 0, 0, 0, 0, RibwichReasons.Reason.DavreWrong);
CheckReasons("ribwich/bun-without-pickles", 5, 432, 3, 0, 0, 0, 0, 1, 0, RibwichReasons.Reason.BunWithoutPickles);
CheckReasons("ribwich/pickles-without-bun", 5, 432, 3, 0, 0, 0, 0, 0, 1, RibwichReasons.Reason.PicklesWithoutBun);
CheckReasons("ribwich/multiple-faults", 13, 430, 40, -1, 0, -1, -1, 1, 0,
    RibwichReasons.Reason.MonthOutOfRange, RibwichReasons.Reason.YearOutOfRange,
    RibwichReasons.Reason.ProductionDateMissing, RibwichReasons.Reason.FacilityOutOfRange,
    RibwichReasons.Reason.FacilityMissing, RibwichReasons.Reason.DavreMissing,
    RibwichReasons.Reason.BunWithoutPickles);

// 堆叠价值（单件当前价值 × 数量）
CheckStack("stack/single", 100, 1, true, 100);
CheckStack("stack/five", 100, 5, true, 500);
CheckStack("stack/count-zero-treated-as-one", 100, 0, true, 100);
CheckStack("stack/unit-zero", 0, 5, false, 0);
CheckStack("stack/overflow", long.MaxValue, 2, false, 0);
Check("stack/per-cell：500/4=125", () => Require(Math.Abs(StackValue.PerCell(500, 4) - 125.0) < 0.0001, "per cell " + StackValue.PerCell(500, 4)));

// 描述预折行（TextWrap.Wrap）
CheckWrap("wrap/zh-perfume", "使用后，与当前顾客的交易收益将提升15%。你同时只能获得一种香水效果的增益。", 26,
    "使用后，与当前顾客的交易收益将提升15%。\n你同时只能获得一种香水效果的增益。");
CheckWrap("wrap/zh-furnace", "将2块废金属或金属矿石提炼成金属锭。需要能量。夜间运行。", 24,
    "将2块废金属或金属矿石提炼成金属锭。需要能量。\n夜间运行。");
CheckWrap("wrap/short", "短描述", 26, "短描述");
CheckWrap("wrap/keep-newline", " 第一行\n第二行", 26, " 第一行\n第二行");
CheckWrap("wrap/en-word", "alpha beta gamma delta", 12, "alpha beta\ngamma delta");
CheckWrap("wrap/kinsoku", "一二三四五。六七八", 5, "一二三四\n五。六七八");   // 收尾标点不放行首
CheckWrap("wrap/number", "收益提升百分之15%", 8, "收益提升百分之\n15%");       // 数字和百分号不拆
Check("wrap/limit", () => Require(
    Array.TrueForAll(TextWrap.Wrap(new string('字', 100), 26).Split('\n'), l => l.Length <= 26), "100 字按 26 断开，每行 ≤ 26"));

// 体积单位与进度条格数（3000 ml 换 L 的阈值见上方 Volume.UseLiters 用例）
CheckText("volume/ml", Volume.Number(2500, false), "2500");
CheckText("volume/liter", Volume.Number(12345, true), "12.35");
CheckText("volume/liter-whole", Volume.Number(10000, true), "10");
CheckFill("fill/near-full", 2999600f / 3000000f, 10, 10);   // 水罐差 0.4 ml：原版 floor 只画 9 格，这里按满画
CheckFill("fill/full", 1f, 10, 10);
CheckFill("fill/floor", 0.95f, 10, 9);
CheckFill("fill/below-threshold", 0.999f, 10, 9);
CheckFill("fill/nan", float.NaN, 10, 0);

// 水质成分表的数值与容器容量
var inv = System.Globalization.CultureInfo.InvariantCulture;
const int WaterTotalParts = 20_000_000;   // 20000 mL × 1000 parts/mL
CheckText("成分百分比 98.07", WaterLayout.PercentText(19_614_000, WaterTotalParts, inv), "98.07");
CheckText("成分百分比 0.58", WaterLayout.PercentText(116_000, WaterTotalParts, inv), "0.58");
CheckText("成分百分比 100", WaterLayout.PercentText(WaterTotalParts, WaterTotalParts, inv), "100.00");
CheckText("体积 mL", WaterLayout.VolumeText(1_961_400, false, inv), "1961.4");
CheckText("体积 L", WaterLayout.VolumeText(19_720_100, true, inv), "19.72");
CheckText("容器容量 mL", WaterLayout.Capacity(20000, 20000, false, inv), "20000/20000mL");
CheckText("容器容量 L", WaterLayout.Capacity(20000, 20000, true, inv), "20/20L");
// 1% 折叠阈值：整数比较，0.999% 仍算低于阈值
CheckFold("正好 1% 不折", 1000, 100000, false);
CheckFold("0.99% 折", 990, 100000, true);
CheckFold("0.999% 折", 999, 100000, true);
CheckFold("1.01% 不折", 1010, 100000, false);
CheckFold("空总量不折", 0, 0, false);

// 复数形式
const string ru = "{0} {0|модуль|модуля|модулей}";   // one/few/many：11–14 归 many，21 回到 one
CheckPlural("plural/ru-1", ru, 1, "ru", "1 модуль");
CheckPlural("plural/ru-2", ru, 2, "ru", "2 модуля");
CheckPlural("plural/ru-5", ru, 5, "ru", "5 модулей");
CheckPlural("plural/ru-11", ru, 11, "ru", "11 модулей");
CheckPlural("plural/ru-21", ru, 21, "ru", "21 модуль");
CheckPlural("plural/ru-22", ru, 22, "ru", "22 модуля");
const string pl = "{0} {0|moduł|moduły|modułów}";    // 波兰语只有 1 是 one，22 是 few，12 是 many
CheckPlural("plural/pl-1", pl, 1, "pl", "1 moduł");
CheckPlural("plural/pl-2", pl, 2, "pl", "2 moduły");
CheckPlural("plural/pl-5", pl, 5, "pl", "5 modułów");
CheckPlural("plural/pl-12", pl, 12, "pl", "12 modułów");
CheckPlural("plural/pl-21", pl, 21, "pl", "21 modułów");
CheckPlural("plural/pl-22", pl, 22, "pl", "22 moduły");
const string fr = "{0} {0|jour|jours}";               // 法语、葡语 0 和 1 都是单数；英语 0 是复数
CheckPlural("plural/fr-0", fr, 0, "fr", "0 jour");
CheckPlural("plural/fr-1", fr, 1, "fr", "1 jour");
CheckPlural("plural/fr-2", fr, 2, "fr", "2 jours");
CheckPlural("plural/en-0", "{0} {0|day|days}", 0, "en", "0 days");
CheckPlural("plural/en-1", "{0} {0|day|days}", 1, "en", "1 day");
// 词形不足时取最后一个；参数不是整数或越界时也取最后一个
CheckPlural("plural/short-forms", "{0} {0|Tag|Tage}", 5, "ru", "5 Tage");
CheckPlural("plural/fraction", "{0} {0|day|days}", 1.5, "en", "1.5 days");
CheckText("plural/missing-arg", Plural.Apply("{3|day|days}", new object[] { 1 }, "en"), "days");
CheckText("plural/no-token", Plural.Apply("{0}/{1} ml", new object[] { 1, 2 }, "en"), "{0}/{1} ml");

return failures == 0 ? 0 : 1;

void CheckText(string name, string actual, string expected)
    => Check(name, () => Require(actual == expected, $"期望 {expected}，实际 {actual}"));

void CheckFill(string name, float ratio, int length, int expected)
    => Check(name, () =>
    {
        int actual = ProgressFill.Filled(ratio, length);
        Require(actual == expected, $"比例 {ratio} → {actual} 格，期望 {expected}");
    });

void CheckWrap(string name, string text, int limit, string expected)
    => Check(name, () =>
    {
        string actual = TextWrap.Wrap(text, limit);
        Require(actual == expected, $"期望 {expected.Replace("\n", "⏎")}，实际 {actual.Replace("\n", "⏎")}");
    });

void CheckFold(string name, int parts, int total, bool expected)
    => Check(name, () =>
    {
        bool actual = WaterLayout.BelowOnePercent(parts, total);
        Require(actual == expected, $"期望 {expected}，实际 {actual}");
    });

void CheckPlural(string name, string template, object count, string lang, string expected)
{
    var args = new[] { count };
    string actual = string.Format(System.Globalization.CultureInfo.InvariantCulture, Plural.Apply(template, args, lang), args);
    CheckText(name, actual, expected);
}

void CheckStack(string name, long unit, int count, bool expectOk, long expectedTotal)
    => Check(name, () =>
    {
        bool ok = StackValue.TryTotal(unit, count, out long total);
        Require(ok == expectOk && total == expectedTotal,
            $"ok={ok} total={total}，期望 ok={expectOk} total={expectedTotal}");
    });

void CheckRange(string name, int correct, int expLow, int expHigh)
    => Check(name, () =>
    {
        var (lo, hi) = AppraisalRanges.Perfect(correct);
        Require(lo == expLow && hi == expHigh, $"完美 {lo}–{hi}，期望 {expLow}–{expHigh}");
    });

// 原因用本地化键比较：失败时仍能看出是哪一条，也不会因为改译文而牵动测试
void CheckReasons(string name, int month, int year, int facility, int davre, int logo,
    int productionMark, int facilityMark, int bun, int pickles, params RibwichReasons.Reason[] expected)
    => Check(name, () =>
    {
        var actual = new List<string>();
        foreach (var r in RibwichReasons.Evaluate(month, year, facility, davre, logo, productionMark, facilityMark, bun, pickles))
            actual.Add(RibwichReasons.Key(r));
        var want = new List<string>();
        foreach (var r in expected) want.Add(RibwichReasons.Key(r));
        actual.Sort(StringComparer.Ordinal);
        want.Sort(StringComparer.Ordinal);
        Require(actual.Count == want.Count && actual.SequenceEqual(want),
            $"实际 [{string.Join(" / ", actual)}]，期望 [{string.Join(" / ", want)}]");
    });

static void ExpectTotal(ValueBreakdown.Result result, int expected)
{
    if (!result.Valid) throw new Exception($"判定为无效（标签不合法），期望 {expected}");
    if (result.Total != expected) throw new Exception($"{result.Total} != {expected}");
}

static Func<string, string?> PlasmaTags(string wear, string crack, string uncov, string reinf, string color, string seal)
{
    var tags = new Dictionary<string, string>
    {
        ["PLASMA_CASING_CONDITION_WEAR"] = wear, ["PLASMA_CASING_CONDITION_CRACK"] = crack,
        ["PLASMA_CASING_CONDITION_UNCOVERED"] = uncov, ["PLASMA_CASING_CONDITION_REINFORCED"] = reinf,
        ["PLASMA_COLOR"] = color, ["PLASMA_SEAL"] = seal,
    };
    return key => tags.GetValueOrDefault(key);
}

static Func<string, string?> HandTags(string mat, string iface, string cond, string armored, string bladed)
{
    var tags = new Dictionary<string, string>
    {
        ["HAND_MATERIAL"] = mat, ["HAND_INTERFACE"] = iface, ["HAND_CONDITION"] = cond,
        ["HAND_ADDON_ARMORED"] = armored, ["HAND_ADDON_BLADED"] = bladed,
    };
    return key => tags.GetValueOrDefault(key);
}

static IReadOnlyList<(string Name, int Parts)> Components() => new List<(string Name, int Parts)>
{
    ("水", 1_500_000),
    ("化学污染物", 400_000),
    ("有机废物", 5_000),
    ("微生物", 3_000),
};

static IReadOnlyList<(string Name, int Parts)> BigComponents() => new List<(string Name, int Parts)>
{
    ("水", 1_500_000),
    ("化学污染物", 408_000),
};

void Check(string name, Action run)
{
    try { run(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void Reconciles(ValueBreakdown.Result result, int nativeValue)
{
    if (!result.Valid || result.Total != nativeValue) throw new Exception($"expected native total {nativeValue}, got {result.Total}");
    int subtotal = 0;
    foreach (var part in result.Parts) subtotal += part.Amount;
    if (subtotal != result.Total) throw new Exception($"displayed contributions sum to {subtotal}, total is {result.Total}");
}

static Func<string, string?> Plasma(string crack, string finish)
{
    var tags = new Dictionary<string, string>
    {
        ["PLASMA_CASING_CONDITION_WEAR"] = "NONE", ["PLASMA_CASING_CONDITION_CRACK"] = crack,
        ["PLASMA_CASING_CONDITION_UNCOVERED"] = "NONE", ["PLASMA_CASING_CONDITION_REINFORCED"] = "NONE",
        ["PLASMA_COLOR"] = finish, ["PLASMA_SEAL"] = "NONE",
    };
    return key => tags.GetValueOrDefault(key);
}
