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
/// 提示文案：Harmony Prefix 接管 `AppraisalHelper.CreateAppraisalTooltip`，
/// 把原版「可使用放大镜进行检查」换成双击提示（devMode 仍保留 TRUE VALUE 行）。
///
/// 只读 + 调用游戏自己的公开方法，不改存档。
/// </summary>
internal static class DoubleClickInspect
{
    private const string HarmonyId = "item_details.double_click";
    private static Harmony? _harmony;
    private static bool _patched;
    private static GameObject? _go;

    internal static void Apply()
    {
        if (_patched) return;
        try
        {
            var target = AccessTools.Method(typeof(AppraisalHelper), "CreateAppraisalTooltip",
                new[] { typeof(RichTextBuilder), typeof(GameItem) });
            if (target == null)
            {
                Log.Warn("DoubleClick.Apply", "未找到 CreateAppraisalTooltip，提示文案保持原版");
                return;
            }
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(DoubleClickInspect), nameof(TipPrefix)));

            // 原版还有一行「可使用放大镜进行检查」，来自 InspectableHelper（INSPECTABLE_TAG）。
            // 双击已能直接鉴定，这行就多余了，鉴定物品上整体跳过。
            var inspectable = AccessTools.Method(typeof(InspectableHelper), "CreateInspectableTooltip",
                new[] { typeof(RichTextBuilder), typeof(GameItem) });
            if (inspectable != null)
            {
                _harmony.Patch(inspectable,
                    prefix: new HarmonyMethod(typeof(DoubleClickInspect), nameof(InspectablePrefix)));
            }
            else
            {
                Log.Warn("DoubleClick.Apply", "未找到 CreateInspectableTooltip，放大镜提示行将保留");
            }

            _patched = true;
            Log.Msg("DoubleClickInspect: patched CreateAppraisalTooltip + CreateInspectableTooltip");
        }
        catch (Exception ex)
        {
            Log.Error("DoubleClick.Apply", ex);
        }
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

    /// <summary>跳过原版「可使用放大镜进行检查」（仅鉴定物品且双击功能开启时）。</summary>
    private static bool InspectablePrefix(GameItem item)
    {
        try
        {
            if (!ModSettings.DoubleClickInspect) return true;
            if (item == null) return true;
            return !GameRead.IsTag(item, "APPRAISAL_ITEM");
        }
        catch { return true; }
    }

    /// <summary>返回 false 表示接管原方法，自己写提示行。</summary>
    private static bool TipPrefix(RichTextBuilder builder, GameItem item)
    {
        try
        {
            if (!ModSettings.DoubleClickInspect) return true;
            if (builder == null || item == null) return true;
            if (!GameRead.IsTag(item, "APPRAISAL_ITEM")) return true;

            // devMode 仍显示真实值（原版行为）
            try
            {
                var store = PlayerStore.Instance;
                if (store != null && store.devMode)
                    builder.AddLine("TRUE VALUE  : " + AppraisalHelper.GetCorrectBaseValue(item),
                        true, RenderHandler.ColorPalette.LightOrange);
            }
            catch { }

            builder.AddLine(
                Loc.T("hint.double_click",
                    "Double-click to inspect; double-click another item to switch"),
                true, RenderHandler.ColorPalette.LightOrange);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("DoubleClick.Tip", ex);
            return true;
        }
    }

    // ---------------- 双击检测 ----------------

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
                if (item == null || !GameRead.IsTag(item, "APPRAISAL_ITEM"))
                {
                    _lastItem = null;
                    return;
                }

                float now = Time.unscaledTime;
                bool twice = ReferenceEquals(item, _lastItem) && now - _lastClickTime <= DoubleClickSeconds;
                _lastItem = item;
                _lastClickTime = now;
                if (!twice) return;

                Inspect(item);
            }
            catch (Exception ex)
            {
                Log.Warn("DoubleClick.Tick", ex);
            }
        }

        private static void Inspect(GameItem item)
        {
            try
            {
                var insp = InspectionUIManager.Instance;
                if (insp == null) return;

                var current = insp.currentInspectingItem;
                if (ReferenceEquals(current, item)) return;   // 不重复重置正在查看的同一件物品

                insp.OnInspectItem(item);
            }
            catch (Exception ex)
            {
                Log.Warn("DoubleClick.Inspect", ex);
            }
        }
    }
}
