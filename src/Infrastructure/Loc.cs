using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 多语种文案的唯一入口。
///
/// 设计契约（改这里之前先读）：
///   1. **key + 英文默认值**：调用写成 <c>Loc.T("price.sell", "Sell: {0}", sell)</c>。
///      英文默认值就是缺词条时的兜底文案，所以永远不可能把 <c>price.sell</c>
///      这种内部键直接显示给玩家。
///   2. **翻译只放在 <c>Localization/&lt;locale&gt;.csv</c>**（key,value 两列）。英文不单独出
///      CSV：它就是代码里的默认值，两处分别维护必然漂移。
///   3. **只认游戏的语言码**：<c>LocHelper.GetCurrentLocaleCode()</c> 返回
///      <c>SelectedLocale.Identifier.Code</c>，即 <c>en/zh/ru/de/ja/fr/es/pl</c>，
///      以及有词表但界面不可选的 <c>it/ko/ptbr</c>。
///   4. **Init/OnEnable 阶段不许调 T**：那时本地化还没初始化，
///      <c>SelectedLocale</c> 会同步 <c>WaitForCompletion()</c>（见经验
///      harmony-patch-jit-triggers-localization-ctor）。设置项文案一律走
///      <c>Func&lt;string&gt;</c> 延迟求值。
///
/// 为什么不直接用游戏的 <c>ModHelper.GetLocalized</c>：它每次都线性查找
/// <c>ModLoader.Mods</c> 并现读 <c>SelectedLocale</c>，而 tooltip 一帧里要取几十条文案；
/// 且 key 缺失时它会返回 key 本身，把内部键画到 tooltip 上。这里自己做一次读盘 +
/// 缓存，格式与官方 CSV 一致，翻译者照样按官方格式写文件。
/// </summary>
internal static class Loc
{
    private const string ModId = "item_details";

    /// <summary>语言码重新判定的最短间隔：换语言在 0.25 秒内生效，避免每行文案都去问一次游戏。</summary>
    private const float CodeTtlSeconds = 0.25f;

    private static string _code = "";
    private static float _codeAt = -1f;

    /// <summary>语言码 → 翻译表。加载失败或文件不存在时存空表，避免反复读盘。</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Tables =
        new(StringComparer.Ordinal);

    /// <summary>当前语言码（小写、去区域后缀前的原始值）。读不到时沿用上一次成功的值。</summary>
    internal static string Code
    {
        get
        {
            float now = Time.realtimeSinceStartup;
            if (_codeAt >= 0f && now - _codeAt < CodeTtlSeconds) return _code;
            _codeAt = now;

            try
            {
                string code = LocHelper.GetCurrentLocaleCode() ?? "";
                if (code.Length > 0) _code = Normalize(code);
            }
            catch { }
            return _code;
        }
    }

    /// <summary>语言码规范化：小写去空白。区域写法（<c>pt-BR</c>）留给 <see cref="Candidates"/> 展开。</summary>
    private static string Normalize(string code) => code.Trim().ToLowerInvariant();

    /// <summary>
    /// 语言码可能对应的 CSV 文件名，按优先级排列：
    /// 原样（<c>pt-br</c>）→ 去分隔符（<c>ptbr</c>）→ 基础语言（<c>pt</c>）。
    /// 游戏里 <c>SelectedLocale.Identifier.Code</c> 的实际写法在不同 Unity Localization
    /// 版本/资源里可能是 <c>pt-br</c> 或 <c>ptbr</c>，两条都试。
    /// </summary>
    private static IEnumerable<string> Candidates(string code)
    {
        yield return code;

        string squeezed = code.Replace("-", "").Replace("_", "");
        if (!string.Equals(squeezed, code, StringComparison.Ordinal)) yield return squeezed;

        int cut = code.IndexOfAny(new[] { '-', '_' });
        if (cut > 0)
        {
            string baseCode = code.Substring(0, cut);
            if (!string.Equals(baseCode, squeezed, StringComparison.Ordinal)) yield return baseCode;
        }
    }

    /// <summary>
    /// 当前语言是否为中文。只用于排版差异（宽度估算、分隔符习惯），
    /// 不用于选文案——选文案一律走 <see cref="T"/>。
    /// </summary>
    /// <summary>
    /// 游戏自带完整译文的语言（设置里能选的那 8 种）。意、韩、巴葡游戏有词表但基本是空的，
    /// 想借用原版文案时这三种要回落到本模组词条。
    /// </summary>
    internal static bool GameTranslated
    {
        get
        {
            string lang = BaseLanguage(Code);
            return lang is "zh" or "en" or "ja" or "ru" or "de" or "fr" or "es" or "pl";
        }
    }

    private static readonly Dictionary<string, CultureInfo> Cultures = new(StringComparer.Ordinal);

    /// <summary>
    /// 数字格式跟游戏语言走（德法俄等用小数逗号），而不是跟 Windows 区域设置走：
    /// 否则中文游戏在德语系统上会出现「98,50%」，同一行里还可能和固定格式的数字混用。
    /// 建不出对应区域时用 InvariantCulture。
    /// </summary>
    internal static CultureInfo Culture
    {
        get
        {
            string code = Code;
            if (Cultures.TryGetValue(code, out var cached)) return cached;
            var culture = CultureInfo.InvariantCulture;
            string name = code switch
            {
                "" or "en" => "",
                "ptbr" or "pt-br" or "pt_br" => "pt-BR",
                _ when code.StartsWith("zh", StringComparison.Ordinal) => "zh-CN",
                _ => code,
            };
            if (name.Length > 0)
            {
                try { culture = CultureInfo.GetCultureInfo(name); }
                catch (Exception ex) { Log.Warn("Loc.Culture", ex); }
            }
            Cultures[code] = culture;
            return culture;
        }
    }

