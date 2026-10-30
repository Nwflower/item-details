using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 顾客悬停卡：鼠标停在店内顾客身上时，显示**本次接待**已经说过的对话。
///
/// 数据来源（正式版 0.46D）：
///   DialogUIManager.UpdateDialogHistory(string) 是所有台词进入游戏录音机的唯一收口，
///   传进来的文本已带说话人：<c>&lt;color=#eb4034&gt;顾客名:&lt;/color&gt; 台词</c>、
///   <c>&lt;color=#6ecf5f&gt;Player:&lt;/color&gt; 台词</c>；换下一位顾客时收到单独的 "---"。
///   卡片是给当前这位顾客看的，说话人名字每行重复、没有信息量，记录时只留台词。
///
/// 归属：**只做本次接待**。收到 "---" 或 PlayerStore.currentClientInstance 换实例就清空，
/// 不按 identifier 归档、不写盘——玩家要的是"这人刚才说了什么"，不是跨天档案。
///
/// 显示：借用游戏自带的 CustomUIManager 悬停提示（跟鼠标、自动折行、360 宽上限），
/// 不另建窗口，也就不会盖住店内的对话气泡。
/// 命中判定读 Interactable.currentHoveringInteractable（悬停时游戏自己置位），
/// 通过 StoreUIManager.clientGameObject 认出顾客本体——不往顾客身上挂组件。
/// </summary>
internal static class CustomerHistory
{
    private const string HarmonyId = "item_details.customer_history";

    /// <summary>本次接待最多留多少行：台词是模板句，留太多只是重复。</summary>
    private const int KeepLines = 40;

    /// <summary>卡片里最多显示几行（再长会盖住半个屏幕）。</summary>
    private const int ShowLines = 8;

    private static Harmony? _harmony;
    private static bool _patched;
    private static GameObject? _go;

    private static readonly List<string> Lines = new();

    internal static void Install()
    {
        if (!_patched)
        {
            try
            {
                var target = AccessTools.Method(typeof(DialogUIManager), "UpdateDialogHistory",
                    new[] { typeof(string) });
                if (target == null)
                {
                    Log.Warn("CustomerHistory.Apply", "未找到 UpdateDialogHistory，顾客对话历史跳过");
                    return;
                }

                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(target, postfix: new HarmonyMethod(typeof(CustomerHistory), nameof(AfterDialogLine)));
                _patched = true;
                Log.Msg("CustomerHistory: patched UpdateDialogHistory");
            }
            catch (Exception ex)
            {
                Log.Error("CustomerHistory.Apply", ex);
            }
        }

        // 没打上补丁就不装轮询器：否则只会弹一张永远空白的卡片。
        if (!_patched || _go != null) return;

        try
        {
            _go = new GameObject("ItemDetails_CustomerHistory");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _go.AddComponent<CustomerHistoryTicker>();
        }
        catch (Exception ex)
        {
            Log.Error("CustomerHistory.Install", ex);
            if (_go != null) { try { UnityEngine.Object.Destroy(_go); } catch { } _go = null; }
        }
    }

    internal static void Stop()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _patched = false;
        Clear();

        try { CustomUIManager.Instance?.HideTooltip(null); } catch { }

        if (_go != null)
        {
            try { UnityEngine.Object.Destroy(_go); } catch { }
            _go = null;
        }
    }

    internal static void Clear()
    {
        try { Lines.Clear(); } catch { }
    }

    /// <summary>
    /// Postfix：每显示一句台词都会经过这里。
    /// 只认带说话人颜色的台词——"---" 分隔符和模组来源提示（&lt;i&gt;…&lt;/i&gt;）
    /// 都不该进历史；电话/广播对话没有店内顾客，也跳过。
    /// </summary>
    private static void AfterDialogLine(string text)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return;

            if (text == "---")
            {
                Clear();          // 游戏在每位顾客开场主对话前插分隔符：新一轮接待
                return;
            }

            if (!text.StartsWith("<color=", StringComparison.Ordinal)) return;
            if (!HasClient()) return;
            if (!ModSettings.CustomerHistory) return;   // 关掉开关就别再攒了

            Lines.Add(WithoutSpeaker(text));
            if (Lines.Count > KeepLines) Lines.RemoveRange(0, Lines.Count - KeepLines);
        }
        catch (Exception ex)
        {
            Log.Warn("CustomerHistory.Record", ex);
        }
    }

    /// <summary>
    /// 去掉行首的说话人：<c>&lt;color=#eb4034&gt;顾客名:&lt;/color&gt; 台词</c> → <c>台词</c>。
    /// 只匹配固定格式的前缀，格式变了就原样保留，不会误伤台词里的颜色标记。
    /// </summary>
    private static string WithoutSpeaker(string line)
    {
        var match = SpeakerPrefix.Match(line);
        return match.Success ? line.Substring(match.Length) : line;
    }

    private static readonly Regex SpeakerPrefix =
        new(@"^<color=#[0-9A-Fa-f]{6,8}>[^<]*[:：]</color> ?", RegexOptions.Compiled);

    private static bool HasClient()
    {
        try { return PlayerStore.Instance != null && PlayerStore.Instance.currentClientInstance != null; }
        catch { return false; }
    }

    /// <summary>拼卡片正文；每次悬停都重新取，顾客换人后内容立刻跟着换。</summary>
    internal static string BuildCard()
    {
        var sb = new StringBuilder(512);
        sb.Append("<color=#FFD166>").Append(ClientName()).Append("</color>");

        if (Lines.Count == 0)
        {
            sb.Append('\n').Append(Loc.T("history.empty", "No dialogue yet this visit."));
            return sb.ToString();
        }

        int shown = Math.Min(ShowLines, Lines.Count);
        int hidden = Lines.Count - shown;
        if (hidden > 0)
            sb.Append('\n').Append(Loc.T("history.earlier", "…{0} earlier lines", hidden));

        for (int i = Lines.Count - shown; i < Lines.Count; i++)
            sb.Append('\n').Append(Lines[i]);

        return sb.ToString();
    }

    private static string ClientName()
    {
        try
        {
            var blueprint = PlayerStore.Instance?.currentClientInstance?.GetClientBlueprint();
            string name = blueprint?.displayName ?? "";
            if (name.Length > 0) return name;
        }
        catch { }
        return Loc.T("history.unknown", "Customer");
    }
}

