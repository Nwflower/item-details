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
/// 自绘一个 overlay 小窗，若干秒后自动关闭。
///
/// 触发：轮询 PlayerStore.Instance.currentClientInstance，实例变化且非空时评估一次。
/// </summary>
internal sealed class CustomerAlertTicker : MonoBehaviour
{
    private const string WindowId = "item_details.customer_alert";
    private const float PollInterval = 0.5f;
    private const float ToastSeconds = 6f;
    private const float ToastWidth = 360f;
    private const float ToastHeight = 88f;
    private const float ToastTopMargin = 28f;

    /// <summary>标题警示色（TMP 富文本）。</summary>
    private const string AlertColor = "#FF6B6B";

    private float _timer;
    private StoreClientInstance? _lastInstance;
    private StoreClientInstance? _attemptInstance;
    private int _attempts;
    private int _toastVersion;
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
        if (!ModSettings.CustomerAlert) return;   // 关掉后连轮询都不必做

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
            _lastInstance = null;
            _attemptInstance = null;
            _attempts = 0;
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

        if (!wanted && !aug) return true;   // 无需提示：算处理完毕，避免反复评估

        string title;
        if (wanted && aug) title = L("通缉犯 · 改造体顾客", "Wanted · Augmented client");
        else if (wanted) title = L("通缉犯", "Wanted");
        else title = L("改造体顾客", "Augmented client");

        string body = string.IsNullOrEmpty(name) ? "" : name;
        if (wanted && payout > 0)
            body += L($"（举报奖励 {payout}）", $" (bounty {payout})");

        // 透视补充：通缉犯真名（原版举报后才揭示）与改造体等级
        var extra = new List<string>();
        if (wanted)
        {
            try
            {
                string real = client.realName ?? "";
                if (real.Length > 0 && real != name)
                    extra.Add(L($"真名：{real}", $"Real name: {real}"));
            }
            catch { }
        }
        if (aug)
        {
            try
            {
                int tier = client.augTier;
                if (tier > 0) extra.Add(L($"改造体等级：{tier}", $"Augmentation tier: {tier}"));
            }
            catch { }
        }

        return ShowToast(title, body, extra);
    }

    /// <summary>返回 true 表示提示已成功显示（或无需显示）。</summary>
    private bool ShowToast(string title, string body, List<string> extra)
    {
        try
        {
            var ui = CustomUIManager.Instance;
            if (ui == null) return false;   // UI 尚未就绪：留给下一次轮询

            try { if (ui.IsOpen(WindowId)) ui.CloseWindow(WindowId); } catch { }

            // 标题直接承载警示文字并标红（titleText 是 TextMeshProUGUI，支持富文本）。
            // 注意：CreateWindow 只在 title != null 时才会 SetTitle，传 null 会保留预制体默认的 "Title"。
            string coloredTitle = $"<color={AlertColor}>{title}</color>";

            var b = ui.CreateWindow(WindowId, coloredTitle, "overlay")
                      .SetDraggable(false)
                      .SetCloseOnEscape(false);
            if (!string.IsNullOrEmpty(body)) b.AddLabel(body);
            foreach (string line in extra) b.AddLabel(line);

            // 每多一行文案就多 18 像素；定位必须用同一个高度，否则多行提示会贴边
            float height = ToastHeight + extra.Count * 18f;
            var win = b.SetSize(ToastWidth, height).Show();
            if (win == null) return false;

            PlaceTopRight(win, height);

            // 新提示会递增版本；旧协程到期时不得关闭后来出现的新提示。
            int version = ++_toastVersion;
            StartCoroutine(CloseAfter(ToastSeconds, version));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("CustomerAlert.ShowToast", ex);
            return false;
        }
    }

    /// <summary>
    /// 放到屏幕右上角，避开三处容易被遮挡的区域：
    ///   中间＝顾客立绘，下方＝柜台货物区，顾客头顶＝对话气泡。
    /// 用父节点的 rect 计算，兼容 CanvasScaler 的参考分辨率。
    /// </summary>
    private void PlaceTopRight(CustomUIWindow win, float height)
    {
        try
        {
            float w, h;
            var parent = win.Rect != null ? win.Rect.parent as RectTransform : null;
            if (parent != null && parent.rect.width > 1f && parent.rect.height > 1f)
            {
                w = parent.rect.width;
                h = parent.rect.height;
            }
            else
            {
                w = Screen.width;
                h = Screen.height;
            }

            float x = w * 0.5f - ToastWidth * 0.5f - ToastTopMargin;
            float y = h * 0.5f - height * 0.5f - ToastTopMargin;
            win.SetPosition(new Vector2(x, y));
        }
        catch (Exception ex)
        {
            Log.Warn("CustomerAlert.PlaceTopRight", ex);
            try { win.Center(); } catch { }
        }
    }

    private static string L(string zh, string en) => Locales.L(zh, en);

    private IEnumerator CloseAfter(float seconds, int version)
    {
        yield return new WaitForSeconds(seconds);
        if (version != _toastVersion) yield break;
        try
        {
            var ui = CustomUIManager.Instance;
            if (ui != null && ui.IsOpen(WindowId)) ui.CloseWindow(WindowId);
        }
        catch { }
    }
}
