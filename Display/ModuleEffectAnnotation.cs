using System;

namespace ItemDetails;

/// <summary>
/// 把原版「模组效果: -性能: +16%」这类百分比行就地补上实际变更，例如：
///   -性能: +16%(+136毫升/天)
///   -效率: +16%(-0能量/天)
///   -质量: +16%(幽灵水)
///
/// 为什么就地改而不是另起一行：玩家要的是"每个加成后面直接看到影响"，而这三行正是
/// 原版 `MachineHelper.AddTooltipModuleBonus` 用 `RichTextBuilder.AddLine` 打出来的，
/// 我们已经在 AddLine 前缀里，接得住。
///
/// 数值全部来自游戏自己，不复制公式：
///   · 性能：水分收集器用 `MachineMoistureFarm.GetOutputVolume`（基准 = BASE_OUTPUT_ML），
///           其它机器用 BASE_PROCESSING_SPEED_TAG → CURRENT_PROCESSING_SPEED_TAG；
///   · 效率：MACHINERY_DRAWN_POWER_BASE → machinery_drawn_power（游戏已按 +效率% 算好）；
///   · 质量：`MachineMoistureFarm.GetOutputQualityDisplay` 的档位名。
/// 任何一项读不到就不补——宁可少一段，也不写错一个数。
/// </summary>
internal static class ModuleEffectAnnotation
{
    private static string _locale = "";
    private static string _perf = "";
    private static string _eff = "";
    private static string _qual = "";

    /// <summary>命中「加成行」时自己写完整行并返回 true（调用方丢弃原行）。</summary>
    internal static bool TryApply(ref string text, RichTextBuilder builder,
        RenderHandler.ColorPalette color, GameItem? item)
    {
        try
        {
            if (!ModSettings.ModuleEffects) return false;
            if (item == null || string.IsNullOrEmpty(text)) return false;
            if (text.IndexOf('%') < 0) return false;   // 只认「+16%」这种加成行

            EnsureLabels();

            char kind;
            if (_perf.Length > 0 && text.Contains(_perf, StringComparison.Ordinal)) kind = 'p';
            else if (_eff.Length > 0 && text.Contains(_eff, StringComparison.Ordinal)) kind = 'e';
            else if (_qual.Length > 0 && text.Contains(_qual, StringComparison.Ordinal)) kind = 'q';
            else return false;

            string note = BuildNote(kind, item);
            if (note.Length == 0) return false;

            // 原行照旧，注释用灰色；这里自己收行，所以调用方不要再走原 AddLine
            builder.Add(text, color);
            builder.Add(note, RenderHandler.ColorPalette.Gray);
            builder.NextLine();
            return true;
        }
        catch { return false; }
    }

    private static string BuildNote(char kind, GameItem item)
    {
        bool zh = Locales.IsChinese;

        switch (kind)
        {
            case 'p':
                if (IsMoistureFarm(item))
                {
                    int baseMl = MachineMoistureFarm.BASE_OUTPUT_ML;
                    int curMl = MachineMoistureFarm.GetOutputVolume(item) / 1000;
                    if (curMl > 0) return Wrap(Signed(curMl - baseMl) + (zh ? "毫升/天" : " ml/day"));
                }

                var speedBase = GameRead.TagInt(item, "BASE_PROCESSING_SPEED_TAG");
                var speedNow = GameRead.TagInt(item, "CURRENT_PROCESSING_SPEED_TAG");
                if (speedBase is int sb && speedNow is int sn && sb > 0 && sn > 0)
                    return Wrap(Signed(sn - sb) + (zh ? "速度" : " speed"));
                return "";

            case 'e':
                var powerBase = GameRead.TagInt(item, "MACHINERY_DRAWN_POWER_BASE");
                var powerNow = GameRead.TagInt(item, "machinery_drawn_power");
                if (powerBase is int pb && powerNow is int pn)
                    return Wrap(Signed(pn - pb) + (zh ? "能量/天" : " power/day"));
                return "";

            case 'q':
                // 水分收集器的品质不是百分比：GetOutputQuality 用「质量加成 + 50」当分数，
                // 再按 50/100/150/200 分档，所以 +16 只是把 50 推到 66，档位仍是幽灵水。
                // 写成 +0.5% 会是编的，这里显示档位 + 当前分数/下一档。
                string tier = "";
                try { tier = MachineMoistureFarm.GetOutputQualityDisplay(item) ?? ""; } catch { }

                var qualityBonus = GameRead.TagInt(item, "TOTAL_PERCENTAGE_QUALITY_BONUS_INT");
                if (tier.Length > 0 && qualityBonus is int qb)
                {
                    int score = qb + 50;          // 与游戏 GetOutputQuality 同一口径
                    int next = NextQualityStep(score);
                    return Wrap(zh ? $"{tier} {score}/{next}" : $"{tier} {score}/{next}");
                }
                return tier.Length > 0 ? Wrap(tier) : "";
        }

        return "";
    }

    private static bool IsMoistureFarm(GameItem item)
    {
        try { return string.Equals(item.identifier, "moisture_farm", StringComparison.Ordinal); }
        catch { return false; }
    }

    /// <summary>零变化也写成负数（-0），和玩家「效率+16% 应该减耗」的直觉一致。</summary>
    private static string Signed(int delta) => delta <= 0 ? "-" + (-delta) : "+" + delta;

    private static string Wrap(string body) => "(" + body + ")";

    /// <summary>下一档品质阈值：游戏 `GetOutputQuality` 用 50/100/150/200 分档。</summary>
    private static int NextQualityStep(int score)
    {
        if (score < 50) return 50;
        if (score < 100) return 100;
        if (score < 150) return 150;
        return 200;
    }

    /// <summary>三项加成的标签名走游戏本地化；换语言后自动重建。</summary>
    private static void EnsureLabels()
    {
        string locale = Locales.Code;
        if (locale == _locale && _perf.Length > 0) return;

        try { _perf = LocHelper.GetLocalizedMechanic("mech_module_stat_performance") ?? ""; } catch { _perf = ""; }
        try { _eff = LocHelper.GetLocalizedMechanic("mech_module_stat_efficiency") ?? ""; } catch { _eff = ""; }
        try { _qual = LocHelper.GetLocalizedMechanic("mech_module_stat_quality") ?? ""; } catch { _qual = ""; }
        _locale = locale;
    }
}
