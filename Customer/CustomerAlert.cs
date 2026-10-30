using System;
using System.Collections;              // IEnumerator：协程返回值
using System.Collections.Generic;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 顾客到店时的 Toast 提示：通缉犯 / 改造体。
///
/// 数据来源（正式版 0.46D，均为 StoreClient 上的直接字段，比声望推算可靠）：
///   StoreClient.isWanted        bool
///   StoreClient.reportReward    int     举报可得奖励
///   StoreClient.isAug           bool
///   StoreClient.displayName     string
///
/// 载体：游戏自带 CustomUIManager/CustomUIBuilder（无内置 Toast API），
/// 自绘一个 overlay 小窗，**常驻到该顾客离开**（PlayerStore.currentClientInstance 被置空时收起），
/// 无动效、不抢焦点。
///
/// 位置：默认屏幕正上方居中；玩家可以用标题栏拖动，位置记在
/// <see cref="_sessionPos"/> 里——**只活在本次进程内，不写盘**，重启游戏回到正上方。
///
/// 触发：轮询 PlayerStore.Instance.currentClientInstance，实例变化且非空时评估一次。
/// </summary>
internal sealed class CustomerAlertTicker : MonoBehaviour
{
    private const string WindowId = "item_details.customer_alert";
    private const float PollInterval = 0.5f;
    /// <summary>提示常驻到顾客离开；这是兜底上限，避免极端情况下窗口一直挂到下一局。</summary>
    private const float MaxToastSeconds = 300f;
    private const float ToastWidth = 360f;
    private const float ToastHeight = 88f;
    private const float ToastTopMargin = 28f;

    /// <summary>
    /// 本次进程内记住的提示位置（父节点坐标系里的 anchoredPosition）。
    /// 刻意**不持久化**：用户要求「重启游戏失效」，所以不进 ModData、不读盘；
    /// 换分辨率/窗口大小后由 <see cref="PlaceAtSavedOrTopCenter"/> 重新夹回屏幕内。
    /// </summary>
    private static Vector2? _sessionPos;

    /// <summary>
    /// 最近一次由我们摆上去的位置。用来判断玩家到底动没动过：
    /// 没动过就不更新 <see cref="_sessionPos"/>，这样「默认正上方」不会被
    /// 上一次提示的高度差异污染（换了行数后仍精确贴顶）。
    /// </summary>
    private static Vector2? _appliedPos;

    /// <summary>标题警示色（TMP 富文本）。</summary>
    private const string AlertColor = "#FF6B6B";

    private float _timer;
    private StoreClientInstance? _lastInstance;
    private StoreClientInstance? _attemptInstance;
    private int _attempts;
    private int _toastVersion;
    private bool _shown;
    private static GameObject? _go;

    /// <summary>同一顾客最多尝试提示几次；仍失败就放弃，避免每 0.5 秒重试刷日志。</summary>
    private const int MaxAttempts = 3;

    internal static void Install()
    {
        if (_go != null) return;
        try
        {
            _go = new GameObject("ItemDetails_CustomerAlert");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _go.AddComponent<CustomerAlertTicker>();
            Log.Msg("CustomerAlertTicker installed");
        }
        catch (Exception ex)
        {
            Log.Error("CustomerAlert.Install", ex);
            if (_go != null) { try { UnityEngine.Object.Destroy(_go); } catch { } _go = null; }
        }
    }

    internal static void Stop()
    {
        // 关窗会销毁节点：先把玩家拖到的位置记进本次会话，重新启用时不必从头摆
        RememberWindowPosition();
        try
        {
            var ui = CustomUIManager.Instance;
            if (ui != null && ui.IsOpen(WindowId)) ui.CloseWindow(WindowId);
        }
        catch { }
        if (_go != null)
        {
            try { UnityEngine.Object.Destroy(_go); } catch { }
            _go = null;
        }
    }

