using System;
using System.Text.RegularExpressions;

namespace ItemDetails;

/// <summary>
/// 文案里的复数写法：<c>{1|Tag|Tage}</c> 按第 1 个参数的数量选词，只输出词本身，数字照常写 <c>{1}</c>。
/// 词形顺序按 CLDR 类别：多数语言「单数|复数」，俄语、波兰语「one|few|many」；中日韩没有复数，不用这种写法。
/// 在 string.Format 之前替换，所以模板里其余占位符不受影响。
/// </summary>
internal static class Plural
{
    private static readonly Regex Token = new(@"\{(\d+)\|([^{}]*)\}", RegexOptions.CultureInvariant);

    /// <param name="lang">基础语言码（de、ru、pt…），不带地区。</param>
    internal static string Apply(string template, object[]? args, string lang)
    {
        if (template.IndexOf('|') < 0) return template;
        return Token.Replace(template, m =>
        {
            string[] forms = m.Groups[2].Value.Split('|');
            int index = int.Parse(m.Groups[1].Value);
            if (args == null || index >= args.Length || !TryCount(args[index], out long n))
                return forms[forms.Length - 1];
            return forms[Math.Min(Category(lang, n), forms.Length - 1)];
        });
    }

    /// <summary>词形序号：0 = one，1 = few（或其余语言的复数），2 = many。</summary>
    internal static int Category(string lang, long n)
    {
        n = Math.Abs(n);
        long mod10 = n % 10, mod100 = n % 100;
        bool few = mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14);
        switch (lang)
        {
            case "ru":
            case "uk":
                if (mod10 == 1 && mod100 != 11) return 0;
                return few ? 1 : 2;
            case "pl":
                if (n == 1) return 0;
                return few ? 1 : 2;
            case "fr":
            case "pt":
                return n <= 1 ? 0 : 1;
            case "zh":
            case "ja":
            case "ko":
                return 0;
            default:
                return n == 1 ? 0 : 1;
        }
    }

    private static bool TryCount(object? value, out long n)
    {
        switch (value)
        {
            case int i: n = i; return true;
            case long l: n = l; return true;
            case short s: n = s; return true;
            case byte b: n = b; return true;
            case uint u: n = u; return true;
            case double d when d == Math.Floor(d) && !double.IsInfinity(d): n = (long)d; return true;
            case float f when f == Math.Floor(f) && !float.IsInfinity(f): n = (long)f; return true;
            default: n = 0; return false;
        }
    }
}
