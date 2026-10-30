using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 在别人写出的某一行后面就地补一段说明：泛型的"认行 + 追加"放在这里，具体物品写进 <see cref="Rules"/> 表。
///
/// 认行只靠原文模板，不猜文本：模板取自写这行的人自己的本地化——
/// 第三方模组用官方 <c>ModHelper.GetLocalized(modId, key)</c>（与它自己取词同一个入口，跟随语言，
/// 当前语言缺失时退到 en）。取不到就不补，不复制对方的文案兜底。模板里的 <c>{0}</c> 只接受整数。
/// 由 <see cref="TooltipPipeline"/> 的 AddLine 前缀调用；只改文本，不新增行，所以位置天然紧跟原句。
///
/// 加一种物品：在 <see cref="Rules"/> 里登记物品 ID、模板来源和后缀函数；后缀函数返回 null 表示这次不补。
/// 第三方模组若愿意主动接入，优先走 <see cref="ItemDetailsApi"/>，这张表只服务没有接入的模组。
/// </summary>
internal static class LineAnnotations
{
    /// <summary>格式化模板时占住 {0} 的私用区字符，用来切出前后两段。</summary>
    private const string Slot = "";

    private sealed class Rule
    {
        internal readonly string[] ItemIds;
        /// <summary>模板所属模组 ID。</summary>
        internal readonly string ModId;
        internal readonly string Key;
        internal readonly Func<GameItem, string?> Suffix;

        // 按语言缓存切好的模板
        internal string Locale = "\0";
        internal string? Head;
        internal string? Tail;
        /// <summary>同一次 tooltip 构建只补一次（嵌套构建、重复行都不叠加）。</summary>
        internal int Build = -1;

        internal Rule(string[] itemIds, string modId, string key, Func<GameItem, string?> suffix)
        {
            ItemIds = itemIds;
            ModId = modId;
            Key = key;
            Suffix = suffix;
        }
    }

    // ---------------- 表 ----------------

    // Pen的自学习强化机原先在「当晚训练：N 次」后补学满数，现由 MachineAdapters.Learning 的进度条代替。
    private static readonly Rule[] Rules = { };

    private static readonly Dictionary<string, List<Rule>> ById = Index();

    private static Dictionary<string, List<Rule>> Index()
    {
        var map = new Dictionary<string, List<Rule>>(StringComparer.Ordinal);
        foreach (var rule in Rules)
            foreach (string id in rule.ItemIds)
            {
                if (!map.TryGetValue(id, out var list)) map[id] = list = new List<Rule>();
                list.Add(rule);
            }
        return map;
    }

    // ---------------- 泛型部分 ----------------

    internal static void TryAppend(ref string text, GameItem item)
    {
        if (!ModSettings.InfoEnhance || string.IsNullOrEmpty(text)) return;

        string id = item.identifier ?? "";
        if (id.Length == 0 || !ById.TryGetValue(id, out var rules)) return;

        foreach (var rule in rules)
        {
            try
            {
                if (rule.Build == TooltipPipeline.BuildId || !Matches(rule, text)) continue;
                rule.Build = TooltipPipeline.BuildId;
                string? suffix = rule.Suffix(item);
                if (!string.IsNullOrEmpty(suffix)) text += suffix;
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("LineAnnotations." + rule.ModId + "." + rule.Key, ex);
            }
        }
    }

    private static bool Matches(Rule rule, string text)
    {
        EnsureTemplate(rule);
        string? head = rule.Head, tail = rule.Tail;
        if (head == null || tail == null) return false;
        if (text.Length <= head.Length + tail.Length) return false;
        if (!text.StartsWith(head, StringComparison.Ordinal) || !text.EndsWith(tail, StringComparison.Ordinal))
            return false;
        string middle = text.Substring(head.Length, text.Length - head.Length - tail.Length);
        return int.TryParse(middle, out _);
    }

    private static void EnsureTemplate(Rule rule)
    {
        string locale = Loc.Code;
        if (rule.Locale == locale) return;
        rule.Locale = locale;
        rule.Head = rule.Tail = null;

        string template = ModHelper.GetLocalized(rule.ModId, rule.Key, Slot) ?? "";
        int at = template.IndexOf(Slot, StringComparison.Ordinal);
        if (at < 0) return;
        rule.Head = template.Substring(0, at);
        rule.Tail = template.Substring(at + Slot.Length);
    }
}

