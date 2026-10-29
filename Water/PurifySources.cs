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
///   机器        item.IsGameItemType("MACHINE") **且** 机器循环回调挂在
///                                                MachinePurifier 上（见 IsPurifierMachine）
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

                // 机器：必须是真正跑 PurifyContainer 的净水器，否则任何机器都会被
                // PurifyContainer 的固定预算蹭出零点几个百分点（见 IsPurifierMachine）。
                if (it.IsGameItemType("MACHINE") && IsPurifierMachine(it))
                {
                    if (r.Machines.Count < MaxMachines && machineIds.Add(id)) r.Machines.Add(it);
                }
            }
            catch { }
        }

        return r;
    }

    /// <summary>
    /// 这台机器是否真的会跑 <see cref="MachinePurifier.PurifyContainer"/>。
    ///
    /// 不能只看类型是 MACHINE：<c>PurifyContainer</c> 与机器身份无关，去除量只取决于
    /// 「水量 × 3‰ + 25ml」再乘水质曲线，任何机器（充能器、能量阵列……）被丢进去都会
    /// 蹭出 0.0x% 的“提升”，于是建议里冒出一堆莫名其妙的净化手段。
    ///
    /// 游戏里只有净水器把这个函数挂在自己的机器循环回调上，所以按回调的声明类型判定：
    /// 委托落在 <c>MachinePurifier</c>（含其闭包类）里就是净水器。
    /// </summary>
    private static bool IsPurifierMachine(GameItem machine)
    {
        try
        {
            var cycle = machine.onCycleEndSlotItemFunc;
            if (cycle == null) return false;

            for (var t = cycle.Method?.DeclaringType; t != null; t = t.DeclaringType)
                if (t == typeof(MachinePurifier)) return true;

            // 兜底：闭包类型名不是嵌套形式时仍能认出来
            string? full = cycle.Method?.DeclaringType?.FullName;
            return full != null && full.StartsWith("MachinePurifier", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>清掉拥有物缓存；Mod 停用/重启用时由调用方触发。</summary>
    internal static void Reset()
    {
        _cached = null;
        _cachedAt = float.MinValue;
    }
}
