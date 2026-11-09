using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 真伪判定，分两层：
///   已知物品（<see cref="Profiles"/>）：原版鉴定窗口按类型写死五个勾选框，顺序与原版词条都在这张表里，预填勾选只认它。
///   嗅探：物品身上读到启用中的真伪特性（CATEGORY_GENUINE_*，带真实档位）才算真伪物品，再枚举它实际启用的问题标签。
/// 证据有先后：标签名只用来找问题项，单凭 ISSUE 字样不会把任何物品判成真伪物品。
/// 结论看真实档位，见 <see cref="AuthenticityRules.Decide"/>。
/// </summary>
internal static class Authenticity
{
    /// <summary>
    /// 一项检查：原版标签键 + 本地化键 + 英文兜底 + 鉴定窗口勾选框的原版标题键。
    /// 这里只存键不存成品文案：分析随时可能被调用，语言却可能在两次悬停之间切换。
    /// </summary>
    internal readonly struct Check
    {
        internal readonly string Tag;       // 原版标签键（item.IsTag 用）
        internal readonly string Key;       // 本地化键（Localization/*.csv）
        internal readonly string En;        // 英文兜底
        internal readonly string GameKey;   // 原版 UI 表里鉴定窗口的勾选框标题
        internal Check(string tag, string key, string en, string gameKey)
        {
            Tag = tag; Key = key; En = en; GameKey = gameKey;
        }

        /// <summary>
        /// 优先用勾选框的原版标题：玩家对质时看到的就是这几个字。
        /// 原版对话里的同名词条（ins_*_issue_*）是嵌进整句的变格碎片，单独显示在德俄波等语言里不通，所以不用。
        /// 意、韩、巴葡的原版 UI 表基本是空的，这些情况用本模组词条。
        /// </summary>
        internal string Name() => GameTitle(GameKey) ?? Loc.T(Key, En);
    }

    /// <summary>原版 UI 表里的标题；没有这个词条或没有译文时返回 null。</summary>
    private static string? GameTitle(string key)
    {
        if (!Loc.GameTranslated) return null;
        try
        {
            if (LocHelper.HasLocalizedUI(key))
            {
                string text = LocHelper.GetLocalizedUI(key);
                if (!string.IsNullOrWhiteSpace(text)
                    && !LocHelper.TryParseMissingTranslation(text, out _, out _))
                    return text.Trim();
            }
        }
        catch { }
        return null;
    }

    /// <summary>一种可鉴定真伪的物品。<see cref="Checks"/> 的顺序就是鉴定窗口五个勾选框的顺序，预填勾选按下标对应。</summary>
    internal sealed class Profile
    {
        internal readonly string TypeTag;
        internal readonly string Category;
        internal readonly Check[] Checks;
        internal Profile(string typeTag, string category, params Check[] checks)
        {
            TypeTag = typeTag; Category = category; Checks = checks;
        }
    }

    private static readonly Profile[] Profiles =
    {
        new("INS_INJECTOR_TAG", "CATEGORY_GENUINE_INJECTOR",
            new("ISSUE_INDICATOR", "auth.indicator", "Indicator Issues", "inspection_indicator_issues"),
            new("ISSUE_LABEL", "auth.label", "Label Issues", "inspection_label_issues"),
            new("ISSUE_SERIAL", "auth.serial", "Serial Number", "inspection_serial_issues"),
            new("ISSUE_BODY", "auth.body", "Body Issues", "inspection_body_issues"),
            new("ISSUE_COLOR", "auth.color", "Color Issues", "inspection_color_issues")),
        new("CIGARETTE_TAG", "CATEGORY_GENUINE_CIGARETTE",
            new("CIGARETTE_LETTERING_ISSUE", "auth.lettering", "Lettering Issues", "inspection_lettering_issues"),
            new("CIGARETTE_COLOR_ISSUE", "auth.color", "Color Issues", "inspection_color_issues"),
            new("CIGARETTE_SEAL_ISSUE", "auth.seal", "Security Seal Issues", "inspection_seal_issues"),
            new("CIGARETTE_LOGO_ISSUE", "auth.logo", "Logo Issues", "inspection_logo_issues"),
            new("CIGARETTE_BOX_ISSUE", "auth.box", "Box Issues", "inspection_box_issues")),
        new("FOOD_STAMP_TAG", "CATEGORY_GENUINE_STAMP",
            new("STAMP_LETTERING_ISSUE", "auth.lettering", "Lettering Issues", "inspection_lettering_issues"),
            new("STAMP_LOGO_ISSUE", "auth.logo", "Logo Issues", "inspection_logo_issues"),
            new("STAMP_SEAL_ISSUE", "auth.seal", "Security Seal Issues", "inspection_seal_issues"),
            new("STAMP_AMOUNT_ISSUE", "auth.amount", "Stamp Value", "inspection_value_issues"),
            new("STAMP_CHIP_ISSUE", "auth.chip", "Chip", "inspection_chip_issues")),
    };

