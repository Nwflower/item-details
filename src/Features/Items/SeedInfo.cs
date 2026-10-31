using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 种子性状透视：不必先分析（测序/磁盘）就能看到该种子携带的性状与等级。
///
/// 依据（0.46D）：
///   `PlantGeneticsHelper.HasAnyTrait(item)` / `GetTraits(item)` 均 public static，
///   返回 `List<(PlantTrait trait, int rank)>`。显示名必须走 `trait.displayName`：
///   原生性状**大多没有注册 nameProvider**（只有少数液体产物性状用 `.Named(...)`），
///   此时 displayName 会转去 `PlantGeneticsText.TraitName(id, englishName)` 查
///   Mechanic 词表；直接读 `nameProvider()` 会拿到 null 而回落到 `englishName`，
///   于是中文环境里整列性状都是英文（2026-10 用户反馈）。
///   原版要经过分析流程才把性状展示给玩家，这里直接读标签。
///
/// 只读，不写任何标签。开关由调用方（ModEntry）判断，模块本身不认识配置。
/// </summary>
internal static class SeedInfo
{
    private const RenderHandler.ColorPalette Color = RenderHandler.ColorPalette.LightTurquoise;

    private const int MaxTraits = 8;

    internal static List<TooltipLine> Collect(GameItem? item)
    {
        var lines = new List<TooltipLine>();
        try
        {
            if (item == null) return lines;

            // 廉价前置判定：只有种子/基因磁盘才有性状。
            // 否则每个物品都要进 GetTraits（内部遍历 PlantTraitRegistry.All）。
            bool isSeed = GameRead.IsTag(item, "SEED_ITEM_TAG");
            if (!isSeed && !GameRead.IsTag(item, "DNA_DISK_TAG")) return lines;

            // 扫描仪已开启时，原版 CreateSeedGeneticsTooltip 自己就会列出性状，
            // 我们再追加一遍只会重复（且"未分析"的说法也不成立）。
            bool scannerOn = false;
            try { scannerOn = PlantGeneticsHelper.IsScannerOn(); } catch { }
            if (scannerOn) return lines;

            List<(PlantTrait trait, int rank)> traits;
            try { traits = PlantGeneticsHelper.GetTraits(item); }
            catch (Exception ex) { Log.Warn("SeedInfo.GetTraits", ex); return lines; }
            if (traits == null || traits.Count == 0) return lines;

            int shown = 0;
            foreach (var (trait, rank) in traits)
            {
                if (shown >= MaxTraits) break;
                if (trait == null) continue;

                // 走 displayName：nameProvider 为空时它会查 Mechanic 词表（plant_trait_<id>_name）。
                // 词表缺条目时 PlantGeneticsText 自己会回落英文，这里再兜一层 id。
                string name = "";
                try { name = trait.displayName ?? ""; } catch { }
                if (string.IsNullOrEmpty(name)) name = trait.englishName ?? trait.id ?? "?";
                if (string.IsNullOrEmpty(name)) continue;

                // 标题已写明“性状”，条目只列名称，避免每条重复“性状：”。
                string text = rank > 1
                    ? Loc.T("seed.trait_rank", "{0} (rank {1})", name, rank)
                    : name;
                lines.Add(new TooltipLine(text, Color));
                shown++;
            }

            if (traits.Count > shown)
            {
                lines.Add(new TooltipLine(
                    Loc.T("seed.more", "... and {0} more", traits.Count - shown),
                    Color));
            }

            if (lines.Count > 0) Log.MarkOk("SeedInfo");
        }
        catch (Exception ex)
        {
            Log.Warn("SeedInfo", ex);
        }
        return lines;
    }
}
