using System;
using ItemDetails.Integration;

namespace ItemDetails;

/// <summary>
/// 逐件读取第三方排版声明。只认排版方通过
/// <see cref="ItemDetailsClient.RegisterTooltipLayout"/> 的注册：不按程序集名嗅探，也不读对方的配置项。
/// 那两条都只对某一个旧版本成立，对方一改版就静默失效，而且会把"谁在画"的判断变成猜。
/// </summary>
internal static class SectionedLayout
{
    private static int cachedBuild;
    private static TooltipLayoutOptions cachedOptions;

    internal static bool UsesNativeBars => TooltipPipeline.Current is GameItem item
        && (For(item) & TooltipLayoutOptions.NativeProgressBars) != 0;

    internal static bool UsesFlowingText(GameItem item)
        => (For(item) & TooltipLayoutOptions.FlowingText) != 0;

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
