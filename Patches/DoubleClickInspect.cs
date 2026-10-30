using System;
using HarmonyLib;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 体验优化：**双击待鉴定物品直接打开鉴定面板**，不必先走到放大镜再拖动。
/// 面板已打开时，双击另一件待鉴定物品会直接切换，不必先关闭面板。
///
/// 实测结论（0.46D）：普通模式下直接调
/// `InspectionUIManager.Instance.OnInspectItem(item)` 面板就能正常显示，
/// **不需要**先 `StoreUIManager.EnterMagnifyingMode()`——那条路会
/// `SetImmediatlyArrive()` 拉顾客、切相机、放黑屏过场，观感差异明显。
/// 因此这里只走最轻的一条：直接开面板。
///
/// 提示文案：原版 `AppraisalHelper.CreateAppraisalTooltip` / `InspectableHelper.CreateInspectableTooltip`
/// 照常执行，它们写出的「可使用放大镜进行检查」两行在 TooltipPipeline 的 AddLine 前缀里
/// 换成双击提示（同一个 tooltip 只留一行）；原版一行都没写时，由两个方法的后缀补上。
/// 不跳过原方法：devMode 的 TRUE VALUE 行与其它模组对这两个方法的补丁都保留。
/// 写法照抄原版容器的「双击以打开」（`ContainerHelper.CreateContainerTooltip`：
/// 前导空格 + 浅蓝色），让同一类“双击做某事”的提示在 tooltip 里长得一样。
///
/// 只读 + 调用游戏自己的公开方法，不改存档。
///
/// 例外：**已购买**（`IS_OWNED_TAG`）的鉴定物不接管双击。原版双击对这些物品还会
/// 执行 ActivateSlotItem（食品券兑换食品额度就是这条），我们的前缀一旦拦住双击，
/// 兑换就按不出来了。已购买物品维持原版的放大镜流程。
/// </summary>
internal static class DoubleClickInspect
{
    private const string HarmonyId = "item_details.double_click";
    private static Harmony? _harmony;
    private static bool _patched;
    private static GameObject? _go;

    /// <summary>已写过双击提示的 tooltip 构建序号（<see cref="TooltipPipeline.BuildId"/>）。</summary>
    private static int _hintBuild = -1;

    // 原版两行放大镜提示的当前语言原文，用于在 AddLine 里精确认出它们
    private static string _hintLocale = "";
    private static string? _appraisalTip;
    private static string? _inspectableTip;

    internal static void Apply()
    {
        if (_patched) return;
        try
        {
            _harmony = new Harmony(HarmonyId);

            // 两个提示来源都只挂后缀：原版没写放大镜提示的可检视物品（如没有 INSPECTABLE_TAG
            // 的真伪鉴定物品），在这里补一行双击提示。
            // 两处 AccessTools.Method 必须直接写 typeof(...)：类型来自变量时，官方扫描器
            // 认不出目标，会判为 reflection-external，而 manifest 没声明就拒绝启用整个模组。
            PatchHintSource(AccessTools.Method(typeof(AppraisalHelper), "CreateAppraisalTooltip",
                new[] { typeof(RichTextBuilder), typeof(GameItem) }), "AppraisalHelper.CreateAppraisalTooltip");
            PatchHintSource(AccessTools.Method(typeof(InspectableHelper), "CreateInspectableTooltip",
                new[] { typeof(RichTextBuilder), typeof(GameItem) }), "InspectableHelper.CreateInspectableTooltip");

            // 复用游戏自己的双击判定：它已经校验了“同一物品 + 时间阈值 + 鼠标未拖动”。
            // 旧 ticker 在第二次按下时重新 Raycast，第一次点击若引起选中/拖动/格子重建，
            // 第二次就可能取不到同一件物品，表现为双击偶发失效。
            var nativeDouble = AccessTools.Method(typeof(ItemMouseDoubleClickHandler), "DoubleClickAction",
                new[] { typeof(GameItem), typeof(Vector2) });
            if (nativeDouble != null)
            {
                _harmony.Patch(nativeDouble,
                    prefix: new HarmonyMethod(typeof(DoubleClickInspect), nameof(NativeDoubleClickPrefix)));
            }
            else
            {
                Log.Warn("DoubleClick.Apply", "未找到 ItemMouseDoubleClickHandler.DoubleClickAction，保留 ticker 兜底");
            }

            _patched = true;
            Log.Msg("DoubleClickInspect: patched tooltip hint sources + DoubleClickAction");
        }
        catch (Exception ex)
        {
            Log.Error("DoubleClick.Apply", ex);
        }
    }

