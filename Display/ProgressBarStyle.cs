using System;
using System.Text;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 把原版 tooltip 里的 `[=====     ]` 文字进度条换成一排 `█`：
/// 已填部分用调用方给的前景色，未填部分用它给的暗色（原版每处都传了 VeryDarkCyan 之类的背景色，
/// 但 <c>AddProgressBar</c> 里把背景色强制清成 None，等于没用上）。
///
/// 不跳过 <c>AddProgressBar</c>：原方法照常拼串、Add、再把这一段标成不折行；这里只在它
/// 调用 Add 的那一刻换掉文本。所以不折行标记、tooltip 换行宽度和其它模组对 AddProgressBar
/// 的补丁都保留。原方法传给 Add 的文本不是 `[` 开头 `]` 结尾时（被别人改过）原样放过。
///
/// 字形用 U+2588：原版自己的背景色层就是用它铺底（RichTextBuilder.TextSlice.GetFillString），
/// tooltip 字体里一定有这个字。
/// </summary>
internal static class ProgressBarStyle
{
    private const string HarmonyId = "item_details.progress_bar";
    private const char Block = '█';
    /// <summary>调用方没给暗色时，未填部分用前景色加这个透明度。</summary>
    private const string DimAlpha = "<alpha=#40>";

    private static Harmony? _harmony;
    private static bool _patched;

    /// <summary>AddProgressBar 正在构建的那一条；只在它执行期间非空，由它里面唯一一次 Add 消费。</summary>
    private static Pending? _pending;

    private sealed class Pending
    {
        internal float Ratio;
        internal int Length;
        internal bool FullLength;
        internal RenderHandler.ColorPalette Dark;
    }

    internal static void Apply()
    {
        if (_patched) return;
        try
        {
            var bar = AccessTools.Method(typeof(RichTextBuilder), "AddProgressBar", new[]
            {
                typeof(float), typeof(int), typeof(RenderHandler.ColorPalette), typeof(RenderHandler.ColorPalette),
                typeof(char[]), typeof(string), typeof(bool), typeof(bool)
            });
            var add = AccessTools.Method(typeof(RichTextBuilder), "Add", new[]
            {
                typeof(string), typeof(RenderHandler.ColorPalette), typeof(string), typeof(bool),
                typeof(bool), typeof(bool), typeof(bool), typeof(RenderHandler.ColorPalette),
                typeof(RenderHandler.ColorPalette), typeof(RenderHandler.ColorPalette)
            });
            if (bar == null || add == null)
            {
                Log.Warn("ProgressBar.Apply", "未找到 RichTextBuilder.AddProgressBar/Add，进度条保持原版");
                return;
            }

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(bar,
                prefix: new HarmonyMethod(typeof(ProgressBarStyle), nameof(BeforeBar)),
                finalizer: new HarmonyMethod(typeof(ProgressBarStyle), nameof(AfterBar)));
            _harmony.Patch(add, prefix: new HarmonyMethod(typeof(ProgressBarStyle), nameof(AddPrefix)));
            _patched = true;
            Log.Msg("ProgressBarStyle patched RichTextBuilder.AddProgressBar/Add");
        }
        catch (Exception ex)
        {
            Log.Error("ProgressBar.Apply", ex);
        }
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _patched = false;
        _pending = null;
    }

    private static void BeforeBar(float __0, int __1, RenderHandler.ColorPalette __3, bool __6)
    {
        _pending = ModSettings.BlockProgressBar && __1 > 0
            ? new Pending { Ratio = __0, Length = __1, Dark = __3, FullLength = __6 }
            : null;
    }

    private static void AfterBar()
    {
        _pending = null;
    }

    private static void AddPrefix(ref string __0)
    {
        var p = _pending;
        if (p == null) return;
        _pending = null;   // 只换 AddProgressBar 里那一次 Add

        try
        {
            string text = __0;
            if (text == null || text.Length < 2 || text[0] != '[' || text[text.Length - 1] != ']') return;
            __0 = Render(p);
        }
        catch (Exception ex)
        {
            Log.Warn("ProgressBar.Render", ex);
        }
    }

    /// <summary>格数与原版一致：已填 = floor(比例 × 长度)。</summary>
    private static string Render(Pending p)
    {
        float ratio = float.IsFinite(p.Ratio) ? Math.Clamp(p.Ratio, 0f, 1f) : 0f;
        int filled = (int)(ratio * p.Length);
        int empty = p.FullLength ? p.Length - filled : 0;

        var sb = new StringBuilder(p.Length + 32);
        if (filled > 0) sb.Append(Block, filled);
        if (empty > 0)
        {
            string rest = new string(Block, empty);
            sb.Append(p.Dark != RenderHandler.ColorPalette.None
                ? RenderHandler.ApplyRichTextColorTag(rest, p.Dark)
                : DimAlpha + rest + "<alpha=#FF>");
        }
        return sb.ToString();
    }
}
