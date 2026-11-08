using System;

namespace ItemDetails;

/// <summary>
/// Neon Undertow（工坊 3833095213，manifest ID <c>water_seller_english_collection</c>）内置兼容层。
///
/// 0.5.3 的反编译证据：
///   - NeonLiquids.Tooltip 通过官方 ModHook.OnCreateTooltipLate 自己写出风味饮料的成分、
///     容量/酒精度、调配方式与「可倒入」提示；
///   - NeonLiquids.Start 把配方液体（Id(BarIngredient) = <c>nu_flavour_*</c>）加入
///     Liquid.Liquids，Stop 时移除；HasFlavour 正是检查这些组分的量 &gt; 0。
///
/// 于是同一瓶饮料会有两份成分信息：物品详情按 Liquid.Liquids 遍历后把配方按占比再画一遍，
/// 而且这些液体的 metabolismRatePerDay &gt; 0 会被判成「药物活性成分」，口径与对方的
/// 「风味原料占比」不同。这里按公开的逐件接管机制让出 LiquidAnalysis 区块，让对方独写。
///
/// 生效条件刻意收得很窄（对齐对方的 HasFlavour，不猜规则、不调用对方内部方法）：
///   1. NU 已启用——它的液体只在其启动时出现在 Liquid.Liquids；
///   2. 物品是液体容器；
///   3. 本次液体分析确实会输出（Analyze 返回非 null，受玩家设置与扫描器门控）；
///   4. 分析结果的成分里有 nu_flavour_ 前缀的组分。
/// 其余容器、NU 未启用、玩家关闭液体分析时都不让出，行为与原来完全一致。
///
/// 两个耦合点：让出区块后没有成分表来重画体积条，原版体积行必须保留（见
/// WaterContainerLine.Skip）；逐件接管会跳过该区块的快照，排版方读不到 water 区块，
/// 这些行本来由对方自己写出。公开 API 形状与区块协议均未改动（COMPATIBILITY.md 第 1、2 条）。
/// </summary>
internal static class NeonUndertow
{
    internal const string ModId = "water_seller_english_collection";

    /// <summary>NeonLiquids.Id(BarIngredient) 生成的风味液体前缀，对应它的 flavourParts。</summary>
    private const string FlavourPrefix = "nu_flavour_";

    /// <summary>NU 在 NeonLiquids.Start 动态加入、Stop 移除的液体前缀。</summary>
    private static readonly string[] NuLiquidPrefixes =
    {
        "nu_flavour_", "nu_soda_", "nu_sealed_",
    };

    // 同一次 tooltip 构建里 RunRenderers 与 WaterContainerLine.Skip 各问一次，按实例 + 构建号缓存。
    private static GameItem? _cacheItem;
    private static int _cacheBuild = -1;
    private static bool _cacheValue;

    /// <summary>NU 是否已启用：它的液体只在其启动时存在，比查 Mod 列表更直接。</summary>
    private static bool Installed()
    {
        try
        {
            foreach (var liquid in Liquid.Liquids)
            {
                string id = liquid?.identifier ?? "";
                foreach (var prefix in NuLiquidPrefixes)
                    if (id.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>这件容器是否是 NU 会自己写成分行的风味饮料；只在物品详情本来要显示时判断。</summary>
    private static bool IsFlavouredDrink(GameItem item)
    {
        if (!GameRead.IsTag(item, "LIQUID_CONTAINER_TAG")) return false;
        // 液体分析关闭或没扫描器时 Analyze 返回 null：物品详情本来就不画成分表，无需让出。
        var analysis = LiquidQueries.Analyze(item);
        if (analysis == null) return false;
        foreach (var component in analysis.Components)
            if (component.Id.StartsWith(FlavourPrefix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>逐件接管回调：只对 NU 风味饮料让出液体分析区块。</summary>
    private static ItemDetailsTooltipSection Sections(GameItem item)
    {
        if (item == null) return ItemDetailsTooltipSection.None;
        try
        {
            bool cacheable = ReferenceEquals(item, TooltipPipeline.Current);
            if (cacheable && ReferenceEquals(item, _cacheItem) && _cacheBuild == TooltipPipeline.BuildId)
                return _cacheValue ? ItemDetailsTooltipSection.LiquidAnalysis : ItemDetailsTooltipSection.None;

            bool value = Installed() && IsFlavouredDrink(item);
            if (cacheable)
            {
                _cacheItem = item;
                _cacheBuild = TooltipPipeline.BuildId;
                _cacheValue = value;
            }
            return value ? ItemDetailsTooltipSection.LiquidAnalysis : ItemDetailsTooltipSection.None;
        }
        catch { return ItemDetailsTooltipSection.None; }
    }

    /// <summary>供体积行补丁复用：让出液体分析时不能同时跳过原版体积行。</summary>
    internal static bool SuppressesLiquidAnalysis(GameItem item)
        => Sections(item) != ItemDetailsTooltipSection.None;

    internal static void Install(PatchScope scope)
    {
        IDisposable registration = ThirdPartyProviders.RegisterTooltip(Sections);
        scope.OnDispose(registration.Dispose);
        Log.Msg("NeonUndertow: registered per-item LiquidAnalysis handoff");
    }
}
