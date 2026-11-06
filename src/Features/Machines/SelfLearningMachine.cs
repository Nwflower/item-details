using System;

namespace ItemDetails;

/// <summary>
/// Pen 的自学习强化机（工坊 3828925546）的适配：
///   · 性能决定每晚有多大概率让一个模组获得「自学习」词条（上限 100%），写进「性能」色块的第三列；
///   · 说明文字（效率、质量、概率三句）因此多余，整行隐藏。
/// 公式来自该模组的 GrantChancePass：性能总和夹在 0–100 当作百分比。
/// 词条授予被它的设置关掉时，说明里本来就没有这一句（只剩效率与质量两句），那时也不写概率。
/// </summary>
internal static class SelfLearningMachine
{
    internal const string MachineId = "Pen_mod_selflearning.machine";
    internal const string LegacyMachineId = "pen_selflearning.machine";

    internal static bool IsMachine(GameItem? item)
    {
        string? id = item?.identifier;
        return string.Equals(id, MachineId, StringComparison.Ordinal)
            || string.Equals(id, LegacyMachineId, StringComparison.Ordinal);
    }

    private static readonly char[] ClauseSeparators = { '；', ';' };

    /// <summary>说明由三句话组成时（效率、质量、性能）才带「授予」那一句。</summary>
    private static bool ShowsGrantClause(string? flavor)
    {
        if (string.IsNullOrEmpty(flavor)) return false;
        int count = 0;
        foreach (char c in flavor!) if (Array.IndexOf(ClauseSeparators, c) >= 0) count++;
        return count >= 2;
    }

    /// <summary>每晚获得词条的概率（%）；授予被关掉时为 0。参数可以是带加成覆盖的探针。</summary>
    internal static int GrantChance(GameItem machine)
    {
        if (!ShowsGrantClause(machine.flavorText)) return 0;
        int performance = GameRead.TagInt(machine, "TOTAL_PERCENTAGE_PERFORMANCE_BONUS_INT") ?? 0;
        return Math.Clamp(performance, 0, 100);
    }

    /// <summary>
    /// 质量每满多少算一次额外推进：从机器说明里「质量每 50%」这类文字取第一个百分数（Pen 的设置值已写进说明）。
    /// 不反射 Pen 的程序集——跨程序集反射会让加载器把本 mod 判成 reflection-external，清单就得声明能力。
    /// </summary>
    internal static int? QualityPerAdvance(GameItem machine)
    {
        var m = System.Text.RegularExpressions.Regex.Match(machine.flavorText ?? "", @"(\d+)\s*%");
        return m.Success && int.TryParse(m.Groups[1].Value, out int v) && v > 0 ? Math.Clamp(v, 10, 200) : null;
    }

    // ---------------- 「当晚训练：X 次」并入质量第三列 ----------------
    // Pen 的这一行在机器信息之后才写出，而质量行已经先写好。所以质量行的渲染写法里放占位（兜底），
    // 这一行写出时取走文本存在 builder 上，原版窗口构建时再把占位换成它。取不到时用兜底文字，信息不丢。

    internal const char TokenStart = '';
    internal const char TokenEnd = '';
    private const string PenModId = "Pen_mod_selflearning";
    private const string TrainingKey = "training_preview";
    private const string Slot = "";

    private sealed class Box { internal string? Text; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RichTextBuilder, Box> Captured = new();
    private static string _tplLocale = "";
    private static string? _tplHead, _tplTail;

    /// <summary>这行是不是 Pen 的「当晚训练：X 次」（用它自己的本地化模板匹配，不依赖语言）；是就记下文本。</summary>
    internal static bool CaptureTraining(RichTextBuilder builder, string text, GameItem? item)
    {
        try
        {
            if (!IsMachine(item) || string.IsNullOrEmpty(text)) return false;
            string locale = Loc.Code;
            if (_tplLocale != locale)
            {
                _tplLocale = locale;
                _tplHead = _tplTail = null;
                string template = ModHelper.GetLocalized(PenModId, TrainingKey, Slot) ?? "";
                int at = template.IndexOf(Slot, StringComparison.Ordinal);
                if (at >= 0)
                {
                    _tplHead = template.Substring(0, at);
                    _tplTail = template.Substring(at + Slot.Length);
                }
            }
            if (_tplHead == null || _tplTail == null) return false;
            string t = text.Trim();
            string head = _tplHead.Trim(), tail = _tplTail.Trim();
            if (t.Length <= head.Length + tail.Length || !t.StartsWith(head, StringComparison.Ordinal)
                || !t.EndsWith(tail, StringComparison.Ordinal)) return false;
            if (!int.TryParse(t.Substring(head.Length, t.Length - head.Length - tail.Length).Trim(), out _)) return false;
            Captured.Remove(builder);
            Captured.Add(builder, new Box { Text = t });
            return true;
        }
        catch { return false; }
    }

    /// <summary>质量第三列的渲染写法：整段文字包上占位，构建时才换成 Pen 的原文。</summary>
    internal static string WithTrainingToken(string render, string note)
        => note.Length == 0 ? render : render.Replace(note, TokenStart + note + TokenEnd);

    /// <summary>构建时把占位换成 Pen 的「当晚训练」文本；没取到就留兜底文字。</summary>
    internal static string ResolveTokens(string render, RichTextBuilder builder)
    {
        string? text = Captured.TryGetValue(builder, out var box) ? box.Text : null;
        var sb = new System.Text.StringBuilder(render.Length);
        for (int i = 0; i < render.Length; i++)
        {
            if (render[i] != TokenStart) { sb.Append(render[i]); continue; }
            int end = render.IndexOf(TokenEnd, i + 1);
            if (end < 0) { sb.Append(render, i + 1, render.Length - i - 1); break; }
            sb.Append(text ?? render.Substring(i + 1, end - i - 1));
            i = end;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 机器的说明（效率、质量、概率三句）已全部体现在色块第三列，整行隐藏。「当晚训练」那行是 Pen 自己写的合计，保留。
    /// 只在说明原样等于机器的背景文字、且色块可用时才隐藏，否则原样保留。
    /// </summary>
    internal static bool ShouldHideFlavor(string text, GameItem? item)
    {
        try
        {
            if (!ModSettings.ModuleEffects || !LabelChips.Available || !IsMachine(item)) return false;
            string flavor = item!.flavorText ?? "";
            return flavor.Length > 0 && string.Equals(text.Trim(), flavor.Trim(), StringComparison.Ordinal);
        }
        catch { return false; }
    }
}
