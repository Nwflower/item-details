using System;
using System.Collections.Generic;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 玩家当前拥有的扫描 / 分析设备快照。
///
/// 为什么需要：`EmporiumEntry.GetAllOwnedItems()` 会递归遍历商店、背包、前台、展示柜、
/// 垃圾箱、售货机与下班窗口等十几棵容器树，一次调用就要分配一个列表；而
/// 「液体组分分析 = 有扫描器时开启」和双击扫描都要在每次悬停 / 双击时判断。
/// 这里按 TTL 缓存一份只含扫描器 id 的小列表：买、卖、移动最多滞后 <see cref="CacheSeconds"/> 秒，
/// 换来悬停路径上的一次字典级判断。
///
/// 只读；主菜单或读档前 <see cref="EmporiumEntry"/> 为空时返回空集合，不抛。
/// </summary>
internal static class ScannerInventory
{
    internal const string WaterScanner = "aquascan";
    internal const string ChemScanner = "chem_scanner";
    internal const string XrayScanner = "xray_scanner";
    internal const string PlantAnalyser = "plant_analyser";

    /// <summary>扫描器列表的缓存时长。够短，玩家刚买到就能用；够长，悬停不会反复全库遍历。</summary>
    private const float CacheSeconds = 2f;

    private static readonly List<GameItem> Found = new();
    private static float _at = -999f;
    private static bool _warned;

    internal static bool Has(string id) => Find(id) != null;

    /// <summary>返回拥有的一件该 id 设备；没有返回 null。多个同名时取遍历到的第一个。</summary>
    internal static GameItem? Find(string id)
    {
        Refresh();
        for (int i = 0; i < Found.Count; i++)
        {
            var item = Found[i];
            if (item != null && item.identifier == id) return item;
        }
        return null;
    }

    /// <summary>水质扫描仪或化学扫描仪任意一件：液体组分分析的门控。</summary>
    internal static bool HasLiquidScanner => Has(WaterScanner) || Has(ChemScanner);

    internal static void Stop()
    {
        Found.Clear();
        _at = -999f;
    }

    private static void Refresh()
    {
        float now = Time.unscaledTime;
        if (now - _at < CacheSeconds) return;
        _at = now;
        Found.Clear();
        try
        {
            var emporium = EmporiumEntry.Instance;
            if (emporium == null) return;   // 主菜单 / 读档前

            foreach (var item in emporium.GetAllOwnedItems())
            {
                if (item == null) continue;
                string id = item.identifier ?? "";
                if (id == WaterScanner || id == ChemScanner || id == XrayScanner || id == PlantAnalyser)
                    Found.Add(item);
            }
        }
        catch (Exception ex)
        {
            // 读失败不应该每次悬停都刷日志：缓存已置空，按“没有扫描器”处理。
            if (!_warned)
            {
                _warned = true;
                Log.Warn("ScannerInventory", ex);
            }
            Found.Clear();
        }
    }
}