    private static void PatchHintSource(System.Reflection.MethodInfo? source, string label)
    {
        if (source != null)
            _harmony!.Patch(source, postfix: new HarmonyMethod(typeof(DoubleClickInspect), nameof(AfterHintSource)));
        else
            Log.Warn("DoubleClick.Apply", $"未找到 {label}，该来源的提示保持原版");
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _patched = false;
        StopTicker();
    }

    private static void StopTicker()
    {
        if (_go == null) return;
        try { UnityEngine.Object.Destroy(_go); } catch { }
        _go = null;
    }

    /// <summary>与原版容器「双击以打开」同格式：前导空格 + 浅蓝色。</summary>
    private static void AddHint(RichTextBuilder builder)
    {
        _hintBuild = TooltipPipeline.BuildId;
        builder.AddLine(HintText, true, RenderHandler.ColorPalette.LightBlue);
    }

    private static string HintText => " " + Loc.T("hint.double_click", "Double-Click To Inspect");

    /// <summary>原版提示方法执行完后：本次 tooltip 还没有双击提示就补一行。</summary>
    private static void AfterHintSource(RichTextBuilder builder, GameItem item)
    {
        try
        {
            if (!ModSettings.DoubleClickInspect) return;
            if (builder == null || !TooltipPipeline.IsBuilding) return;
            if (_hintBuild == TooltipPipeline.BuildId) return;
            if (!IsInspectableItem(item)) return;
            AddHint(builder);
        }
        catch (Exception ex)
        {
            Log.Warn("DoubleClick.Hint", ex);
        }
    }

    /// <summary>
    /// 由 TooltipPipeline 的 AddLine 前缀调用：原版放大镜提示换成双击提示，
    /// 同一个 tooltip 里第二次出现时丢弃。返回 false 表示丢弃这一行。
    /// </summary>
    internal static bool TryRewriteHint(ref string text, ref RenderHandler.ColorPalette color, ref bool italic)
    {
        try
        {
            if (!ModSettings.DoubleClickInspect || string.IsNullOrEmpty(text)) return true;

            // 已购买的鉴定物不接管双击：提示保留原版的「可使用放大镜」，别写成双击。
            var cur = TooltipPipeline.Current;
            if (cur != null && GameRead.IsTag(cur, "IS_OWNED_TAG")) return true;

            EnsureHintSources();
            bool vanilla = string.Equals(text, _appraisalTip, StringComparison.Ordinal)
                        || string.Equals(text, _inspectableTip, StringComparison.Ordinal);
            if (!vanilla) return true;
            if (_hintBuild == TooltipPipeline.BuildId) return false;

            _hintBuild = TooltipPipeline.BuildId;
            text = HintText;
            color = RenderHandler.ColorPalette.LightBlue;
            italic = false;
            return true;
        }
        catch { return true; }
    }

    private static void EnsureHintSources()
    {
        string locale = Loc.Code;
        if (_hintLocale == locale && _appraisalTip != null) return;
        try { _appraisalTip = LocHelper.GetLocalizedUI("mech_appraisal_item_tip"); } catch { _appraisalTip = null; }
        try { _inspectableTip = LocHelper.GetLocalizedMechanic("mech_inspectable_tooltip"); } catch { _inspectableTip = null; }
        _hintLocale = locale;
    }