/// <summary>
/// 悬停轮询器：顾客提示的 Toast 是 0.5 秒一轮，悬停必须跟手，这里用 0.2 秒。
/// 顾客换人时清空历史，兜住"新顾客没有主对话、游戏没插分隔符"的情况。
/// </summary>
internal sealed class CustomerHistoryTicker : MonoBehaviour
{
    private const float PollInterval = 0.2f;

    private float _timer;
    private StoreClientInstance? _instance;
    private string _shown = "";
    private float _reshowAt;

    private void Update()
    {
        _timer += Time.deltaTime;
        if (_timer < PollInterval) return;
        _timer = 0f;

        try
        {
            if (!ModSettings.CustomerHistory)
            {
                Hide();
                return;
            }

            TrackClientChange();

            if (IsHoveringClient()) Show();
            else Hide();
        }
        catch (Exception ex)
        {
            Log.Warn("CustomerHistory.Tick", ex);
        }
    }

    /// <summary>换了顾客实例就重来一遍，避免上一位的台词挂在下一位头上。</summary>
    private void TrackClientChange()
    {
        StoreClientInstance? current = null;
        try { current = PlayerStore.Instance?.currentClientInstance; } catch { }

        if (ReferenceEquals(current, _instance)) return;
        _instance = current;
        CustomerHistory.Clear();
    }

    private static bool IsHoveringClient()
    {
        try
        {
            if (!Interactable.isHoveringInteractable) return false;

            var hovered = Interactable.currentHoveringInteractable;
            var client = StoreUIManager.Instance?.clientGameObject;
            if (hovered == null || client == null) return false;

            Transform target = client.transform;
            Transform hit = hovered.transform;
            return hit == target || hit.IsChildOf(target) || target.IsChildOf(hit);
        }
        catch { return false; }
    }

    private void Show()
    {
        try
        {
            var ui = CustomUIManager.Instance;
            if (ui == null) return;

            string text = CustomerHistory.BuildCard();

            // 内容没变就不重建排版；但每秒重摆一次——别的悬停元素收起时
            // 可能顺手把共享 tooltip 关了，玩家还停在顾客身上却什么都没了。
            float now = Time.unscaledTime;
            if (text == _shown && now < _reshowAt) return;

            ui.ShowTooltip(text, null);
            _shown = text;
            _reshowAt = now + 1f;
        }
        catch (Exception ex)
        {
            Log.Warn("CustomerHistory.Show", ex);
        }
    }

    private void Hide()
    {
        if (_shown.Length == 0) return;

        string shown = _shown;
        _shown = "";

        try
        {
            var ui = CustomUIManager.Instance;
            if (ui == null) return;

            // 只在气泡里还是我们写的那段文字时收起：鼠标可能已经从顾客身上
            // 移到别的 CustomUI 悬停元素，那边刚把自己的 tooltip 摆上来，
            // 这时 HideTooltip 会把别人的内容一起关掉。
            var tip = GameObject.Find("custom_ui_tooltip");
            var tmp = tip != null ? tip.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            if (tmp != null && tmp.text != shown) return;

            // 传 null 而不是自己的 owner：游戏的 HideTooltip(owner) 只在 owner 不是当前
            // tooltipOwner 时才收起，用自己的触发器反而关不掉。
            ui.HideTooltip(null);
        }
        catch { }
    }
}
