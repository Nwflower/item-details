using System;

namespace ItemDetails;

/// <summary>
/// 语言判定与文案选择的唯一入口。
///
/// 以前 ModSettings / CustomerAlert / DoubleClickInspect / AppraisalTitlePatch
/// 各写一份 IsChinese()，ModEntry 又自己解析一遍 locale，规则重复五处，改一处必漏四处。
/// 现在判定规则只在这里存在一份，并按 locale code 缓存结果：同一次会话内
/// 语言不变就不重复解析，切换语言后自动重新判定。
/// </summary>
internal static class Locales
{
    private static string _code = "";
    // 游戏默认语言是英文：locale 完全读不到时按英文处理，避免非中文玩家看到中文文案。
    private static bool _zh;

    /// <summary>当前 locale code。读不到时沿用上一次成功的值（初始为空串）。</summary>
    internal static string Code
    {
        get
        {
            try
            {
                string code = LocHelper.GetCurrentLocaleCode() ?? "";
                if (code.Length > 0) _code = code;
            }
            catch { }
            return _code;
        }
    }

    /// <summary>
    /// 当前语言是否中文。拿不到 locale 时保持上一次结论；从未拿到过则按英文处理
    /// （游戏默认语言是英文，宁可少一次中文化，也不要给非中文玩家塞中文）。
    /// </summary>
    internal static bool IsChinese
    {
        get
        {
            string code = Code;
            if (code.Length == 0) return _zh;
            string c = code.ToLowerInvariant();
            _zh = c.StartsWith("zh", StringComparison.Ordinal)
                || c.Contains("chinese")
                || c.Contains("hans")
                || c.Contains("hant");
            return _zh;
        }
    }

    /// <summary>按当前语言取一条文案。</summary>
    internal static string L(string zh, string en) => IsChinese ? zh : en;
}
