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

    internal static string? DisplayName(BerryQuality q, bool zh)
    {
        if (zh)
        {
            return q switch
            {
                BerryQuality.Normal => "正常",
                BerryQuality.Small => "较小",
                BerryQuality.Moldy => "发霉",
                BerryQuality.Unripe => "未成熟",
                BerryQuality.Dried => "干枯",
                _ => null,
            };
        }
        return q switch
        {
            BerryQuality.Normal => "Normal",
            BerryQuality.Small => "Small",
            BerryQuality.Moldy => "Moldy",
            BerryQuality.Unripe => "Unripe",
            BerryQuality.Dried => "Dried",
            _ => null,
        };
    }
}