    private void Update()
    {
        if (!ModSettings.CustomerAlert)
        {
            if (_shown) { try { Dismiss(); } catch { } }   // 关掉开关后别把提示留在屏幕上
            return;
        }

        _timer += Time.deltaTime;
        if (_timer < PollInterval) return;
        _timer = 0f;

        try { Tick(); }
        catch (Exception ex) { Log.Warn("CustomerAlert.Tick", ex); }
    }

    private void Tick()
    {
        StoreClientInstance? instance = null;
        try
        {
            var store = PlayerStore.Instance;
            if (store == null) return;
            instance = store.currentClientInstance;
        }
        catch { return; }

        if (instance == null)
        {
            Dismiss();   // 顾客离开：提示随之收起
            return;
        }

        // 同一个顾客实例只提示一次
        if (ReferenceEquals(instance, _lastInstance)) return;

        StoreClient? blueprint = null;
        try { blueprint = instance.GetClientBlueprint(); } catch (Exception ex) { Log.Warn("CustomerAlert.GetBlueprint", ex); }
        if (blueprint == null) return;   // 读取瞬时失败：下个轮询重试

        if (!ReferenceEquals(instance, _attemptInstance))
        {
            _attemptInstance = instance;
            _attempts = 0;
        }

        if (Evaluate(blueprint))
        {
            _lastInstance = instance;   // 只有真正处理成功才标记，避免 UI 未就绪时永久漏提示
            _attempts = 0;
            return;
        }

        _attempts++;
        if (_attempts >= MaxAttempts)
        {
            _lastInstance = instance;
            _attempts = 0;
            Log.Warn("CustomerAlert.GiveUp", "提示连续失败，已放弃该顾客");
        }
    }

    /// <summary>返回 true 表示该顾客已处理完毕（无需提示，或提示已成功显示）。</summary>
    private bool Evaluate(StoreClient client)
    {
        bool wanted = false, aug = false;
        string name = "";
        int reward = 0;

        try { wanted = client.isWanted; } catch (Exception ex) { Log.Warn("CustomerAlert.isWanted", ex); }
        try { aug = client.isAug; } catch (Exception ex) { Log.Warn("CustomerAlert.isAug", ex); }


        // 潜行通缉：游戏自己刻意隐藏（不写"今日有通缉犯到访"、不上报纸），
        // 我们跟着隐藏，避免剧透。
        if (wanted)
        {
            try { if (client.isStealthWanted) wanted = false; } catch { }
        }
        try { name = client.displayName ?? ""; } catch { }
        try { reward = client.reportReward; } catch { }

        // reportReward 是「未修正」的原始值，游戏在发放时才套起始 perk 修正：
        //   HandleReportRewardConvict → HandleReportRewardModelCitizen（见 PlayerStore 汇报流程）
        // 直接显示原值会与玩家实际拿到的钱不符，这里先算成实际奖励。
        int payout = reward;
        try
        {
            payout = StartingPerkContent.HandleReportRewardConvict(reward);
            payout = StartingPerkContent.HandleReportRewardModelCitizen(payout);
        }
        catch (Exception ex) { Log.Warn("CustomerAlert.PerkPayout", ex); payout = reward; }

        if (!wanted && !aug)
        {
            Dismiss();          // 换了位普通顾客：上一位的提示不能留着
            return true;
        }

        string title;
        if (wanted && aug) title = Loc.T("alert.title_both", "Wanted · Augmented customer");
        else if (wanted) title = Loc.T("alert.title_wanted", "Wanted");
        else title = Loc.T("alert.title_augmented", "Augmented customer");

        string body = string.IsNullOrEmpty(name) ? "" : name;
        if (wanted && payout > 0)
            body += Loc.T("alert.bounty", " (bounty {0})", payout);

        // 透视补充：通缉犯真名（原版举报后才揭示）、改造体击杀奖励与威胁值
        var extra = new List<string>();
        if (wanted)
        {
            try
            {
                string real = client.realName ?? "";
                if (real.Length > 0 && real != name)
                    extra.Add(Loc.T("alert.real_name", "Real name: {0}", real));
            }
            catch { }
        }
        if (aug)
        {
            // 击杀奖励：复刻 AugHelper.OnGoodKill 的算法（天数递增 100→250，再套起始 perk 的
            // 定罪/模范市民修正与「改造体危机」1.25 倍）。展示的是实际入账值，不含声望。
            try
            {
                int kill = AugKillReward();
                if (kill > 0) extra.Add(Loc.T("alert.aug_kill_reward", "Kill reward: {0}", kill));
            }
            catch (Exception ex) { Log.Warn("CustomerAlert.AugKillReward", ex); }

            // 改造体威胁：PlayerStore.augPresence，初始 15、击杀下调、放走上调，满 100 在高难度
            // 触发结局；危机结束的判据也看它是否归零。
            try
            {
                int presence = Mathf.Clamp(PlayerStore.Instance.augPresence, 0, AugPresenceMax);
                extra.Add(Loc.T("alert.aug_presence", "Aug threat: {0}/100", presence));
            }
            catch (Exception ex) { Log.Warn("CustomerAlert.AugPresence", ex); }
        }

        return ShowToast(title, body, extra);
    }

