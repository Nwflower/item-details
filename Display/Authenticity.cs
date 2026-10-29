using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 真伪判定：三类违禁品（注射器/香烟/粮票）各有一组"问题标签"。
/// 只读 item.IsTag(key)，某个 ISSUE_* 为 true 即该项有问题。
/// </summary>
internal static class Authenticity
{
    internal enum ItemKind { None, Injector, Cigarette, Stamp }

    /// <summary>类型标签 → 种类</summary>
    internal static ItemKind DetectKind(GameItem? item)
    {
        if (GameRead.IsTag(item, "INS_INJECTOR_TAG")) return ItemKind.Injector;
        if (GameRead.IsTag(item, "CIGARETTE_TAG")) return ItemKind.Cigarette;
        if (GameRead.IsTag(item, "FOOD_STAMP_TAG")) return ItemKind.Stamp;
        return ItemKind.None;
    }

    internal readonly struct Check
    {
        internal readonly string Tag;   // 原版标签键（item.IsTag 用）
        internal readonly string Zh;
        internal readonly string En;
        internal Check(string tag, string zh, string en) { Tag = tag; Zh = zh; En = en; }
    }

    private static readonly Check[] InjectorChecks =
    {
        new("ISSUE_INDICATOR", "指示灯异常", "Indicator fault"),
        new("ISSUE_LABEL", "标签/名称异常", "Label fault"),
        new("ISSUE_SERIAL", "序列号异常", "Serial fault"),
        new("ISSUE_BODY", "瓶体破损", "Body damage"),
        new("ISSUE_COLOR", "颜色异常", "Color fault"),
    };

    private static readonly Check[] CigaretteChecks =
    {
        new("CIGARETTE_LETTERING_ISSUE", "字母印刷异常", "Lettering fault"),
        new("CIGARETTE_COLOR_ISSUE", "颜色异常", "Color fault"),
        new("CIGARETTE_SEAL_ISSUE", "防伪标异常", "Seal fault"),
        new("CIGARETTE_LOGO_ISSUE", "商标异常", "Logo fault"),
        new("CIGARETTE_BOX_ISSUE", "包装盒异常", "Box fault"),
    };

    private static readonly Check[] StampChecks =
    {
        new("STAMP_LETTERING_ISSUE", "字母印刷异常", "Lettering fault"),
        new("STAMP_LOGO_ISSUE", "商标异常", "Logo fault"),
        new("STAMP_SEAL_ISSUE", "防伪标异常", "Seal fault"),
        new("STAMP_AMOUNT_ISSUE", "面值异常", "Value fault"),
        new("STAMP_CHIP_ISSUE", "芯片异常", "Chip fault"),
    };

    internal static Check[] ChecksFor(ItemKind kind) => kind switch
    {
        ItemKind.Injector => InjectorChecks,
        ItemKind.Cigarette => CigaretteChecks,
        ItemKind.Stamp => StampChecks,
        _ => Array.Empty<Check>(),
    };

    internal readonly struct Result
    {
        internal readonly ItemKind Kind;
        internal readonly int ReadableFlags;    // 成功读到的项数
        internal readonly int ExpectedFlags;    // 应读项数
        internal readonly int Faults;           // 判定有问题的项数
        internal readonly List<string> FaultNamesZh;
        internal readonly List<string> FaultNamesEn;
        internal Result(ItemKind kind, int readable, int expected, int faults, List<string> zh, List<string> en)
        {
            Kind = kind; ReadableFlags = readable; ExpectedFlags = expected;
            Faults = faults; FaultNamesZh = zh; FaultNamesEn = en;
        }
        internal bool HasData => Kind != ItemKind.None && ReadableFlags > 0;
        /// <summary>所有应读项都成功读到。缺项时不得下"正品"结论。</summary>
        internal bool Complete => HasData && ReadableFlags == ExpectedFlags;
        internal bool IsGenuine => Complete && Faults == 0;
    }

    internal static Result Analyze(GameItem? item)
    {
        ItemKind kind = DetectKind(item);
        var zh = new List<string>();
        var en = new List<string>();
        if (kind == ItemKind.None)
            return new Result(kind, 0, 0, 0, zh, en);

        var checks = ChecksFor(kind);
        int readable = 0, faults = 0;
        foreach (var c in checks)
        {
            try
            {
                // IsTag 返回 false 表示"没有这个问题"；true 表示有问题
                bool bad = item!.IsTag(c.Tag);
                readable++;
                if (bad) { faults++; zh.Add(c.Zh); en.Add(c.En); }
                Log.MarkOk("Auth." + c.Tag);
            }
            catch (Exception ex)
            {
                Log.Warn("Auth." + c.Tag, ex);
            }
        }
        return new Result(kind, readable, checks.Length, faults, zh, en);
    }
}
