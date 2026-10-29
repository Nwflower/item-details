using System;

namespace ItemDetails;

/// <summary>
/// 与其它模组的共存规则。
///
/// 目前只有一条：AquaSense（工坊 3828878417，ID <c>chiqingsan.AquaSense</c>）是专职水质模组，
/// 它已经在 tooltip 里输出「水质详情 + 液体成分 + 容量条 + 滤芯耐久」。本模组若把
/// 「水质：xx% / 杂质 0.7%」再列一遍就是纯重复，玩家看到的是两段几乎一样的水质信息。
/// 所以检测到它启用时，本模组让出水质段，只保留它没有的「净化收益建议」。
/// </summary>
internal static class ModCompat
{
    private const string AquaSenseId = "chiqingsan.AquaSense";

    private static bool? _aquaSense;

    /// <summary>AquaSense 是否已加载并启用。模组的启用状态只在启动时读取，因此结果可缓存。</summary>
    internal static bool AquaSenseActive
    {
        get
        {
            if (_aquaSense.HasValue) return _aquaSense.Value;
            _aquaSense = Detect(AquaSenseId);
            return _aquaSense.Value;
        }
    }

    private static bool Detect(string id)
    {
        try
        {
            foreach (var mod in ModLoader.Mods)
            {
                if (mod?.Manifest == null) continue;
                if (!string.Equals(mod.Manifest.ID, id, StringComparison.OrdinalIgnoreCase)) continue;
                return mod.IsEnabled;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("ModCompat", ex);
        }

        // 兜底：Mods 列表里没有（例如尚未加载完）时，用启用集合判断。
        try { return ModLoader.EnabledMods != null && ModLoader.EnabledMods.Contains(id); }
        catch { return false; }
    }
}
