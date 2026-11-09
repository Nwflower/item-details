using System;
using System.Collections.Generic;
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
return failures == 0 ? 0 : 1;

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