    internal static bool IsChinese
    {
        get
        {
            string c = Code;
            return c.StartsWith("zh", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 取一条文案。<paramref name="key"/> 存在于当前语言 CSV 时用译文，
    /// 否则用 <paramref name="english"/>；<paramref name="args"/> 按 <see cref="string.Format(string, object[])"/>
    /// 填充 <c>{0}</c> 占位符。
    /// </summary>
    internal static string T(string key, string english, params object[] args)
    {
        string text = english;
        string lang = "en";
        try
        {
            string code = Code;
            if (code.Length > 0 && !IsEnglish(code))
            {
                var table = Table(code);
                if (table != null && table.TryGetValue(key, out string? translated)
                    && !string.IsNullOrEmpty(translated))
                {
                    text = translated;
                    lang = BaseLanguage(code);
                }
            }
        }
        catch { }

        // 复数写法 {1|Tag|Tage} 要在 Format 前按各自语言的规则换成词
        if (TryFormat(Plural.Apply(text, args, lang), args, out string formatted)) return formatted;
        english = Plural.Apply(english, args, "en");
        if (TryFormat(english, args, out formatted)) return formatted;
        return english;
    }

    /// <summary>基础语言码：zh-hans → zh，pt-br / ptbr → pt。</summary>
    private static string BaseLanguage(string code)
    {
        if (code.StartsWith("pt", StringComparison.Ordinal)) return "pt";
        int cut = code.IndexOfAny(new[] { '-', '_' });
        return cut > 0 ? code.Substring(0, cut) : code;
    }

    /// <summary>只取译文、没有兜底文案的场合（极少用：只在兜底已在别处确定时）。</summary>
    internal static string? TryT(string key)
    {
        try
        {
            string code = Code;
            if (code.Length == 0 || IsEnglish(code)) return null;
            var table = Table(code);
            if (table != null && table.TryGetValue(key, out string? translated)
                && !string.IsNullOrEmpty(translated))
                return translated;
        }
        catch { }
        return null;
    }

    private static bool IsEnglish(string code) => code == "en";

    /// <summary>尝试填充占位符；失败时由调用方用英文模板重试，绝不抛到 tooltip 构建路径上。</summary>
    private static bool TryFormat(string text, object[] args, out string result)
    {
        if (args == null || args.Length == 0)
        {
            result = text;
            return true;
        }

        try
        {
            result = string.Format(Culture, text, args);
            return true;
        }
        catch (FormatException)
        {
            result = "";
            return false;
        }
    }

    /// <summary>当前语言的翻译表；文件不存在或解析失败时返回空表并缓存。</summary>
    private static Dictionary<string, string> Table(string code)
    {
        if (Tables.TryGetValue(code, out var cached)) return cached;

        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string candidate in Candidates(code))
        {
            try
            {
                string? text = ModHelper.ReadModFileText(ModId, "Localization/" + candidate + ".csv");
                if (string.IsNullOrEmpty(text)) continue;
                Parse(text!, table);
                if (table.Count > 0) break;
            }
            catch (Exception ex)
            {
                Log.Warn("Loc.Table", ex);
            }
        }

        Tables[code] = table;
        return table;
    }

    /// <summary>
    /// 解析 <c>key,value</c> 两列 CSV。与 <c>ModLoader.LoadLocalization</c> 保持一致：
    /// 只按第一个逗号切分，值两侧加引号时可保留内部逗号，<c>""</c> 还原为 <c>"</c>，
    /// <c>\n</c> 还原为换行；以 <c>#</c> 开头的行是注释。
    /// </summary>
    private static void Parse(string text, Dictionary<string, string> into)
    {
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;

            int comma = line.IndexOf(',');
            if (comma < 0)
            {
                Log.Warn("Loc.Parse", $"第 {i + 1} 行缺少逗号，已跳过");
                continue;
            }

            string key = line.Substring(0, comma).Trim();
            if (key.Length == 0)
            {
                Log.Warn("Loc.Parse", $"第 {i + 1} 行 key 为空，已跳过");
                continue;
            }

            into[key] = Unquote(line.Substring(comma + 1));
        }
    }

    private static string Unquote(string raw)
    {
        string v = raw.Trim();
        if (v.Length >= 2 && v.StartsWith("\"", StringComparison.Ordinal)
            && v.EndsWith("\"", StringComparison.Ordinal))
            v = v.Substring(1, v.Length - 2).Replace("\"\"", "\"");
        return v.Replace("\\n", "\n");
    }

    /// <summary>当前语言的词条数（0 = 没有对应 CSV，全部走英文兜底）。供自检与调试使用。</summary>
    internal static int TableEntries
    {
        get
        {
            try
            {
                string code = Code;
                return code.Length == 0 ? 0 : Table(code).Count;
            }
            catch { return 0; }
        }
    }

    /// <summary>语言切换或重载后丢弃缓存（供自检/调试使用）。</summary>
    internal static void ResetCache()
    {
        Tables.Clear();
        _code = "";
        _codeAt = -1f;
    }
}