    /// <summary>这件物品属于哪一种；不可鉴定真伪时返回 null。</summary>
    internal static Profile? ProfileOf(GameItem? item)
    {
        foreach (var profile in Profiles)
            if (GameRead.IsTag(item, profile.TypeTag)) return profile;
        return null;
    }

    internal readonly struct Result
    {
        internal readonly bool HasData;
        internal readonly bool Complete;            // 该读的都读到了才下"正品"结论
        internal readonly AuthVerdict Verdict;
        internal readonly string? ConditionDisplay; // 原版真实档位的显示名；只在认不出档位时用
        internal readonly int ReadableFlags;        // 成功读到的项数
        internal readonly int ExpectedFlags;        // 应读项数
        internal readonly int Faults;               // 判定有问题的项数
        internal readonly List<string> FaultNames;  // 已按当前语言解析的不合格项名
        internal Result(bool hasData, bool complete, AuthVerdict verdict, string? conditionDisplay,
            int readable, int expected, int faults, List<string> names)
        {
            HasData = hasData; Complete = complete; Verdict = verdict; ConditionDisplay = conditionDisplay;
            ReadableFlags = readable; ExpectedFlags = expected; Faults = faults; FaultNames = names;
        }
        internal bool IsGenuine => Complete && Verdict == AuthVerdict.Genuine;
    }

    internal static Result Analyze(GameItem? item)
    {
        var names = new List<string>();
        var profile = ProfileOf(item);
        ItemFeature? feature = AuthFeature(item, profile);
        if (profile == null && feature == null)
            return new Result(false, false, AuthVerdict.Genuine, null, 0, 0, 0, names);

        int readable = 0, faults = 0;
        if (profile != null)
        {
            foreach (var c in profile.Checks)
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
        }

        bool scanned = ScanIssueTags(item!, profile, feature, names, ref faults);
        ItemCondition? real = feature?.realCondition;
        var verdict = AuthenticityRules.Decide(real?.identifier, faults, real?.modValueDisplay ?? 0);
        int expected = profile?.Checks.Length ?? 0;
        // 已知物品靠表里的项下结论，枚举失败只是少了额外项；嗅探到的物品只有这一个来源
        bool complete = profile != null ? readable == expected && readable > 0 : scanned;
        return new Result(profile == null || readable > 0, complete, verdict, real?.display, readable, expected, faults, names);
    }

    /// <summary>已知物品按表里的类别取特性；其余物品找带真实档位的真伪特性。</summary>
    private static ItemFeature? AuthFeature(GameItem? item, Profile? profile)
    {
        if (item == null) return null;
        try
        {
            if (profile != null) return item.FindItemFeatureByCategory(profile.Category);
            if (item.itemFeatures == null) return null;
            foreach (var feature in item.itemFeatures)
                if (feature != null && feature.useCondition && feature.realCondition != null
                    && AuthenticityRules.IsAuthCategory(feature.category))
                    return feature;
        }
        catch (Exception ex)
        {
            Log.Warn("Auth.Feature", ex);
        }
        return null;
    }

    /// <summary>枚举表外的问题标签：第三方加的问题项或第三方的物品。按键名排序，同一件物品每次输出一致。</summary>
    private static bool ScanIssueTags(GameItem item, Profile? profile, ItemFeature? feature, List<string> names, ref int faults)
    {
        try
        {
            var extra = new List<string>();
            foreach (var pair in item.modifiedState.dict)
                if (AuthenticityRules.IsIssueTag(pair.Key) && pair.Value != null && pair.Value.IsEnabled()
                    && !IsListed(profile, pair.Key))
                    extra.Add(pair.Key);
            extra.Sort(StringComparer.Ordinal);
            string? category = profile?.Category ?? feature?.category;
            foreach (string tag in extra)
            {
                faults++;
                names.Add(SniffedName(tag, category));
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Auth.Scan", ex);
            return false;
        }
    }

    private static bool IsListed(Profile? profile, string tag)
    {
        if (profile == null) return false;
        foreach (var check in profile.Checks)
            if (check.Tag == tag) return true;
        return false;
    }

    /// <summary>先按原版勾选框的命名规律找词条，找不到就把标签写成可读的英文，不显示原始键名。</summary>
    private static string SniffedName(string tag, string? category)
    {
        string stem = AuthenticityRules.IssueStem(tag, category);
        return GameTitle("inspection_" + stem.ToLowerInvariant() + "_issues") ?? AuthenticityRules.Humanize(stem);
    }

    /// <summary>结论的名字，用原版词表：存档里的档位显示名定格在生成时的语言，不跟随切换语言。</summary>
    internal static string VerdictName(in Result result) => result.Verdict switch
    {
        AuthVerdict.Counterfeit => LocHelper.GetLocalizedMechanicOrDefault("mech_condition_counterfeit", "Counterfeit"),
        AuthVerdict.Unusable => LocHelper.GetLocalizedMechanicOrDefault("mech_condition_unusable", "Unusable"),
        AuthVerdict.Expired => LocHelper.GetLocalizedMechanicOrDefault("mech_condition_expired", "Expired"),
        _ => result.ConditionDisplay ?? "",
    };
}
