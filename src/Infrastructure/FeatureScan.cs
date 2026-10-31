using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 安全遍历物品特性。ConditionEffects 与 FeatureReveal 原先各写一份
/// 「取 itemFeatures、限 128 项、逐项 try/catch」的骨架，现在共用这里。
/// </summary>
internal static class FeatureScan
{
    /// <summary>超过这个数量视为数据异常，整体放弃，避免异常数据把 tooltip 拖慢。</summary>
    private const int MaxFeatures = 128;

    /// <summary>返回非空特性列表；读取失败或数量异常时返回空表。</summary>
    internal static List<ItemFeature> All(GameItem? item)
    {
        var result = new List<ItemFeature>();
        if (item == null) return result;

        try
        {
            var features = item.itemFeatures;
            if (features == null) return result;

            int count = features.Count;
            if (count <= 0 || count > MaxFeatures) return result;

            for (int i = 0; i < count; i++)
            {
                ItemFeature? f;
                try { f = features[i]; } catch { continue; }
                if (f != null) result.Add(f);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("FeatureScan", ex);
            return result;
        }
        return result;
    }
}
