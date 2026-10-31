using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 发现玩家拥有的净化手段：滤芯按游戏的显式标签，其余按登记表。
///
///   滤芯        item.IsTag("WATER_FILTER")：游戏给所有滤材（含第三方）打的约定标签，按标签识别；
///   其余手段    按物品 ID 查它走哪个游戏函数：
///               机器 → MachinePurifier.PurifyContainer；便携 → WaterHelper.PurifyToBaseWater；
///               化学剂 → ChemicalHelper.DissolveInto + 反应链。
///
/// 查表顺序：作者显式注册（<see cref="ItemDetailsApi.RegisterWaterPurifier"/>）→ 原版条目
/// （<see cref="Known"/>）→ 嗅探出的自动条目（<see cref="Sniffed"/>）→ 都没有就不模拟。
///
/// 嗅探只认证据，不按"能力"去猜（PurifyContainer 对任何机器都能蹭出零点几个百分点；
/// 带 SOLUBLE_TAG 的原版物品有十几种，能净水的只有净水片）：
///   机器/便携   回调里调用了对应的游戏函数（<see cref="CallbackEvidence"/>），说明模拟走的就是它真实的路径；
///   化学剂      回调走 DissolveInto 只说明能溶，还要由 WaterAdvice 在标准试验水上溶一次，
///               纯度上升才登记为净化剂（<see cref="JudgeChem"/>）；等待试验的放在 <see cref="Report.ChemCandidates"/>。
/// 正反结论都按 ID 缓存到 <see cref="Reset"/>，同一种物品只嗅探一次。
///
/// 结果缓存 2 秒；除嗅探结论外不写任何状态。
/// </summary>
internal static class PurifySources
{
    internal sealed class Report
    {
        /// <summary>可用滤芯（按 identifier 去重）。</summary>
        internal readonly List<GameItem> Filters = new();
        /// <summary>登记过的化学净化剂（按 identifier 去重）。</summary>
        internal readonly List<GameItem> ChemAgents = new();
        /// <summary>回调走 DissolveInto、还没做过标准试验的可溶物（按 identifier 去重）。</summary>
        internal readonly List<GameItem> ChemCandidates = new();
        /// <summary>登记过的净水机器（按 identifier 去重）。</summary>
        internal readonly List<GameItem> Machines = new();
        /// <summary>便携净水器。</summary>
        internal GameItem? Portable;

        // 报告会复用 2 秒，因此设备指纹每次报告只算一次，不在每帧 tooltip 重建时重复展开。
        // 滤芯单独签名：容器已装滤芯时，背包滤芯不参与本次模拟。
        internal long CommonSignature;
        internal long FiltersSignature;
        internal bool SignaturesValid;
    }

    internal enum Kind
    {
        Machine,
        Portable,
        Chem,
    }

    /// <summary>原版净化手段：物品 ID → 走哪个游戏函数。滤芯不进表，按 WATER_FILTER 标签识别。</summary>
    private static readonly Dictionary<string, Kind> Known = new(StringComparer.Ordinal)
    {
        ["water_purifier"] = Kind.Machine,             // MachinePurifier.PurifyContainer
        ["portable_water_purifier"] = Kind.Portable,   // WaterHelper.PurifyToBaseWater
        ["water_tablet"] = Kind.Chem,                  // ChemicalHelper.DissolveInto（含氯）
    };

    /// <summary>嗅探结论：null 表示已判定不是净化手段。</summary>
    private static readonly Dictionary<string, Kind?> Sniffed = new(StringComparer.Ordinal);
    /// <summary>回调已证明走 DissolveInto、等待标准试验的可溶物。</summary>
    private static readonly HashSet<string> AwaitingTrial = new(StringComparer.Ordinal);

    private static readonly MethodInfo? PurifyContainer = AccessTools.Method(typeof(MachinePurifier), nameof(MachinePurifier.PurifyContainer));
    private static readonly MethodInfo? PurifyToBaseWater = AccessTools.Method(typeof(WaterHelper), nameof(WaterHelper.PurifyToBaseWater));
    private static readonly MethodInfo? DissolveInto = AccessTools.Method(typeof(ChemicalHelper), nameof(ChemicalHelper.DissolveInto));

