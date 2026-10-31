using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 真伪判定：三类违禁品（注射器/香烟/粮票）各有一组"问题标签"。
/// 只读 item.IsTag(key)，某个 ISSUE_* 为 true 即该项有问题。
/// 检查表与目标游戏版本保持一致；第三方新增规则由第三方自行说明，不用标签名猜测。
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

    /// <summary>
    /// 一项检查：原版标签键 + 本地化键 + 英文兜底 + 鉴定面板上对应的原版标题键。
    /// 这里只存键不存成品文案：分析随时可能被调用，语言却可能在两次悬停之间切换。
    /// </summary>
    internal readonly struct Check
    {
        internal readonly string Tag;       // 原版标签键（item.IsTag 用）
        internal readonly string Key;       // 本地化键（Localization/*.csv）
        internal readonly string En;        // 英文兜底
        internal readonly string? GameKey;  // 原版 UI 表里鉴定面板的问题标题
        internal Check(string tag, string key, string en, string? gameKey = null)
        {
            Tag = tag; Key = key; En = en; GameKey = gameKey;
        }

        /// <summary>
        /// 优先用鉴定面板上的原版标题：玩家勾选问题时看到的就是这几个字。
        /// 原版对话里的同名词条（ins_*_issue_*）是嵌进整句的变格碎片，单独显示在德俄波等语言里不通，所以不用。
        /// 意、韩、巴葡的原版 UI 表基本是空的，盒体也没有标题，这些情况用本模组词条。
        /// </summary>
        internal string Name()
        {
            if (GameKey != null && Loc.GameTranslated)
            {
                try
                {
                    if (LocHelper.HasLocalizedUI(GameKey))
                    {
                        string text = LocHelper.GetLocalizedUI(GameKey);
                        if (!string.IsNullOrWhiteSpace(text)
                            && !LocHelper.TryParseMissingTranslation(text, out _, out _))
                            return text.Trim();
                    }
                }
                catch { }
            }
            return Loc.T(Key, En);
        }
    }

    private static readonly Check[] InjectorChecks =
    {
        new("ISSUE_INDICATOR", "auth.indicator", "Indicator Issues", "ins_inj_indicator_issues"),
        new("ISSUE_LABEL", "auth.label", "Label Issues", "ins_inj_label_issues"),
        new("ISSUE_SERIAL", "auth.serial", "Serial Number", "ins_inj_serial_number_issues"),
        new("ISSUE_BODY", "auth.body", "Body Issues", "ins_inj_body_issues"),
        new("ISSUE_COLOR", "auth.color", "Color Issues", "ins_general_color_issues"),
    };

    private static readonly Check[] CigaretteChecks =
    {
        new("CIGARETTE_LETTERING_ISSUE", "auth.lettering", "Lettering Issues", "ins_general_lettering_issues"),
        new("CIGARETTE_COLOR_ISSUE", "auth.color", "Color Issues", "ins_general_color_issues"),
        new("CIGARETTE_SEAL_ISSUE", "auth.seal", "Security Seal Issues", "ins_general_security_seal_issues"),
        new("CIGARETTE_LOGO_ISSUE", "auth.logo", "Logo Issues", "ins_general_logo_issues"),
        new("CIGARETTE_BOX_ISSUE", "auth.box", "Box Issues"),   // 鉴定面板没有盒体标题
    };

    private static readonly Check[] StampChecks =
    {
        new("STAMP_LETTERING_ISSUE", "auth.lettering", "Lettering Issues", "ins_general_lettering_issues"),
        new("STAMP_LOGO_ISSUE", "auth.logo", "Logo Issues", "ins_general_logo_issues"),
        new("STAMP_SEAL_ISSUE", "auth.seal", "Security Seal Issues", "ins_general_security_seal_issues"),
        new("STAMP_AMOUNT_ISSUE", "auth.amount", "Stamp Value", "ins_stamp_value"),
        new("STAMP_CHIP_ISSUE", "auth.chip", "Chip", "ins_stamp_chip"),
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
        internal readonly List<string> FaultNames;  // 已按当前语言解析的不合格项名
        internal Result(ItemKind kind, int readable, int expected, int faults, List<string> names)
        {
            Kind = kind; ReadableFlags = readable; ExpectedFlags = expected;
            Faults = faults; FaultNames = names;
        }
        internal bool HasData => Kind != ItemKind.None && ReadableFlags > 0;
        /// <summary>所有原版检查项都成功读到才下"正品"结论。</summary>
        internal bool Complete => HasData && ReadableFlags == ExpectedFlags;
        internal bool IsGenuine => Complete && Faults == 0;
    }

    internal static Result Analyze(GameItem? item)
    {
        ItemKind kind = DetectKind(item);
        var names = new List<string>();
        if (kind == ItemKind.None)
            return new Result(kind, 0, 0, 0, names);

        var checks = ChecksFor(kind);
        int readable = 0, faults = 0;
        foreach (var c in checks)
        {
            try
            {
                // IsTag 返回 false 表示"没有这个问题"；true 表示有问题
                bool bad = item!.IsTag(c.Tag);
                readable++;
                if (bad) { faults++; names.Add(c.Name()); }
                Log.MarkOk("Auth." + c.Tag);
            }
            catch (Exception ex)
            {
                Log.Warn("Auth." + c.Tag, ex);
            }
        }
        return new Result(kind, readable, checks.Length, faults, names);
    }
}