    /// <summary>改造体威胁值的上限：满值在高难度下触发结局，危机结束的判据也看它归零。</summary>
    private const int AugPresenceMax = 100;

    /// <summary>
    /// 击杀改造体的实际入账奖励，算法与 <c>AugHelper.OnGoodKill</c> 一致：
    /// 基准 100 + (当天 − 改造体引入日)，夹在 100–250；再套起始 perk 的
    /// 定罪（×0.75）/ 模范市民（×1.5）修正；「改造体危机」perk 再 ×1.25。
    /// </summary>
    private static int AugKillReward()
    {
        int day = 0;
        try { day = StoreStation.Instance.dayCounter; } catch { }

        int amount = Mathf.Clamp(100 + (day - PlayerStore.GetAugIntroducedDay()), 100, 250);
        try { amount = StartingPerkContent.HandleReportRewardConvict(amount); } catch { }
        try { amount = StartingPerkContent.HandleReportRewardModelCitizen(amount); } catch { }
        try { if (StartingPerk.IsAugMenace()) amount = RNG.MultiplyAndRound(amount, 1.25f); } catch { }
        return amount;
    }

    /// <summary>返回 true 表示提示已成功显示（或无需显示）。</summary>
    private bool ShowToast(string title, string body, List<string> extra)
    {
        try
        {
            var ui = CustomUIManager.Instance;
            if (ui == null) return false;   // UI 尚未就绪：留给下一次轮询

            // 重建窗口会销毁旧节点，位置要在那之前抓下来
            RememberWindowPosition();
            try { if (ui.IsOpen(WindowId)) ui.CloseWindow(WindowId); } catch { }

            // 标题直接承载警示文字并标红（titleText 是 TextMeshProUGUI，支持富文本）。
            // 注意：CreateWindow 只在 title != null 时才会 SetTitle，传 null 会保留预制体默认的 "Title"。
            string coloredTitle = $"<color={AlertColor}>{title}</color>";

            var b = ui.CreateWindow(WindowId, coloredTitle, "overlay")
                      .SetDraggable(true)
                      .SetCloseOnEscape(false);
            if (!string.IsNullOrEmpty(body)) b.AddLabel(body);
            foreach (string line in extra) b.AddLabel(line);

            // 每多一行文案就多 18 像素；定位必须用同一个高度，否则多行提示会贴边
            float height = ToastHeight + extra.Count * 18f;
            var win = b.SetSize(ToastWidth, height).Show();
            if (win == null) return false;

            PlaceAtSavedOrTopCenter(win, height);
            _shown = true;

            // 常驻：由「顾客离开」触发关闭；这里只留一个兜底上限，防止极端情况下窗口一直挂着。
            // 新提示会递增版本；旧计时器到期时不得关闭后来出现的新提示。
            int version = ++_toastVersion;
            StartCoroutine(AutoHideAfter(MaxToastSeconds, version));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("CustomerAlert.ShowToast", ex);
            return false;
        }
    }

