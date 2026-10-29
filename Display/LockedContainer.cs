using System;

namespace ItemDetails;

/// <summary>
/// 两套锁的统一判断：
///   - 保险箱/枪柜：LOCK_TYPE，真实状态在 contentWindow.isLocked；
///   - 安全箱/医疗箱/证物箱：LOCKED_CONTAINER_TAG，状态在 LOCK_STATUS。
/// 只有“确实还锁着”的容器才交给 SealedBoxInfo；解锁后按普通容器显示。
/// </summary>
internal static class LockedContainer
{
    internal static bool IsLocked(GameItem? item)
    {
        if (item == null) return false;
        try
        {
            if (item.IsTag("LOCK_TYPE"))
            {
                var cw = item.contentWindow;
                return cw == null || cw.isLocked;
            }

            if (item.IsTag("LOCKED_CONTAINER_TAG"))
                return item.IsTag("LOCK_STATUS")
                    && item.GetTagReadonly("LOCK_STATUS").GetBool();
        }
        catch { }
        return false;
    }
}
