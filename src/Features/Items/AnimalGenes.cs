using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Palette = RenderHandler.ColorPalette;

namespace ItemDetails;

/// <summary>基因扫描时替换原版动物提示中的六条性状行，保留其余养殖信息。</summary>
internal static class AnimalGenes
{
    private const string AlleleSeparator = " / ";
    private static readonly Func<string, float, float, Palette>? StatColor = BindStatColor();
    // 旧版模组扫描器会把 OpCode 的比较运算符误判为动态代码。
    private static readonly EqualityComparer<OpCode> OpcodeComparer = EqualityComparer<OpCode>.Default;

    // 原版初始等位基因是两次 U(0.5, 1.5) 的均值。按对称三角分布取第 90、70、40 百分位。
    private static readonly double GradeS = 0.5 - Math.Sqrt(0.10 / 2);
    private static readonly double GradeA = 0.5 - Math.Sqrt(0.30 / 2);
    private static readonly double GradeB = Math.Sqrt(0.40 / 2) - 0.5;

    // 第 0 项繁殖力的即时状态由原版另外显示，这里额外列出它的遗传值，画在表格最后一行；
    // 其余六项是原版的顺序，转译器按它们匹配原版指令。
    private static readonly (string Key, string Id, string Fallback)[] Traits =
    {
        ("husbandry_tooltip_fertility", HusbandryHelper.ANIMAL_FERTILITY_TAG, "Fertility"),
        ("husbandry_stat_growth_rate", HusbandryHelper.ANIMAL_GROWTH_RATE_TAG, "Growth rate"),
        ("husbandry_stat_litter_size", HusbandryHelper.ANIMAL_LITTER_SIZE_TAG, "Litter size"),
        ("husbandry_stat_immunity", HusbandryHelper.ANIMAL_IMMUNITY_TAG, "Immunity"),
        ("husbandry_stat_max_health", HusbandryHelper.ANIMAL_MAX_HEALTH_TAG, "Max health"),
        ("husbandry_stat_longevity", HusbandryHelper.ANIMAL_LONGEVITY_TAG, "Longevity"),
        ("husbandry_stat_hunger_rate", HusbandryHelper.ANIMAL_HUNGER_RATE_TAG, "Hunger rate"),
    };

    internal static void Install(PatchScope scope)
    {
        scope.Patch(AccessTools.Method(typeof(HusbandryHelper), nameof(HusbandryHelper.CreateItemTooltip),
                new[] { typeof(RichTextBuilder), typeof(GameItem) }),
            transpiler: new HarmonyMethod(typeof(AnimalGenes), nameof(ReplaceStatBlock)) { priority = Priority.Last });
    }

    private static Func<string, float, float, Palette>? BindStatColor()
    {
        var method = AccessTools.Method(typeof(HusbandryHelper), "GetStatCompareColor",
            new[] { typeof(string), typeof(float), typeof(float) });
        return method == null ? null
            : (Func<string, float, float, Palette>?)Delegate.CreateDelegate(
                typeof(Func<string, float, float, Palette>), method, throwOnBindFailure: false);
    }

