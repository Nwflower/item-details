namespace ItemDetails;

internal enum BerryQuality
{
    NotBerry = -100,   // 没有 BERRY_TAG，不是果实
    Unknown = 0,
    Normal,
    Small,
    Moldy,
    Unripe,            // 未成熟（visualVariant 0/1 均归此类）
    Dried,
}

/// <summary>
/// 果实品质分档。condition/visualVariant 语义来自 0.46D 反编译结果；
/// 带 BERRY_TAG 的物品都适用，不限于荧光莓（第三方果实沿用同一个 InitBerry）。
/// </summary>
internal static class BerryQualityClassifier
{
    internal static BerryQuality Classify(int condition, int visualVariant)
    {
        if (condition == -2) return BerryQuality.Moldy;
        if (condition == -1)
        {
            return visualVariant switch
            {
                0 => BerryQuality.Unripe,
                1 => BerryQuality.Unripe,
                2 => BerryQuality.Dried,
                _ => BerryQuality.Unknown,
            };
        }
        if (condition == 0 || condition == 1) return BerryQuality.Normal;
        if (condition == 2) return BerryQuality.Small;
        return BerryQuality.Unknown;
    }

    internal static string? DisplayName(BerryQuality q) => q switch
    {
        BerryQuality.Normal => Loc.T("berry.normal", "Normal"),
        BerryQuality.Small => Loc.T("berry.small", "Small"),
        BerryQuality.Moldy => Loc.T("berry.moldy", "Moldy"),
        BerryQuality.Unripe => Loc.T("berry.unripe", "Underripe"),
        BerryQuality.Dried => Loc.T("berry.dried", "Dried"),
        _ => null,
    };
}
