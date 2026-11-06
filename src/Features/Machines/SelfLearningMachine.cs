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

    private static System.Reflection.MethodInfo? _preview, _previewText;
    private static bool _resolved;

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var night = asm.GetType("PenSelfLearning.NightService", false);
                var loc = asm.GetType("PenSelfLearning.Loc", false);
                if (night == null || loc == null) continue;
                _preview = night.GetMethod("PreviewAdvances", new[] { typeof(GameItem) });
                _previewText = loc.GetMethod("TrainingPreview", new[] { typeof(int) });
                return;
            }
        }
        catch { }
    }

    /// <summary>Pen 自己的「当晚训练：X 次」文字（它的预览函数与本地化），取不到返回 null。</summary>
    internal static string? TrainingText(GameItem machine)
    {
        try
        {
            Resolve();
            if (_preview == null || _previewText == null) return null;
            if (_preview.Invoke(null, new object[] { machine }) is not int advances) return null;
            return _previewText.Invoke(null, new object[] { advances }) as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// 机器的说明（效率、质量、概率三句）已全部体现在色块第三列，整行隐藏。
    /// 只在说明原样等于机器的背景文字、且色块可用时才隐藏，否则原样保留。
    /// </summary>
    internal static bool ShouldHideFlavor(string text, GameItem? item)
    {
        try
        {
            if (!ModSettings.ModuleEffects || !LabelChips.Available || !IsMachine(item)) return false;
            string flavor = item!.flavorText ?? "";
            if (flavor.Length > 0 && string.Equals(text.Trim(), flavor.Trim(), StringComparison.Ordinal)) return true;
            // 「当晚训练：X 次」已并进质量色块的第三列
            string? training = TrainingText(item);
            return training != null && string.Equals(text.Trim(), training.Trim(), StringComparison.Ordinal);
        }
        catch { return false; }
    }
}
