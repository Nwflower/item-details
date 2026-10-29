using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 发现玩家拥有的净化手段——**按能力识别，不按物品 ID 白名单**。
///
/// 依据（0.46D）：
///   EmporiumEntry.Instance.GetAllOwnedItems()  玩家拥有的全部物品
///   滤芯        item.IsTag("WATER_FILTER")     —— water_filter / water_filter_adv /
///                                                hydroreed_filter / koton_fabric / uv_filter
///                                                以及第三方新增滤材
///   化学净化剂  item.IsTag("SOLUBLE_TAG")      —— 溶入水后由 ChemicalHelper.HandleTemperatureReaction
///                                                的反应链处理（含氯、稳定剂沉降、中和等）
///   机器        item.IsGameItemType("MACHINE") —— 黑盒试 MachinePurifier.PurifyContainer
///   便携净水器  identifier == "portable_water_purifier"
///                                               唯一保留 ID 的例外：它没有区分标签，
///                                               效果挂在 onTargetItem 回调里，无法按能力识别
///
/// 纯读取；结果缓存 2 秒。
/// </summary>
internal static class PurifySources
{
    internal sealed class Report
    {
        /// <summary>可用滤芯（按 identifier 去重）。</summary>
        internal readonly List<GameItem> Filters = new();
        /// <summary>可溶化学物品（按 identifier 去重），涵盖净水片与第三方净化剂。</summary>
        internal readonly List<GameItem> ChemAgents = new();
        /// <summary>机器（按 identifier 去重），是否真能净水由模拟结果决定。</summary>
        internal readonly List<GameItem> Machines = new();
        /// <summary>便携净水器（无标签，只能按 ID 认）。</summary>
        internal GameItem? Portable;
    }

    // 上限：控制单次悬停的模拟次数
    private const int MaxFilters = 3;
    private const int MaxChemAgents = 4;
    private const int MaxMachines = 2;

    private static readonly float CacheSeconds = 2f;
    private static Report? _cached;
    private static float _cachedAt = float.MinValue;

    internal static Report Scan()
    {
        try
        {
            if (_cached != null && UnityEngine.Time.unscaledTime - _cachedAt <= CacheSeconds)
                return _cached;

            _cached = CountOwned();
            _cachedAt = UnityEngine.Time.unscaledTime;
            return _cached;
        }
        catch (Exception ex)
        {
            Log.Warn("PurifySources.Scan", ex);
            return new Report();
        }
    }

    private static Report CountOwned()
    {
        var r = new Report();

        List<GameItem>? items;
        try { items = EmporiumEntry.Instance?.GetAllOwnedItems(); }
        catch (Exception ex)
        {
            Log.Warn("PurifySources.Owned", ex);
            return r;
        }
        if (items == null)
        {
            Log.Warn("PurifySources.Owned", "GetAllOwnedItems 返回 null");
            return r;
        }

        var filterIds = new HashSet<string>(StringComparer.Ordinal);
        var chemIds = new HashSet<string>(StringComparer.Ordinal);
        var machineIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var it in items)
        {
            if (it == null) continue;
            string id;
            try { id = it.identifier ?? ""; } catch { continue; }
            if (id.Length == 0) continue;

            try
            {
                // 便携净水器：没有区分标签，只能按 ID
                if (id == "portable_water_purifier")
                {
                    r.Portable ??= it;
                    continue;
                }

                // 滤芯：任何带 WATER_FILTER 的物品（含第三方）
                if (it.IsTag("WATER_FILTER"))
                {
                    if (r.Filters.Count < MaxFilters && filterIds.Add(id)) r.Filters.Add(it);
                    continue;
                }

                // 化学净化剂：可溶化学物品（净水片就走这条）
                if (it.IsTag("SOLUBLE_TAG"))
                {
                    if (r.ChemAgents.Count < MaxChemAgents && chemIds.Add(id)) r.ChemAgents.Add(it);
                    continue;
                }

                // 机器：黑盒试，能不能净水由模拟判定
                if (it.IsGameItemType("MACHINE"))
                {
                    if (r.Machines.Count < MaxMachines && machineIds.Add(id)) r.Machines.Add(it);
                }
            }
            catch { }
        }

        return r;
    }

    /// <summary>清掉拥有物缓存；Mod 停用/重启用时由调用方触发。</summary>
    internal static void Reset()
    {
        _cached = null;
        _cachedAt = float.MinValue;
    }
}
