using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ItemDetails;

/// <summary>
/// GetCurrentValue 可见的本地物品状态指纹。市场事件等全局输入不在这里展开，
/// 仍由子项价值缓存的 TTL 兜底；第三方定价若读取其它字段，也遵循同一 TTL。
/// </summary>
internal static class ItemValueFingerprint
{
    internal static bool TryRead(GameItem item, out long fingerprint)
    {
        fingerprint = unchecked((long)14695981039346656037UL);
        try
        {
            AddItem(ref fingerprint, item);
            // withChild:true 不只看 contentWindow；电池、模块和检视槽也可能参与价值。
            var children = item.FindAllChildItems();
            Mix(ref fingerprint, children.Count);
            foreach (var child in children)
            {
                if (child != null) AddItem(ref fingerprint, child);
            }
            return true;
        }
        catch
        {
            fingerprint = 0;
            return false;
        }
    }

    private static void AddItem(ref long hash, GameItem item)
    {
        Mix(ref hash, item.uniqueId);
        Mix(ref hash, item.identifier);
        Mix(ref hash, item.unitCount);
        Mix(ref hash, item.unitValue);
        Mix(ref hash, item.lateUnitValue);
        Mix(ref hash, item.unitBaseValue);
        Mix(ref hash, item.backupUnitValue);

        var tags = item.modifiedState?.dict;
        if (tags != null)
        {
            Mix(ref hash, tags.Count);
            foreach (var pair in tags)
            {
                Mix(ref hash, pair.Key);
                var tag = pair.Value;
                if (tag == null) { Mix(ref hash, -1); continue; }
                Mix(ref hash, tag.valueEnabled ? 1 : 0);
                Mix(ref hash, tag.valueString);
                Mix(ref hash, tag.valueInt);
                Mix(ref hash, tag.valueLong);
                Mix(ref hash, BitConverter.DoubleToInt64Bits(tag.valueDouble));
                Mix(ref hash, tag.valueBool ? 1 : 0);
                Mix(ref hash, BitConverter.DoubleToInt64Bits(tag.valueFloat));
            }
        }

        var features = item.itemFeatures;
        if (features == null) return;
        Mix(ref hash, features.Count);
        foreach (var feature in features)
        {
            if (feature == null) { Mix(ref hash, -1); continue; }
            Mix(ref hash, feature.identifier);
            Mix(ref hash, feature.category);
            Mix(ref hash, (int)feature.featureType);
            Mix(ref hash, feature.isDisabled ? 1 : 0);
            Mix(ref hash, (int)feature.GetValueStage());
            // 同时覆盖暴露状态、真假条件和实际价值修正，无需读取其私有字段。
            Mix(ref hash, feature.GetCurrentValueMod());
        }
    }

    private static void Mix(ref long hash, string? value)
    {
        if (value == null) { Mix(ref hash, -1); return; }
        Mix(ref hash, value.Length);
        foreach (char c in value) Mix(ref hash, c);
    }

    private static void Mix(ref long hash, long value)
    {
        unchecked { hash = (hash ^ value) * 1099511628211L; }
    }
}

/// <summary>缓存键严格按物品实例身份比较，不走游戏类型可能覆写的 Equals。</summary>
internal sealed class ReferenceIdentityComparer<T> : IEqualityComparer<T> where T : class
{
    internal static readonly ReferenceIdentityComparer<T> Instance = new();
    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}