    private static IEnumerable<CodeInstruction> ReplaceStatBlock(IEnumerable<CodeInstruction> instructions)
    {
        var body = new List<CodeInstruction>(instructions);
        var guard = AccessTools.Method(typeof(AnimalGenes), nameof(ShowVanillaStats));
        const int instructionsPerStat = 4;
        int length = (Traits.Length - 1) * instructionsPerStat;
        for (int start = 2; start + length < body.Count; start++)
        {
            if (!OpcodeComparer.Equals(body[start].opcode, OpCodes.Ldstr) || !Equals(body[start].operand, Traits[1].Key)) continue;
            var loadSpecies = body[start - 2];
            var skip = body[start - 1];
            if (!OpcodeComparer.Equals(loadSpecies.opcode, OpCodes.Ldfld) || loadSpecies.operand is not FieldInfo field
                || field.FieldType != typeof(AnimalSpecies) || skip.blocks.Count != 0
                || (!OpcodeComparer.Equals(skip.opcode, OpCodes.Brfalse) && !OpcodeComparer.Equals(skip.opcode, OpCodes.Brfalse_S)))
                continue;
            if (!MatchesStats(body, start, instructionsPerStat)) continue;

            // 原版以 species 作为 brfalse 的条件。复用这个分支，保留原版其余控制流。
            body.InsertRange(start - 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(skip),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, guard),
            });
            return body;
        }
        Log.Warn("AnimalGenes.Apply", "找不到完整的原版动物性状行，沿用原版显示");
        return body;
    }

    private static bool MatchesStats(List<CodeInstruction> body, int start, int stride)
    {
        for (int i = 1; i < Traits.Length; i++)
        {
            int pos = start + (i - 1) * stride;
            var closure = body[pos + 2];
            var call = body[pos + 3];
            if (!OpcodeComparer.Equals(body[pos].opcode, OpCodes.Ldstr) || !Equals(body[pos].operand, Traits[i].Key)
                || !OpcodeComparer.Equals(body[pos + 1].opcode, OpCodes.Ldstr) || !Equals(body[pos + 1].operand, Traits[i].Id)
                || (!OpcodeComparer.Equals(closure.opcode, OpCodes.Ldloca) && !OpcodeComparer.Equals(closure.opcode, OpCodes.Ldloca_S))
                || !OpcodeComparer.Equals(call.opcode, OpCodes.Call) || call.operand is not MethodInfo method
                || method.DeclaringType != typeof(HusbandryHelper)
                || !method.Name.StartsWith("<CreateItemTooltip>g__AddStatLine|", StringComparison.Ordinal))
                return false;

            // 有其他补丁把异常边界插进原版性状块时，不跨过那些指令。
            for (int offset = 0; offset < stride; offset++)
                if (body[pos + offset].blocks.Count != 0) return false;
        }
        return body[start + (Traits.Length - 1) * stride].blocks.Count == 0;
    }

    private static bool ShowVanillaStats(AnimalSpecies? species, RichTextBuilder builder, GameItem item)
        => species != null && !TryWrite(builder, item, species);

    private static bool TryWrite(RichTextBuilder builder, GameItem item, AnimalSpecies species)
    {
        if (StatColor == null || !ModSettings.TooltipFormatting
            || !PlayerStore.HasInstance || !PlayerStore.Instance.isGeneScannerScanning)
            return false;

        try
        {
            if (species.geneticTraitIDs == null || species.geneticBaseValues == null)
                return false;

            var rows = new (string Label, string Value,
                (string Text, Palette Color, char Grade) A, (string Text, Palette Color, char Grade) B)[Traits.Length];
            for (int i = 0; i < Traits.Length; i++)
            {
                var (key, id, fallback) = Traits[i];
                if (!species.geneticTraitIDs.Contains(id)
                    || !species.geneticBaseValues.TryGetValue(id, out float baseline) || !float.IsFinite(baseline) || baseline <= 0
                    || !TryAllele(item, id + GeneticHelper.ALLELE_A_SUFFIX, out float a)
                    || !TryAllele(item, id + GeneticHelper.ALLELE_B_SUFFIX, out float b))
                    return false;

                float expression = GeneticHelper.GetExpressedValue(item, id);
                if (!float.IsFinite(expression)) return false;
                bool higher = !HusbandryHelper.StatHigherIsBetter.TryGetValue(id, out bool direction) || direction;
                // 繁殖力置底：其余六项保持原版顺序在前
                rows[i == 0 ? Traits.Length - 1 : i - 1] = (TraitLabel(key, fallback), Number(expression) + (i == 0 ? "%" : ""),
                    (Number(a), StatColor(id, a, baseline), Grade(a, baseline, higher)),
                    (Number(b), StatColor(id, b, baseline), Grade(b, baseline, higher)));
            }

            Write(builder, item, rows);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("AnimalGenes", ex);
            return false;
        }
    }

    private static void Write(RichTextBuilder builder, GameItem item,
        (string Label, string Value,
            (string Text, Palette Color, char Grade) A, (string Text, Palette Color, char Grade) B)[] rows)
    {
        string label = Loc.T("animal.genes_trait", "Trait");
        string value = Loc.T("animal.genes_expression", "Expression");
        string alleles = Loc.T("animal.genes_alleles", "Alleles");
        string grade = Loc.T("animal.genes_grade", "Grade");
        float labelWidth = WaterColumns.TextEm(label);
        float valueWidth = WaterColumns.TextEm(value);
        float alleleWidth = WaterColumns.TextEm(alleles);
        int labelChars = ProgressBarStyle.VisibleLength(label);
        int valueChars = ProgressBarStyle.VisibleLength(value);
        int alleleChars = ProgressBarStyle.VisibleLength(alleles);
        foreach (var row in rows)
        {
            string pair = row.A.Text + AlleleSeparator + row.B.Text;
            labelWidth = Math.Max(labelWidth, WaterColumns.TextEm(row.Label));
            valueWidth = Math.Max(valueWidth, WaterColumns.TextEm(row.Value));
            alleleWidth = Math.Max(alleleWidth, WaterColumns.TextEm(pair));
            labelChars = Math.Max(labelChars, ProgressBarStyle.VisibleLength(row.Label));
            valueChars = Math.Max(valueChars, row.Value.Length);
            alleleChars = Math.Max(alleleChars, pair.Length);
        }
        const float gap = 0.7f;
        float valueEnd = labelWidth + gap + valueWidth;
        float alleleEnd = valueEnd + gap + alleleWidth;
        float gradeStart = alleleEnd + gap;

        // SectionedTooltips 已按原版 CreateItemTooltip 的调用范围把这些行归入养殖小节。
        var output = new TooltipSections.Writer(builder, item, "animal-genes");
        int widest = labelChars + valueChars + alleleChars + Math.Max(ProgressBarStyle.VisibleLength(grade), 5) + 3;
        output.RequireWidth(ProgressBarStyle.WidthForLine(widest) + 4);
        output.Add(label, Palette.LightPurple);
        Column(value, valueEnd - WaterColumns.TextEm(value), Palette.LightPurple);
        Column(alleles, alleleEnd - WaterColumns.TextEm(alleles), Palette.LightPurple);
        Column(grade, gradeStart, Palette.LightPurple);
        output.NextLine();
        foreach (var row in rows)
        {
            string pair = row.A.Text + AlleleSeparator + row.B.Text;
            output.Add(row.Label, Palette.LightCyan);
            Column(row.Value, valueEnd - WaterColumns.TextEm(row.Value), Palette.LightCyan);
            Column(row.A.Text, alleleEnd - WaterColumns.TextEm(pair), row.A.Color);
            output.Add(AlleleSeparator, Palette.DarkCyan);
            output.Add(row.B.Text, row.B.Color);
            Column(row.A.Grade.ToString(), gradeStart, GradeColor(row.A.Grade));
            output.Add(AlleleSeparator, Palette.Gray);
            output.Add(row.B.Grade.ToString(), GradeColor(row.B.Grade));
            output.NextLine();
        }
        output.Emit();

        void Column(string text, float position, Palette color)
            => output.AddRendered("　" + text, color, ModuleEffectAnnotation.Pos(position) + text);
    }

    private static bool TryAllele(GameItem item, string key, out float value)
    {
        value = 0;
        if (!GameRead.TryPeekTag(item, key, out var tag) || tag?.valueEnabled != true) return false;
        value = tag.valueFloat;
        return float.IsFinite(value);
    }

    internal static char Grade(float allele, float baseline, bool higherIsBetter)
    {
        double improvement = ((double)allele - baseline) / baseline;
        if (!higherIsBetter) improvement = -improvement;
        if (improvement >= GradeS) return 'S';
        if (improvement >= GradeA) return 'A';
        return improvement >= GradeB ? 'B' : 'C';
    }

    private static Palette GradeColor(char grade) => grade switch
    {
        'S' => (Palette)0xD4AF37,
        'A' => Palette.LightGreen,
        'B' => Palette.Gray,
        _ => Palette.LightRed,
    };

    private static string Number(float value) => value.ToString("0.##");

    private static string TraitLabel(string key, string fallback)
    {
        try
        {
            string name = LocHelper.GetLocalizedMechanicOrDefault(key, fallback);
            return name.Trim().TrimEnd(':', '：').TrimEnd();
        }
        catch { return fallback; }
    }
}
