using System;
using System.Collections.Generic;
using System.Text;

namespace ItemDetails;

/// <summary>真伪结论：与原版真伪特性的真实档位一一对应。</summary>
internal enum AuthVerdict { Genuine, Counterfeit, Unusable, Expired, Other }

/// <summary>真伪判定里不依赖游戏的部分：结论怎么下、哪些命名算真伪约定。</summary>
internal static class AuthenticityRules
{
    private const string CategoryPrefix = "CATEGORY_GENUINE_";

    /// <summary>
    /// 真实档位优先于问题标签：原版给过期、不可用的注射器也写 ISSUE_*，它们并不是赝品。
    /// 没有真伪特性的物品（原版直接生成的正品注射器）只有标签可读。
    /// 标为正品却带问题标签时按赝品算：原版对质也只认标签。
    /// 认不出的档位（第三方自定义）看它对价值的影响：降价才算有问题，不因为名字陌生就判假。
    /// </summary>
    internal static AuthVerdict Decide(string? realConditionId, int faults, int valueModifier = 0)
    {
        string id = realConditionId ?? "";
        if (id.StartsWith("counterfeit", StringComparison.Ordinal)) return AuthVerdict.Counterfeit;
        if (id.StartsWith("unusable", StringComparison.Ordinal)) return AuthVerdict.Unusable;
        if (id.StartsWith("expired", StringComparison.Ordinal)) return AuthVerdict.Expired;
        if (id.Length > 0 && !id.StartsWith("genuine", StringComparison.Ordinal) && valueModifier < 0)
            return AuthVerdict.Other;
        return faults > 0 ? AuthVerdict.Counterfeit : AuthVerdict.Genuine;
    }

    /// <summary>真伪特性的类别约定：原版三类都是这个前缀，工坊模组也按它识别。</summary>
    internal static bool IsAuthCategory(string? category)
        => category != null && category.StartsWith(CategoryPrefix, StringComparison.Ordinal)
           && category.Length > CategoryPrefix.Length;

    /// <summary>问题标签的命名约定：<c>ISSUE_X</c> 或 <c>X_ISSUE</c>。只在已确认是真伪物品后才看，单凭名字不算证据。</summary>
    internal static bool IsIssueTag(string? tag)
        => tag != null && tag.Length > 6
           && (tag.StartsWith("ISSUE_", StringComparison.Ordinal) || tag.EndsWith("_ISSUE", StringComparison.Ordinal));

    /// <summary>问题标签里描述问题的部分：去掉 ISSUE 和物品名前缀，如 CIGARETTE_WAX_SEAL_ISSUE → WAX_SEAL。</summary>
    internal static string IssueStem(string tag, string? category)
    {
        string stem = tag.StartsWith("ISSUE_", StringComparison.Ordinal) ? tag.Substring(6)
            : tag.EndsWith("_ISSUE", StringComparison.Ordinal) ? tag.Substring(0, tag.Length - 6) : tag;
        if (IsAuthCategory(category))
        {
            string subject = category!.Substring(CategoryPrefix.Length) + "_";
            if (stem.Length > subject.Length && stem.StartsWith(subject, StringComparison.Ordinal))
                stem = stem.Substring(subject.Length);
        }
        return stem;
    }

    /// <summary>词表里没有译名时的显示：WAX_SEAL → Wax Seal。</summary>
    internal static string Humanize(string stem)
    {
        var text = new StringBuilder(stem.Length);
        foreach (string word in stem.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (text.Length > 0) text.Append(' ');
            text.Append(word[0]).Append(word.Substring(1).ToLowerInvariant());
        }
        return text.ToString();
    }
}
