using System;
using ItemDetails.Integration;

namespace ItemDetails;

/// <summary>
/// 保留已承诺的 SectionedTooltips 旧版行为。1.6.2 / 1.6.3 的开关和普通容器面板
/// 已对照上游 68de0ca / ed5379e；不把这些配置约定推断到未知版本。
/// 规范文本已统一为游戏进度条、自然文本，旧版无需声明即可读取。
/// </summary>
internal static class LegacySectionedTooltips
{
    private const string ModId = "amres.sectioned_tooltips";

    internal static bool YieldsContentsGrid(bool sealedBox)
    {
        // 旧版只画普通容器；不能因此吞掉密封箱透视、压缩率或机器内部信息。
        if (sealedBox) return false;
        // 有显式接入时连 null / None / 异常都尊重为不接管，不再猜旧设置。
        if (AppDomain.CurrentDomain.GetData(ItemDetailsClient.LayoutSlot) is Func<GameItem, int?>
            || ThirdPartyProviders.HasItemTooltipDisplays) return false;

        var mod = ModLoader.Mods.Find(m => m.IsEnabled && m.Manifest.ID == ModId);
        if (mod == null || (mod.Manifest.ModVersion != "1.6.2" && mod.Manifest.ModVersion != "1.6.3"))
            return false;
        var config = ModConfig.Find(ModId);
        // 两个旧版本的默认值都为 true；只读，不创建配置或写入玩家文件。
        return (config?.Get("Enabled") as ConfigEntry<bool>)?.Value != false
            && (config?.Get("ContentsPanel") as ConfigEntry<bool>)?.Value != false;
    }
}
