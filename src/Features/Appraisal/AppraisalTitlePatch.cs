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
    private static bool _loggedOnce;        // 首次注入时打一条路径日志，方便排查"写错控件"
    private static bool _reapplyScheduled;  // 已在后续几帧跟踪标题
    private static int _lastValue;          // 本次鉴定的真实价值，供后续几帧重建标题

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

            int value = ResolveTrueValue(gameItem);
            if (value <= 0) return;

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

            _lastValue = value;
            string current = _titleTmp.text ?? "";
            // 我们没注入过、或游戏自己改写了标题 → 以当前文本为原文
            if (_applied.Length == 0 || current != _applied) _original = current;

            string desired = BuildTitle(_original, value);
            if (current == desired) return;

            ApplyTitle(_original, value);

            if (!_loggedOnce)
            {
                _loggedOnce = true;
                Log.Msg($"AppraisalTitle: 注入到 '{Path(_titleTmp.transform)}' → {desired}");
            }

            // 标题常由 TMPLocalizer 异步本地化：开面板当帧可能还是预制体里的英文，
            // 之后才换成中文。所以注入后要跟几帧，看过游戏的基线再重建一次。
            if (!_reapplyScheduled && insp != null)
            {
                _reapplyScheduled = true;
                try { insp.StartCoroutine(ReapplyNextFrame()); }
                catch { _reapplyScheduled = false; }
            }
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
    ///
    /// 后缀语言跟**当前 locale** 走。标题本身由 TMPLocalizer 异步本地化，开面板当帧可能
    /// 还是英文预制体文本，此时会出现「Appraisal Service 标价420」这种半中半英；
    /// InjectPostfix 会在随后几帧以游戏写好的基线重建，首帧的混合态不会留下。
    /// 旧版靠判断标题基线是否含 CJK 来选后缀，那只够中英两种语言。
    /// </summary>
    private static string BuildTitle(string original, int trueValue)
    {
        var (_, best) = AppraisalRanges.GamePerfect(trueValue);
        return Loc.T("title.appraisal_suffix", "{0} List {1}, best offer {2}", original, trueValue, best);
    }

    private static TextMeshProUGUI? FindTitle(Transform root)
    {
        string localized = "";
        try { localized = LocHelper.GetLocalizedUI("ui_inspection_panel_title") ?? ""; } catch { }
        // 先只认「正在显示」的标题：面板层级里常有同名但未激活的隐藏副本，
        // 写进副本会表现为"标题完全没变"。找不到再退回全量搜索。
        return FindTitle(root, localized, true) ?? FindTitle(root, localized, false);
    }

    private static TextMeshProUGUI? FindTitle(Transform t, string localized, bool activeOnly)
    {
        for (int i = 0; i < t.childCount; i++)
        {
            var child = t.GetChild(i);
            if (child == null) continue;

            var tmp = child.GetComponent<TextMeshProUGUI>();
            if (tmp != null && (!activeOnly || tmp.isActiveAndEnabled))
            {
                string txt = tmp.text ?? "";
                if ((localized.Length > 0 && txt.Contains(localized))
                    || txt.Contains("鉴定服务")
                    || txt.Contains("Appraisal Service")
                    || txt.Contains("Appraisal service"))
                    return tmp;
            }

            var found = FindTitle(child, localized, activeOnly);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>游戏没登记这类鉴定物时，使用原版精选公式或第三方显式提供的总价，
    /// 与 tooltip 保持同一来源，不通过采样反推定价。</summary>
    private static int ResolveTrueValue(GameItem item)
    {
        try
        {
            int? native = GameRead.TrueValue(item);
            if (native is int n && n > 0) return n;
        }
        catch { }
        try { if (ValueBreakdown.Analyze(item)?.Total is int curated && curated > 0) return curated; } catch { }
        try { return ThirdPartyProviders.Appraisal(item)?.Total ?? 0; } catch { return 0; }
    }

    /// <summary>把标题写成「基线 + 后缀」并记下来；<paramref name="baseText"/> 是最新的游戏原文。</summary>
    private static void ApplyTitle(string baseText, int value)
    {
        if (_titleTmp == null) return;
        string desired = BuildTitle(baseText, value);
        _original = baseText;
        _applied = desired;
        try { _titleTmp.text = desired; } catch { }
    }

    /// <summary>
    /// 开面板后的几帧内盯住标题：游戏/本地化组件把标题改回原文时，**以它的新文本为基线重建**，
    /// 而不是把我们那版（可能基于预制体英文）强塞回去。
    /// </summary>
    private static System.Collections.IEnumerator ReapplyNextFrame()
    {
        try
        {
            for (int i = 0; i < 5; i++)
            {
                yield return null;
                if (_titleTmp == null || _applied.Length == 0) yield break;

                bool active;
                try { active = _titleTmp.isActiveAndEnabled; } catch { active = false; }
                if (!active) yield break;

                string cur;
                try { cur = _titleTmp.text ?? ""; } catch { break; }
                if (cur == _applied) continue;

                ApplyTitle(cur, _lastValue);
            }
        }
        finally
        {
            _reapplyScheduled = false;
        }
    }

    private static string Path(Transform t)
    {
        try
        {
            var sb = new System.Text.StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
        catch { return "?"; }
    }
}
