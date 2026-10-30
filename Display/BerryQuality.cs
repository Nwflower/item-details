namespace ItemDetails;

internal enum BerryQuality
{
    NotBerry = -100,   // 不是荧光莓
    Unknown = 0,
    Normal,
    Small,
    Moldy,
    Unripe,            // 未成熟（visualVariant 0/1 均归此类）
    Dried,
}

/// <summary>荧光莓品质分类。condition/visualVariant 语义来自 0.46D 反编译结果。</summary>
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
