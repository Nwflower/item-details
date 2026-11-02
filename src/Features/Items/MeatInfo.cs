using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 肉类来源和朊病毒标记。严格只读：不调用 FoodItemHelper.IsMeatFood，
/// 因为它在缺失 MEAT_TYPE_TAG 时会通过 GetTagReadonly 初始化标签。
///
/// 依据游戏原版 FoodItemHelper.IsMeatFood：存在 MEAT_FOOD_TAG 或
/// MEAT_TYPE_TAG 非空均属于肉类。普通肉品模板没有来源，也必须显示“未知”。
/// 来源名字从原版物种注册表获取，不猜测不存在/无法识别的类型。
/// 朊病毒检查使用 IsTag 的启用状态，不以字典里是否存在该键为准。
/// </summary>
internal static class MeatInfo
{
    private const RenderHandler.ColorPalette Info = RenderHandler.ColorPalette.LightTurquoise;
    private const RenderHandler.ColorPalette Neutral = RenderHandler.ColorPalette.Gray;
    private const RenderHandler.ColorPalette Warning = RenderHandler.ColorPalette.LightRed;

    internal static List<TooltipLine> Collect(GameItem? item)
    {
        var lines = new List<TooltipLine>(2);
        if (item == null) return lines;

        // 与原版 IsMeatFood 判定一致，但完全不经 GetTagReadonly。
        string? speciesId = GameRead.TagString(item, "MEAT_TYPE_TAG");
        if (!GameRead.IsTag(item, "MEAT_FOOD_TAG") && string.IsNullOrEmpty(speciesId))
            return lines;

        string source = ResolveSource(speciesId) ?? Loc.T("meat.unknown", "Unknown");
        lines.Add(new TooltipLine(
            Loc.T("meat.source", "Meat source: {0}", source), Info));

        // “未标记”只代表没有启用的朊病毒标签，不表示同类相食绝对安全。
        bool hasPrion = GameRead.IsTag(item, "ANIMAL_PRION_TAG");
        string status = hasPrion
            ? Loc.T("meat.prion.present", "Present")
            : Loc.T("meat.prion.unmarked", "Unmarked");
        lines.Add(new TooltipLine(
            Loc.T("meat.prion", "Prions: {0}", status),
            hasPrion ? Warning : Neutral));
        return lines;
    }

    private static string? ResolveSource(string? speciesId)
    {
        if (string.IsNullOrWhiteSpace(speciesId)) return null;

        // AquacultureItems.GENERIC_FISH_MEAT_TYPE：只有通用鱼类，没有具体鱼种。
        if (string.Equals(speciesId, "FISH", StringComparison.Ordinal))
            return Loc.T("meat.fish", "Fish");

        try
        {
            if (AnimalSpecies.All != null
                && AnimalSpecies.All.TryGetValue(speciesId, out var animal)
                && animal != null)
            {
                string name = animal.GetDisplayName();
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch (Exception ex) { Log.Warn("MeatInfo.AnimalName", ex); }

        try
        {
            if (FishSpecies.All != null
                && FishSpecies.All.TryGetValue(speciesId, out var fish)
                && fish != null)
            {
                string name = fish.GetDisplayName();
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch (Exception ex) { Log.Warn("MeatInfo.FishName", ex); }
        return null;
    }
}