    /// <summary>
    /// 默认放在屏幕**正上方居中**；玩家拖动过之后沿用本次会话记住的位置。
    ///
    /// 坐标系与 <c>CustomUIWindow.SetPosition</c> / <c>UIDragHandler</c> 一致：
    /// 窗口父节点的中心为原点，单位是参考分辨率像素。
    /// 每次显示都重新夹一次，防三件事：换分辨率、提示行数变化、玩家把窗口拖出屏幕外。
    /// </summary>
    private void PlaceAtSavedOrTopCenter(CustomUIWindow win, float height)
    {
        try
        {
            GetParentSize(win, out float w, out float h);

            Vector2 pos = _sessionPos is Vector2 saved
                ? saved
                : new Vector2(0f, h * 0.5f - height * 0.5f - ToastTopMargin);

            Vector2 final = ClampInsideParent(pos, w, h, ToastWidth, height);
            win.SetPosition(final);
            _appliedPos = final;
        }
        catch (Exception ex)
        {
            Log.Warn("CustomerAlert.Place", ex);
            try { win.Center(); } catch { }
        }
    }

    /// <summary>窗口父节点的可用尺寸；取不到就退回屏幕像素。</summary>
    private static void GetParentSize(CustomUIWindow win, out float w, out float h)
    {
        var parent = win.Rect != null ? win.Rect.parent as RectTransform : null;
        if (parent != null && parent.rect.width > 1f && parent.rect.height > 1f)
        {
            w = parent.rect.width;
            h = parent.rect.height;
            return;
        }
        w = Screen.width;
        h = Screen.height;
    }

    /// <summary>把中心坐标夹到「整窗仍在父节点内」的范围，避免拖出去之后抓不回来。</summary>
    private static Vector2 ClampInsideParent(Vector2 pos, float w, float h, float width, float height)
    {
        float maxX = Mathf.Max(0f, (w - width) * 0.5f);
        float maxY = Mathf.Max(0f, (h - height) * 0.5f);
        return new Vector2(Mathf.Clamp(pos.x, -maxX, maxX), Mathf.Clamp(pos.y, -maxY, maxY));
    }

    /// <summary>
    /// 把窗口当前所在位置记进本次会话——**仅当玩家真的拖动过**。
    /// 窗口即将被销毁时调用；没拖过就保持原样，下次仍精确回到正上方。
    /// </summary>
    private static void RememberWindowPosition()
    {
        try
        {
            var ui = CustomUIManager.Instance;
            if (ui == null || !ui.IsOpen(WindowId)) return;

            var win = ui.GetWindow(WindowId);
            if (win == null || !win.IsAlive || win.Rect == null) return;

            Vector2 current = win.Rect.anchoredPosition;
            // 1 像素以内视为「还是我们摆的位置」，不算玩家拖动
            if (_appliedPos is Vector2 applied
                && (current - applied).sqrMagnitude <= 1f)
                return;

            _sessionPos = current;
        }
        catch (Exception ex)
        {
            Log.Warn("CustomerAlert.RememberPos", ex);
        }
    }


    /// <summary>收起提示并重置「当前顾客」状态（顾客离开、换成普通顾客、关闭模组时都会走到）。</summary>
    private void Dismiss()
    {
        _lastInstance = null;
        _attemptInstance = null;
        _attempts = 0;
        if (!_shown) return;
        _shown = false;

        // 关窗会销毁节点，玩家拖动过的位置得先记住（仅本次进程内有效）
        RememberWindowPosition();

        try
        {
            var ui = CustomUIManager.Instance;
            if (ui != null && ui.IsOpen(WindowId)) ui.CloseWindow(WindowId);
        }
        catch { }
    }

    /// <summary>兜底关闭：正常路径应在顾客离开时立刻收起，这里只防窗口挂到下一局。</summary>
    private IEnumerator AutoHideAfter(float seconds, int version)
    {
        yield return new WaitForSeconds(seconds);
        if (version != _toastVersion) yield break;
        Dismiss();
    }
}
