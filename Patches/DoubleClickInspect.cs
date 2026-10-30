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
/// 写法照抄原版容器的「双击以打开」（`ContainerHelper.CreateContainerTooltip`：
/// 前导空格 + 浅蓝色），让同一类“双击做某事”的提示在 tooltip 里长得一样。
///
/// 只读 + 调用游戏自己的公开方法，不改存档。
/// </summary>
internal static class DoubleClickInspect
{
    private const string HarmonyId = "item_details.double_click";
    private static Harmony? _harmony;
    private static bool _patched;

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
            var nativeDouble = AccessTools.Method(typeof(ItemMouseDoubleClickHandler), "DoubleClickAction",
                new[] { typeof(GameItem), typeof(Vector2) });
            if (nativeDouble == null)
            {
                Log.Warn("DoubleClick.Apply", "未找到原生双击入口，保留原版操作与提示");
                return;
            }
            _harmony = new Harmony(HarmonyId);
            // 由游戏的输入调度器处理时间阈值、已消费的点击与拖动仲裁。
            _harmony.Patch(nativeDouble,
                prefix: new HarmonyMethod(typeof(DoubleClickInspect), nameof(NativeDoubleClickPrefix)));
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
            Log.Msg("DoubleClickInspect: patched CreateAppraisalTooltip + CreateInspectableTooltip + DoubleClickAction");
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
    }

    /// <summary>
    /// 跳过原版「可使用放大镜进行检查」。价格鉴定的提示由 TipPrefix 写；
    /// 真伪鉴定/其它可检视物品在这里补同样的双击提示。
    /// </summary>
    private static bool InspectablePrefix(RichTextBuilder builder, GameItem item)
    {
        try
        {
            if (!ModSettings.DoubleClickInspect) return true;
            if (item == null) return true;

            // 价格鉴定走 CreateAppraisalTooltip，由 TipPrefix 统一写提示，避免重复。
            if (GameRead.IsTag(item, "APPRAISAL_ITEM")) return false;

            if (!IsInspectableItem(item)) return true;

            if (builder != null) AddHint(builder);
            return false;
        }
        catch { return true; }
    }

    /// <summary>与原版容器「双击以打开」同格式：前导空格 + 浅蓝色。</summary>
    private static void AddHint(RichTextBuilder builder) =>
        builder.AddLine(" " + Loc.T("hint.double_click", "Double-Click To Inspect"),
            true, RenderHandler.ColorPalette.LightBlue);

    /// <summary>价格鉴定、真伪鉴定和其它 INSPECTABLE 物品都允许双击打开检视面板。</summary>
    private static bool IsInspectableItem(GameItem? item)
    {
        if (item == null) return false;
        try
        {
            if (GameRead.IsTag(item, "APPRAISAL_ITEM")) return true;
            if (GameRead.IsTag(item, "INSPECTABLE_TAG")) return true;
            return Authenticity.DetectKind(item) != Authenticity.ItemKind.None;
        }
        catch { return false; }
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

            AddHint(builder);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn("DoubleClick.Tip", ex);
            return true;
        }
    }

    // ---------------- 双击检测 ----------------

    /// <summary>
    /// 游戏原生双击判定的接管点，沿用原版的物品、时间与输入消费判定，
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

}