    // 上限：控制单次悬停的模拟次数
    private const int MaxFilters = 3;
    internal const int MaxChemAgents = 4;
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
        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        var machineIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var it in items)
        {
            if (it == null) continue;
            string id = it.identifier ?? "";
            if (id.Length == 0) continue;

            try
            {
                var kind = Resolve(it, id, out bool needsTrial);
                if (kind != null)
                {
                    switch (kind)
                    {
                        case Kind.Portable:
                            r.Portable ??= it;
                            break;
                        case Kind.Chem:
                            if (r.ChemAgents.Count < MaxChemAgents && chemIds.Add(id)) r.ChemAgents.Add(it);
                            break;
                        case Kind.Machine:
                            if (r.Machines.Count < MaxMachines && machineIds.Add(id)) r.Machines.Add(it);
                            break;
                    }
                    continue;
                }
                if (needsTrial)
                {
                    if (candidateIds.Add(id)) r.ChemCandidates.Add(it);
                    continue;
                }

                // 滤芯：任何带 WATER_FILTER 的物品（含第三方）
                if (it.IsTag("WATER_FILTER") && r.Filters.Count < MaxFilters && filterIds.Add(id))
                    r.Filters.Add(it);
            }
            catch { }
        }

        // 设备列表、耐久、标签和特性都参与建议结果；在 2 秒报告重建时统一取指纹。
        r.SignaturesValid = true;
        long common = unchecked((long)14695981039346656037UL);
        foreach (var machine in r.Machines) MixSource(r, ref common, machine);
        if (r.Portable != null) MixSource(r, ref common, r.Portable);
        foreach (var agent in r.ChemAgents) MixSource(r, ref common, agent);

        long filters = unchecked((long)14695981039346656037UL);
        foreach (var filter in r.Filters) MixSource(r, ref filters, filter);
        r.CommonSignature = common;
        r.FiltersSignature = filters;
        return r;
    }

    /// <summary>显式注册 → 原版条目 → 嗅探结论；都没有时现场嗅探一次并缓存。</summary>
    private static Kind? Resolve(GameItem it, string id, out bool needsTrial)
    {
        needsTrial = false;
        if (ThirdPartyProviders.TryPurifier(id, out var registered)) return registered;
        if (Known.TryGetValue(id, out var known)) return known;
        if (Sniffed.TryGetValue(id, out var judged)) return judged;
        if (AwaitingTrial.Contains(id))
        {
            needsTrial = true;
            return null;
        }
        return Sniff(it, id, out needsTrial);
    }

    private static Kind? Sniff(GameItem it, string id, out bool needsTrial)
    {
        needsTrial = false;
        Kind? kind = null;
        string evidence = "";

        if (it.IsGameItemType("MACHINE"))
        {
            if (CallbackEvidence.Reaches(it.onCycleEndSlotItemFunc, PurifyContainer))
            {
                kind = Kind.Machine;
                evidence = "机器循环回调调用 MachinePurifier.PurifyContainer";
            }
        }
        else if (CallbackEvidence.Reaches(it.onThisTargetItemFunc, PurifyToBaseWater)
                 || CallbackEvidence.Reaches(it.onItemTargetItemFunc, PurifyToBaseWater))
        {
            kind = Kind.Portable;
            evidence = "使用回调调用 WaterHelper.PurifyToBaseWater";
        }
        else if (it.IsTag("SOLUBLE_TAG")
                 && (CallbackEvidence.Reaches(it.onItemTargetItemFunc, DissolveInto)
                     || CallbackEvidence.Reaches(it.onThisTargetItemFunc, DissolveInto)))
        {
            AwaitingTrial.Add(id);
            needsTrial = true;
            return null;
        }

        Sniffed[id] = kind;
        if (kind != null) Log.Msg($"PurifySources: 自动登记「{id}」为{kind}（{evidence}）");
        return kind;
    }

    /// <summary>该 ID 是否已有结论（显式注册、原版条目或嗅探），不必再做标准试验。</summary>
    internal static bool IsJudged(string id)
        => Sniffed.ContainsKey(id) || Known.ContainsKey(id) || ThirdPartyProviders.TryPurifier(id, out _);

    /// <summary>记下标准试验的结论；之后的扫描按它归类，并让当前报告失效。</summary>
    internal static void JudgeChem(string id, bool purifies)
    {
        AwaitingTrial.Remove(id);
        Sniffed[id] = purifies ? Kind.Chem : null;
        if (purifies) Log.Msg($"PurifySources: 自动登记「{id}」为化学净化剂（标准试验水纯度上升）");
        Invalidate();
    }

    private static void MixSource(Report report, ref long hash, GameItem item)
    {
        if (!ItemValueFingerprint.TryRead(item, out long fingerprint)) report.SignaturesValid = false;
        unchecked { hash = (hash ^ fingerprint) * 1099511628211L; }
    }

    /// <summary>只让当前报告失效，保留嗅探结论。</summary>
    internal static void Invalidate()
    {
        _cached = null;
        _cachedAt = float.MinValue;
    }

    /// <summary>清掉拥有物缓存与嗅探结论；Mod 停用/重启用时由调用方触发。</summary>
    internal static void Reset()
    {
        Invalidate();
        Sniffed.Clear();
        AwaitingTrial.Clear();
    }
}