    /// <summary>
    /// 价格鉴定、真伪鉴定和其它 INSPECTABLE 物品都允许双击打开检视面板。
    ///
    /// 已购买（<c>IS_OWNED_TAG</c>）的鉴定物除外：这些物品的原版双击有正经用途——
    /// 食品券就是靠 <c>ActivateSlotItem</c> 兑换，收购后的鉴定物也可能带着自己的激活动作。
    /// 我们的前缀会吞掉整个双击，等于把兑换按掉了，所以这类物品一律交还原版。
    /// </summary>
    private static bool IsInspectableItem(GameItem? item)
    {
        if (item == null) return false;
        try
        {
            if (GameRead.IsTag(item, "IS_OWNED_TAG")) return false;
            if (GameRead.IsTag(item, "APPRAISAL_ITEM")) return true;
            if (GameRead.IsTag(item, "INSPECTABLE_TAG")) return true;
            return Authenticity.DetectKind(item) != Authenticity.ItemKind.None;
        }
        catch { return false; }
    }

    // ---------------- 双击检测 ----------------

    /// <summary>
    /// 游戏原生双击判定的接管点。与原版共用“同一物品 + 时间窗口 + 未拖动”的判定，
    /// 不再在第二次按下时重新 Raycast；返回 false 表示不再执行游戏的双击动作。
    /// </summary>
    private static bool NativeDoubleClickPrefix(GameItem newItem, Vector2 mousePosition)
    {
        try
        {
            if (!ModSettings.DoubleClickInspect) return true;
            if (!IsInspectableItem(newItem)) return true;
            if (ItemPinHelper.BlocksClicks) return true;

            // 可检视物品的双击必须由本功能消费，即使面板初始化失败也不能
            // 回落到原版 ClickAction（商店里会变成购买/激活）。失败时宁可不响应。
            TryInspect(newItem, "native");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("DoubleClick.Native", ex);
            return true;
        }
    }

    /// <summary>打开鉴定面板；成功（含已经打开同一件）返回 true。</summary>
    private static bool TryInspect(GameItem item, string source)
    {
        try
        {
            var insp = InspectionUIManager.Instance;
            if (insp == null) return false;

            var current = insp.currentInspectingItem;
            if (ReferenceEquals(current, item)) return true;   // 不重复重置正在查看的同一件物品

            insp.OnInspectItem(item);
            Log.Msg($"DoubleClickInspect: {source} -> {GameRead.ItemName(item)}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("DoubleClick.Inspect", ex);
            return false;
        }
    }

    internal static void Install()
    {
        if (_go != null) return;
        try
        {
            _go = new GameObject("ItemDetails_DoubleClickInspect");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _go.AddComponent<Ticker>();
            Log.Msg("DoubleClickInspect ticker installed");
        }
        catch (Exception ex)
        {
            Log.Error("DoubleClick.Install", ex);
            StopTicker();
        }
    }

    private sealed class Ticker : MonoBehaviour
    {
        private const float DoubleClickSeconds = 0.35f;

        private GameItem? _lastItem;
        private float _lastClickTime = -99f;

        private void Update()
        {
            try
            {
                if (!ModSettings.DoubleClickInspect) return;

                var ui = StoreUIManager.Instance;
                if (ui == null) return;

                // 放大镜模式内由游戏自己处理点击，不接管
                if (ui.isMagnifyingMode) return;

                if (!Input.GetMouseButtonDown(0)) return;
                if (ItemPinHelper.BlocksClicks) return;

                var item = RenderHandler.RaycastElement<GameItem>(Input.mousePosition);
                if (item == null || !IsInspectableItem(item))
                {
                    _lastItem = null;
                    return;
                }

                float now = Time.unscaledTime;
                bool twice = ReferenceEquals(item, _lastItem) && now - _lastClickTime <= DoubleClickSeconds;
                _lastItem = item;
                _lastClickTime = now;
                if (!twice) return;

                TryInspect(item, "ticker");
            }
            catch (Exception ex)
            {
                Log.Warn("DoubleClick.Tick", ex);
            }
        }
    }
}
