using System;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 在鉴定服务面板标题后追加「标价&lt;真实价值&gt;，最优报价&lt;完美档上界&gt;」。
///
/// 不再常驻轮询：Postfix InspectionUIManager.OnInspectItem 时面板已经打开，
/// 直接在 appraisalPanel 子树里找一次标题；ResetUI / CloseUI 时恢复。
/// 标题控件按本地化键 ui_inspection_panel_title 匹配，避免写死中英文。
///
/// 恢复靠「记住注入前的原文」，而不是事后从字符串里剥离后缀：后者依赖我们自己
/// 拼的括号格式，游戏改文案或换语言就会剥离失败，后缀会一层层叠上去。
/// </summary>
internal static class AppraisalTitlePatch
{
    private const string HarmonyId = "item_details.appraisal_title";

    private static Harmony? _harmony;
    private static bool _patched;
    private static TextMeshProUGUI? _titleTmp;
    private static string _original = "";   // 注入前的原文
    private static string _applied = "";    // 我们写进去的文本（空 = 当前没有注入）

    internal static void Apply()
    {
        if (_patched) return;
        try
        {
            var inspect = AccessTools.Method(typeof(InspectionUIManager), "OnInspectItem",
                new[] { typeof(GameItem) });
            var reset = AccessTools.Method(typeof(InspectionUIManager), "ResetUI", Type.EmptyTypes);
            var close = AccessTools.Method(typeof(InspectionUIManager), "CloseUI", Type.EmptyTypes);
            if (inspect == null)
            {
                Log.Warn("Title.Apply", "未找到 OnInspectItem，标题真实价格跳过");
                return;
            }

            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(inspect,
                postfix: new HarmonyMethod(typeof(AppraisalTitlePatch), nameof(InspectPostfix)));
            if (reset != null)
                _harmony.Patch(reset, prefix: new HarmonyMethod(typeof(AppraisalTitlePatch), nameof(RestorePrefix)));
            if (close != null)
                _harmony.Patch(close, prefix: new HarmonyMethod(typeof(AppraisalTitlePatch), nameof(RestorePrefix)));

            _patched = true;
            Log.Msg("AppraisalTitlePatch: patched OnInspectItem/ResetUI/CloseUI");
        }
        catch (Exception ex)
        {
            Log.Error("Title.Apply", ex);
        }
    }

    internal static void Remove()
    {
        RestoreTitle();
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _patched = false;
    }

    private static void InspectPostfix(GameItem gameItem)
    {
        try
        {
            // 注意：不要在这里先 RestoreTitle。OnInspectItem 开头必定调用 ResetUI，
            // 其 prefix 已经把标题恢复成注入前原文；若这里再恢复一次，会把游戏刚
            // 按新语言写好的标题覆盖成旧语言的 _original，反而制造错误。
            if (!ModSettings.Value || gameItem == null) return;
            if (!GameRead.IsTag(gameItem, "APPRAISAL_ITEM")) return;

            int? trueVal = GameRead.TrueValue(gameItem);
            if (trueVal is not int value || value <= 0) return;

            var insp = InspectionUIManager.Instance;
            var panel = insp?.appraisalPanel;
            if (panel == null) return;

            // 面板可能被销毁重建：Unity 已销毁的对象只有 == null 判断才有效，
            // 因此这里不能用 ??=（它只看托管引用，会把已销毁的控件当活的）。
            if (_titleTmp == null)
            {
                _titleTmp = FindTitle(panel.transform);
                _applied = "";
                _original = "";
            }
            if (_titleTmp == null)
            {
                Log.Warn("Title.Find", "未找到鉴定服务标题控件");
                return;
            }

            string current = _titleTmp.text ?? "";
            // 我们没注入过、或游戏自己改写了标题 → 以当前文本为原文
            if (_applied.Length == 0 || current != _applied) _original = current;

            string desired = BuildTitle(_original, value);
            if (current == desired) return;

            _titleTmp.text = desired;
            _applied = desired;
        }
        catch (Exception ex)
        {
            Log.Warn("Title.Inspect", ex);
        }
    }

    private static void RestorePrefix() => RestoreTitle();

    private static void RestoreTitle()
    {
        if (_titleTmp == null || _applied.Length == 0) return;
        try { _titleTmp.text = _original; } catch { }
        _applied = "";
    }

    /// <summary>
    /// 「鉴定服务 标价&lt;真实价值&gt;，最优报价&lt;完美档上界&gt;」。
    /// 最优报价取完美档上界（真实价 ×105%，与游戏 RNG.CalculatePercentage 同样整数截断），
    /// 即顾客会给到的最高价。
    /// </summary>
    private static string BuildTitle(string original, int trueValue)
    {
        var (_, best) = AppraisalRanges.Perfect(trueValue);
        return Locales.IsChinese
            ? $"{original} 标价{trueValue}，最优报价{best}"
            : $"{original} List {trueValue}, best offer {best}";
    }

    private static TextMeshProUGUI? FindTitle(Transform root)
    {
        string localized = "";
        try { localized = LocHelper.GetLocalizedUI("ui_inspection_panel_title") ?? ""; } catch { }
        return FindTitle(root, localized);
    }

    private static TextMeshProUGUI? FindTitle(Transform t, string localized)
    {
        for (int i = 0; i < t.childCount; i++)
        {
            var child = t.GetChild(i);
            if (child == null) continue;

            var tmp = child.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
            {
                string txt = tmp.text ?? "";
                if ((localized.Length > 0 && txt.Contains(localized))
                    || txt.Contains("鉴定服务")
                    || txt.Contains("Appraisal Service")
                    || txt.Contains("Appraisal service"))
                    return tmp;
            }

            var found = FindTitle(child, localized);
            if (found != null) return found;
        }
        return null;
    }
}
