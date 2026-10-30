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
    }

    /// <summary>模块各项的学习进度；不是学习模块时返回空表。</summary>
    internal static List<Stat> Read(GameItem? module)
    {
        var stats = new List<Stat>();
        if (module == null) return stats;
        for (int i = 0; i < Stats.Length; i++)
        {
            string maxTag = "LEARNING_ALGO_MAX_" + Stats[i] + "_INT";
            if (!GameRead.TagEnabled(module, maxTag)) continue;
            int max = GameRead.TagInt(module, maxTag) ?? 0;
            if (max <= 0) continue;
            int cur = GameRead.TagInt(module, "BONUS_PERCENTAGE_" + Stats[i] + "_INT") ?? 0;
            stats.Add(new Stat(i, cur, max));
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
    internal static List<TooltipLine> Collect(GameItem? item)
    {
        var lines = new List<TooltipLine>();
        try
        {
            var stats = Read(item);
            if (stats.Count == 0) return lines;

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
