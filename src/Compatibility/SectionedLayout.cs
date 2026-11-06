using System;
using ItemDetails.Integration;

namespace ItemDetails;

/// <summary>
/// 逐件读取第三方排版声明。旧版普通容器面板的兼容由
/// <see cref="LegacySectionedTooltips"/> 单独处理，不扩大成整个 Contents 区块的接管。
/// </summary>
internal static class SectionedLayout
{
    private static int cachedBuild;
    private static TooltipLayoutOptions cachedOptions;

    internal static bool DrawsContents(GameItem item)
        => (For(item) & TooltipLayoutOptions.DrawsContents) != 0;

    private static TooltipLayoutOptions For(GameItem item)
    {
        if (!ReferenceEquals(item, TooltipPipeline.Current)) return Read(item);
        if (cachedBuild != TooltipPipeline.BuildId)
        {
            cachedOptions = Read(item);
            cachedBuild = TooltipPipeline.BuildId;
        }
        return cachedOptions;
    }

    private static TooltipLayoutOptions Read(GameItem item)
    {
        if (AppDomain.CurrentDomain.GetData(ItemDetailsClient.LayoutSlot) is Func<GameItem, int?> providers)
        {
            foreach (Func<GameItem, int?> describe in providers.GetInvocationList())
            {
                try
                {
                    if (describe(item) is int options) return (TooltipLayoutOptions)options;
                }
                catch (Exception ex) { Log.Warn("TooltipLayout", ex); }
            }
        }
        return TooltipLayoutOptions.None;
    }
}
