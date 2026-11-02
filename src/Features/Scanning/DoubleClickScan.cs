using System;

namespace ItemDetails;

/// <summary>
/// 双击扫描（独立于右键菜单扫描开关）。设备匹配和扫描面板由 ContextMenuScan 共用实现。
/// 不接管有原版容器打开或激活行为的物品，避免影响食品券、装有滤芯的水罐、
/// 自定义模组物品等。上锁补给箱有 X 射线扫描器时可直接扫描。
/// </summary>
internal static class DoubleClickScan
{
    /// <summary>在不改变物品状态的情况下判断这次双击是否可以被扫描接管。</summary>
    internal static bool CanHandle(GameItem? item)
    {
        if (!ModSettings.DoubleClickScan || item == null) return false;
        try
        {
            if (GeneralHelper.IsDisplayOnly(item)) return false;
            if (!ContextMenuScan.CanScan(item)) return false;

            // 原版在上锁补给箱上无法打开容器；有 X 射线扫描器时允许直接扫描。
            if (ContextMenuScan.IsLockedSupplyCrate(item)
                && ScannerInventory.Has(ScannerInventory.XrayScanner))
                return true;

            return !VanillaDoubleClickHasResult(item);
        }
        catch (Exception ex)
        {
            Log.Warn("DoubleClickScan.CanHandle", ex);
            return false;
        }
    }

    /// <summary>扫描成功才消费双击；缺设备或面板不可用时把动作留给原版/鉴定。</summary>
    internal static bool TryScan(GameItem? item, string source)
    {
        if (!CanHandle(item)) return false;
        return ContextMenuScan.TryScan(item, source);
    }

    /// <summary>
    /// 与旧版双击扫描一致：已有容器窗口或可激活操作时，扫描不得覆盖原版双击。
    /// 失败时按存在原版动作处理。仅检查，不调用任何有副作用的操作。
    /// </summary>
    private static bool VanillaDoubleClickHasResult(GameItem item)
    {
        try
        {
            var window = item.contentWindow;
            if (window != null && !window.isLocked) return true;
        }
        catch { return true; }

        try { return item.MayActivateSlotItem() && item.CanActivateSlotItem(); }
        catch { return true; }
    }
}
