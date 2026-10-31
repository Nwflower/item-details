using System;
using UnityEngine;
using UnityEngine.Localization.Settings;

namespace ItemDetails;

/// <summary>
/// 把「会 JIT 到本地化相关类型」的 Harmony 装配推迟到游戏语言真正应用之后。
///
/// 背景（见 docs/experience/harmony-patch-jit-triggers-localization-ctor.md）：
///   Harmony 在 Patch 时会 Compile 目标方法，Mono 在 JIT 期间会执行目标方法引用到的类型的
///   静态构造。`GameItemElement.GetTooltipBasic` 的 IL 直接引用 `WaterFeatureHelper` /
///   `WaterHelper`，若在 `ModLoader.Awake`（`locale` 仍是默认 en）就打这个补丁，
///   `WaterFeatureHelper` 的六个 `*_DISPLAY` 会固化成英文，且随存档保存：
///     - 交易面板「品相」按 `ItemCondition.display` 显示英文；
///     - `WaterHelper.WillAcceptWater` 拿英文常量比中文动态名，水永远卖不出去。
///
/// 因此本类的唯一职责：等主菜单把玩家存档里的 locale 真正 `SelectedLocale` 之后再执行一次
/// 装配回调。判断条件先看 `InitializationOperation.IsDone`（此时读 `SelectedLocale` 不会
/// 触发 `WaitForCompletion`），再确认当前 locale 等于 `ResolveStartupLocale()`；超时则兜底执行，
/// 避免某个异常版本把模组整体卡死。
/// </summary>
internal sealed class DeferredPatchRunner : MonoBehaviour
{
    /// <summary>等待上限：超过后无论如何都执行一次，避免模块永远不装配。</summary>
    private const float MaxWaitSeconds = 30f;

    private Action? _pending;
    private string _label = "";
    private float _started;

    internal static void Schedule(string label, Action action)
    {
        try
        {
            var go = new GameObject("item_details_deferred_patch");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var runner = go.AddComponent<DeferredPatchRunner>();
            runner._pending = action;
            runner._label = label;
            runner._started = Time.realtimeSinceStartup;
            Log.Msg($"DeferredPatch: 等待语言就绪后装配「{label}」");
        }
        catch (Exception ex)
        {
            // 兜底：延迟器本身建不起来时，只能立即装配（至少不让功能消失）。
            Log.Warn("DeferredPatch.Schedule", ex);
            try { action(); } catch (Exception inner) { Log.Error("DeferredPatch.Fallback", inner); }
        }
    }

    internal static void Cancel()
    {
        try
        {
            foreach (var runner in Resources.FindObjectsOfTypeAll<DeferredPatchRunner>())
            {
                if (runner == null) continue;
                runner._pending = null;
                UnityEngine.Object.Destroy(runner.gameObject);
            }
        }
        catch { }
    }

    private void Update()
    {
        var action = _pending;
        if (action == null) return;

        bool ready;
        try { ready = LocaleReady(); }
        catch { ready = false; }

        if (!ready && Time.realtimeSinceStartup - _started < MaxWaitSeconds) return;

        _pending = null;
        try
        {
            action();
            Log.Msg($"DeferredPatch: 语言就绪，已装配「{_label}」（{Time.realtimeSinceStartup - _started:0.00}s）");
        }
        catch (Exception ex)
        {
            Log.Error("DeferredPatch.Apply." + _label, ex);
        }
        finally
        {
            try { Destroy(gameObject); } catch { }
        }
    }

    /// <summary>游戏是否已经把存档 locale 应用到 SelectedLocale。</summary>
    private static bool LocaleReady()
    {
        if (!LocalizationSettings.InitializationOperation.IsDone) return false;

        string code = LocHelper.GetCurrentLocaleCode();
        if (string.IsNullOrEmpty(code)) return false;

        string target = LocHelper.ResolveStartupLocale();
        if (string.IsNullOrEmpty(target)) return true;

        string a = Normalize(code);
        string b = Normalize(target);
        return a == b || a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal);
    }

    private static string Normalize(string code) => (code ?? "").Trim().ToLowerInvariant();
}
