using System;
using System.Collections.Generic;
using System.Linq;

namespace ItemDetails;

/// <summary>机器加成行中要说明的项目。</summary>
public enum ItemDetailsModuleStat
{
    Performance,
    Efficiency,
    Quality,
}

/// <summary>净化手段走哪个游戏函数；Item Details 用该函数在探针上模拟净化收益。</summary>
public enum ItemDetailsPurifierKind
{
    /// <summary>机器，模拟 MachinePurifier.PurifyContainer（读机器自身的性能/品质加成）。</summary>
    Machine,
    /// <summary>手持净水器，模拟 WaterHelper.PurifyToBaseWater。</summary>
    Portable,
    /// <summary>可溶化学剂，模拟 ChemicalHelper.DissolveInto 加温度反应链（按物品自身的液体含量）。</summary>
    ChemicalAgent,
}

/// <summary>一项已经由提供方解释清楚的价值贡献。</summary>
public readonly struct ItemDetailsBreakdownPart
{
    public string Label { get; }
    public int Amount { get; }
    public bool IsBase { get; }

    public ItemDetailsBreakdownPart(string label, int amount, bool isBase = false)
    {
        Label = label;
        Amount = amount;
        IsBase = isBase;
    }
}

/// <summary>第三方鉴定类型的完整价值分解。</summary>
public sealed class ItemDetailsBreakdown
{
    public int Total { get; }
    public IReadOnlyList<ItemDetailsBreakdownPart> Parts { get; }

    public ItemDetailsBreakdown(int total, IEnumerable<ItemDetailsBreakdownPart> parts)
    {
        Total = total;
        Parts = (parts ?? throw new ArgumentNullException(nameof(parts))).ToArray();
    }
}

/// <summary>返回 null 表示本次不提供分解；总价必须和该物品当前规则一致。</summary>
public delegate ItemDetailsBreakdown? ItemDetailsAppraisalProvider(GameItem item);

/// <summary>返回已本地化的注释正文（不含括号）；null 或空串表示这一项不写注释。</summary>
public delegate string? ItemDetailsMachineEffectProvider(GameItem machine, ItemDetailsModuleStat stat);

/// <summary>
/// 第三方模组的显式接入口。提供方只返回自己理解的语义，Item Details 不采样、不反推规则。
/// 在第三方模组的 OnEnable 中注册，并保存返回的注册句柄，在 OnDisable 中 Dispose。
/// 同一鉴定类型或机器 ID 同时只允许一个提供方，避免隐式覆盖其它模组。
/// </summary>
public static class ItemDetailsApi
{
    /// <summary>按 AppraisalItemType.id 注册鉴定分解提供方。</summary>
    public static IDisposable RegisterAppraisalBreakdown(string appraisalTypeId, ItemDetailsAppraisalProvider provider)
        => ThirdPartyProviders.RegisterAppraisal(appraisalTypeId, provider);

    /// <summary>按机器物品 identifier 注册加成注释提供方；注册后该 ID 的原版注释由提供方接管。</summary>
    public static IDisposable RegisterMachineEffects(string itemId, ItemDetailsMachineEffectProvider provider)
        => ThirdPartyProviders.RegisterMachine(itemId, provider);

    /// <summary>
    /// 按物品 identifier 声明它是哪种净水手段，水质建议会用对应的原版函数模拟收益。
    /// 未注册的物品仍会按回调证据与标准试验自动识别；注册后以这里为准。
    /// </summary>
    public static IDisposable RegisterWaterPurifier(string itemId, ItemDetailsPurifierKind kind)
        => ThirdPartyProviders.RegisterPurifier(itemId, kind);
}
