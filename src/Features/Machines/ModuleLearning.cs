using System;
using System.Collections.Generic;
using System.Text;

namespace ItemDetails;

/// <summary>
/// 「自学习算法」模块的学习进度：每项加成当前值 / 学习上限。
///
/// 原版（ModuleEffectHelper.OnUsedLearningAlgo）每次机器运转给三项加成各 +1，
/// 加到 LEARNING_ALGO_MAX_*_INT 为止；模块 tooltip 只显示当前加成，上限不显示。
/// 使用同一组原版标签的模块可以直接复用这份只读进度，不推断具体机器的推进频率。
///
/// 上限只读标签，不按「基础值 × 2」推算：其它模组可能改倍率，或者移除词条。
/// 标签不存在或已禁用的那一项就不显示。
/// </summary>
internal static class ModuleLearning
{
    private static readonly string[] Stats = { "PERFORMANCE", "EFFICIENCY", "QUALITY" };
    private static readonly string[] StatNameKeys =
    {
        "mech_module_stat_performance", "mech_module_stat_efficiency", "mech_module_stat_quality",
    };

    internal readonly struct Stat
    {
        internal readonly int Index;
        internal readonly int Current;
        internal readonly int Max;

        internal Stat(int index, int current, int max)
        {
            Index = index;
            Current = current;
            Max = max;
        }

        internal int Remaining => Math.Max(0, Max - Current);
        /// <summary>负面加成：从负值向 0 学。</summary>
        internal bool Negative => Max <= 0;
    }

    private const string LearningEffect = "MODULE_EFFECT_LEARNING_ALGORITHM";

    /// <summary>
    /// 是学习模块：任一项有学习上限，或带原版的学习效果标记。
    /// 全部为负的模块在 InitLearningAlgo 里一个上限也不会写，只能靠效果标记认出来。
    /// </summary>
    private static bool IsLearning(GameItem module)
    {
        for (int i = 0; i < Stats.Length; i++)
            if (GameRead.TagEnabled(module, "LEARNING_ALGO_MAX_" + Stats[i] + "_INT")
                && (GameRead.TagInt(module, "LEARNING_ALGO_MAX_" + Stats[i] + "_INT") ?? 0) > 0) return true;
        return string.Equals(GameRead.TagString(module, "MODULE_EFFECT1_TAG"), LearningEffect, StringComparison.Ordinal)
            || string.Equals(GameRead.TagString(module, "MODULE_EFFECT2_TAG"), LearningEffect, StringComparison.Ordinal);
    }

    /// <summary>
    /// 模块各项的学习进度；不是学习模块时返回空表。
    /// 负面加成同样会学：原版 OnUsedLearningAlgo 对没有上限标签的项按「上限 0」处理，
    /// 每次运转把负值 +1，直到 0。所以负项记作 当前值/0，不再被「没有上限标签」跳过。
    /// </summary>
    internal static List<Stat> Read(GameItem? module)
    {
        var stats = new List<Stat>();
        if (module == null || !IsLearning(module)) return stats;
        for (int i = 0; i < Stats.Length; i++)
        {
            string maxTag = "LEARNING_ALGO_MAX_" + Stats[i] + "_INT";
            int cur = GameRead.TagInt(module, "BONUS_PERCENTAGE_" + Stats[i] + "_INT") ?? 0;
            int max = GameRead.TagEnabled(module, maxTag) ? GameRead.TagInt(module, maxTag) ?? 0 : 0;
            if (max > 0) stats.Add(new Stat(i, cur, max));
            else if (cur < 0) stats.Add(new Stat(i, cur, 0));
        }
        return stats;
    }

    /// <summary>模块最多还差几点学满；不是学习模块返回 -1。</summary>
    internal static int Remaining(GameItem? module)
    {
        var stats = Read(module);
        if (stats.Count == 0) return -1;
        int remaining = 0;
        foreach (var s in stats) remaining = Math.Max(remaining, s.Remaining);
        return remaining;
    }

    /// <summary>模块 tooltip：「学习进度：性能 12/30、质量已学满」。</summary>
    internal static List<TooltipLine> Collect(GameItem? item, TooltipSections.Writer output = default)
    {
        var lines = new List<TooltipLine>();
        try
        {
            var stats = Read(item);
            if (stats.Count == 0) return lines;

            if (output.Capturing)
                output.Field("stats", stats.ConvertAll(s => (Stats[s.Index], s.Current, s.Max)).AsReadOnly());
            var sb = new StringBuilder();
            bool allFull = true;
            foreach (var s in stats)
            {
                if (sb.Length > 0) sb.Append(Loc.T("list.sep", ", "));
                string name = StatName(s.Index);
                if (s.Remaining == 0)
                {
                    sb.Append(Loc.T("learning.stat_full", "{0} maxed", name));
                }
                else
                {
                    sb.Append(Loc.T("learning.stat", "{0} {1}/{2}", name, s.Current, s.Max));
                    allFull = false;
                }
            }

            lines.Add(new TooltipLine(Loc.T("learning.progress", "Learning: {0}", sb.ToString()),
                allFull ? RenderHandler.ColorPalette.LightGreen : RenderHandler.ColorPalette.LightCyan));
            Log.MarkOk("ModuleLearning");
        }
        catch (Exception ex)
        {
            Log.Warn("ModuleLearning", ex);
        }
        return lines;
    }

    /// <summary>加成名称（游戏原文）。</summary>
    internal static string NameOf(int index) => StatName(index);

    /// <summary>加成名称用游戏原文（与模块 tooltip 的「性能 +16%」同一个词），去掉可能带的冒号和短横。</summary>
    private static string StatName(int index)
    {
        string name = "";
        try { name = LocHelper.GetLocalizedMechanic(StatNameKeys[index]) ?? ""; } catch { }
        name = name.Trim().Trim('-', ':', '：').Trim();
        if (name.Length > 0) return name;
        return index switch { 0 => "Performance", 1 => "Efficiency", _ => "Quality" };
    }
}
