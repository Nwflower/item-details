using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 水质提升预估：对玩家实际拥有的每种净化手段，在独立探针上跑一次游戏自己的处理函数，
/// 比较前后纯度，得到「用这种手段能把水提到多少」。
///
/// 安全边界：
///   - 容器探针 = DirectoryMaster.Create + 逐条 TagState.Clone（见 GameRead.DeepCloneTags）；
///   - 滤芯/化学剂同样深拷贝，保留真实耐久与全部标签，绝不用「满耐久默认品」代替；
///   - 机器同样深拷贝：PurifyContainer 内部会用 GetTagReadonly 读加成标签，
///     缺键时 TagSystem.GetTag 会插入 0，把真实机器的品质加成钉死（净水器只能到 99.0%）；
///   - 所有探针在 finally 中销毁，创建/调用失败一律不产出建议。
///
/// 效果口径全部复用原版函数，不自己写公式：
///   机器     MachinePurifier.PurifyContainer
///   便携     WaterHelper.PurifyToBaseWater
///   化学剂   ChemicalHelper.DissolveInto + ChemicalHelper.HandleTemperatureReaction
///            （净水片就是「含 15ml 氯的可溶化学物品」，走同一条链，不再特判）
///   滤芯     WaterHelper.HandleFilter（逐液体，被去除量转成水；uv_filter 由其内部处理）
///
/// 这条路径比其它模块贵得多，所以按液体组成、容器类型、纯度与已装滤芯状态缓存。
/// 不按物品实例缓存：玩家常有多瓶同纯度、同杂质的水，按实例会让每瓶首次悬停
/// 都重跑一次完整模拟（实测单次 60ms 左右）。同组成的水共享结果，首次只付一次成本。
/// 100 槽只是容量上限，条目仍受 30 秒 TTL 约束；水的纯度/杂质变化会改变 key，仍会重算。
///
/// 探针壳按 identifier 池化复用。实测单次 66ms 中 DirectoryMaster.Create 占 55ms，
/// DeepCloneTags 仅 0.07ms；池化后新组成约 10ms，化学剂因 DissolveInto 会 Destroy
/// 化学剂、仍需每次克隆，是剩余 Create 成本的主要来源。
/// </summary>
internal static class WaterAdvice
{
    private const float CacheSeconds = 30f;
    // 容量只是上限：条目仍受 30 秒 TTL 约束。100 槽足以覆盖正常的水容器对比，
    // 线性扫描在工具提示频率下可忽略，真正防止卡顿的是不再让 A/B 互相淘汰。
    private const int MaxEntries = 100;

    /// <summary>某种手段的模拟结果。</summary>
    internal sealed class Method
    {
        internal string Name = "";
        internal double GainPct;      // 纯度提升（百分点）
    }

    private sealed class CacheEntry
    {
        internal long Signature;
        internal int Purity;
        internal GameItem? InstalledFilter;
        internal int? FilterDurability;
        internal int? FilterSize;
        internal string FilterName = "";
        internal string Locale = "";
        internal float At;
        internal List<Method> Methods = new();
    }

    private static readonly List<CacheEntry> Cache = new();

    // 探针壳按 identifier 复用。DirectoryMaster.Create 实测约 4–5ms/次；
    // 标签深拷贝只有 0.1ms，因此复用壳、重灌标签才是正确优化。
    private static readonly Dictionary<string, GameItem> ProbeShells = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, GameItem> SourceShells = new(StringComparer.Ordinal);

    internal static List<Method> SimulateMethods(GameItem? item)
    {
        var result = new List<Method>();
        try
        {
            if (item == null) return result;
            if (!GameRead.IsTag(item, "LIQUID_CONTAINER_TAG")) return result;
            // 酒瓶也是液体容器，但不能走水质净化链；空瓶/纯非水液体也不必扫描拥有物。
            if (GameRead.IsTag(item, "WINE_CONTAINER_TAG")) return result;

            int purity = SafePurity(item);
            if (purity < 0) return result;

            long signature = CompositionSignature(item);
            // HandleFilter 的效果取决于已装滤芯的耐久与孔径，不能只按水质共享。
            // 直接查标签，不调用会在缺键时写入真实物品的 GetTagReadonly。
            GameItem? installedFilter = WaterHelper.GetFilterItem(item);
            int? filterDurability = GameRead.TagInt(installedFilter, "DURABILITY_TAG");
            int? filterSize = GameRead.TagInt(installedFilter, "FILTER_SIZE_TAG");
            string filterName = GameRead.ItemName(installedFilter);
            string locale = Loc.Code;
            float now = UnityEngine.Time.unscaledTime;

            // 过期项顺手清掉，其余按最近使用顺序从后往前找。
            for (int i = Cache.Count - 1; i >= 0; i--)
            {
                var entry = Cache[i];
                if (now - entry.At > CacheSeconds)
                {
                    Cache.RemoveAt(i);
                    continue;
                }
                if (entry.Signature != signature || entry.Purity != purity) continue;
                if (!ReferenceEquals(entry.InstalledFilter, installedFilter)
                    || entry.FilterDurability != filterDurability || entry.FilterSize != filterSize
                    || entry.FilterName != filterName || entry.Locale != locale) continue;

                if (i != Cache.Count - 1)
                {
                    Cache.RemoveAt(i);
                    Cache.Add(entry);
                }
#if ITEMDETAILS_DIAG
                TrackCache(hit: true, now);
#endif
                return entry.Methods;
            }

#if ITEMDETAILS_DIAG
            TrackCache(hit: false, now);
#endif

#if ITEMDETAILS_DIAG
            _diagCloneCreateMs = 0;
            _diagCloneTagsMs = 0;
            _diagFeatureMs = 0;
            _diagCloneCount = 0;
            var diagTotal = System.Diagnostics.Stopwatch.StartNew();
            var diagScan = System.Diagnostics.Stopwatch.StartNew();
#endif
            var owned = PurifySources.Scan();
#if ITEMDETAILS_DIAG
            diagScan.Stop();
#endif
            var usedNames = new HashSet<string>(StringComparer.Ordinal);

            // 容器探针壳与净化源壳都按 identifier 复用；每次使用前只重灌标签。
            GameItem? sharedProbe = GetShell(ProbeShells, item);
            if (sharedProbe == null) return result;

            // 1) 机器：黑盒试 PurifyContainer。机器必须走深拷贝——函数内部用
            //    GetTagReadonly 读品质/性能加成，而 TagSystem.GetTag 对缺键会插入 0，
            //    那会把真实机器的加成钉死，净水器从此只能净化到 99.0%（GetPurityMult(0)=1.0）。
            foreach (var machine in owned.Machines)
            {
                GameItem m = machine;
                if (!ResetProbe(sharedProbe, item)) return result;
                var shell = GetShell(SourceShells, m);
                if (shell == null || !ResetSource(shell, m)) continue;
                RunOnProbe(sharedProbe, m, usedNames, result,
                    (probe, _) => ApplyMachineShell(probe, shell));
            }

            // 2) 便携净水器
            if (owned.Portable != null)
            {
                if (!ResetProbe(sharedProbe, item)) return result;
                RunOnProbe(sharedProbe, owned.Portable, usedNames, result,
                    (probe, _) => WaterHelper.PurifyToBaseWater(probe));
            }

            // 3) 化学剂：溶入探针后跑游戏自己的反应链（净水片、以及第三方沉淀剂都走这里）
            foreach (var agent in owned.ChemAgents)
            {
                GameItem a = agent;
                if (!ResetProbe(sharedProbe, item)) return result;
                RunOnProbe(sharedProbe, a, usedNames, result, (probe, _) =>
                {
                    // DissolveInto 会 Destroy 化学剂，因此这里不能复用壳；
                    // 每次克隆一份并在 finally 中清理。
                    var dissolved = CloneItem(a);
                    if (dissolved == null) return;
                    try
                    {
                        ChemicalHelper.DissolveInto(dissolved, probe);
                        ChemicalHelper.HandleTemperatureReaction(probe);
                    }
                    finally { try { dissolved.Destroy(); } catch { } }
                });
            }

            // 4) 滤芯：容器里已装的那支优先（它才是实际能用的），否则用背包里的。
            if (installedFilter != null)
            {
                var shell = GetShell(SourceShells, installedFilter);
                if (shell == null || !ResetSource(shell, installedFilter)) return result;
                if (!ResetProbe(sharedProbe, item)) return result;
                RunFilter(sharedProbe, installedFilter, shell, usedNames, result);
            }
            else
            {
                foreach (var f in owned.Filters)
                {
                    var shell = GetShell(SourceShells, f);
                    if (shell == null || !ResetSource(shell, f)) continue;
                    if (!ResetProbe(sharedProbe, item)) return result;
                    RunFilter(sharedProbe, f, shell, usedNames, result);
                }
            }

            result.RemoveAll(m => m.GainPct < 0.01);
            result.Sort((a, b) => b.GainPct.CompareTo(a.GainPct));

#if ITEMDETAILS_DIAG
            diagTotal.Stop();
            Log.Msg($"[perf] 水质模拟 purity={purity} methods={result.Count} clones={_diagCloneCount} " +
                    $"total={diagTotal.Elapsed.TotalMilliseconds:0.00}ms scan={diagScan.Elapsed.TotalMilliseconds:0.00}ms " +
                    $"create={_diagCloneCreateMs:0.00}ms tags={_diagCloneTagsMs:0.00}ms feature={_diagFeatureMs:0.00}ms");
#endif
            Cache.Add(new CacheEntry
            {
                Signature = signature,
                Purity = purity,
                InstalledFilter = installedFilter,
                FilterDurability = filterDurability,
                FilterSize = filterSize,
                FilterName = filterName,
                Locale = locale,
                At = now,
                Methods = result,
            });
            if (Cache.Count > MaxEntries) Cache.RemoveAt(0);
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.SimulateMethods", ex);
            return result;
        }
    }

    /// <summary>
    /// 水质组成指纹：容器类型 + 每种液体的 identifier/parts。
    /// 两瓶同类型、同纯度、同杂质的水得到同一签名，共享净化建议；
    /// 任何液体比例变化都会改变签名，不会返回过期建议。
    /// </summary>
    private static long CompositionSignature(GameItem item)
    {
        const long offset = unchecked((long)14695981039346656037UL);
        const long prime = 1099511628211L;
        unchecked
        {
            long h = offset;

            try
            {
                string id = item.identifier ?? "";
                for (int i = 0; i < id.Length; i++) h = (h ^ id[i]) * prime;
            }
            catch { }

            foreach (var liquid in Liquid.Liquids)
            {
                if (liquid == null) continue;
                string part = liquid.currentPart ?? "";
                int parts = GameRead.TagInt(item, part) ?? 0;
                if (parts <= 0) continue;

                for (int i = 0; i < part.Length; i++) h = (h ^ part[i]) * prime;
                h = (h ^ parts) * prime;
            }
            return h;
        }
    }

    /// <summary>按 identifier 取一个可复用壳；首次才 Create。</summary>
    private static GameItem? GetShell(Dictionary<string, GameItem> pool, GameItem source)
    {
        try
        {
            string id = source.identifier ?? "";
            if (id.Length == 0) return null;
            if (pool.TryGetValue(id, out var existing)) return existing;

            var shell = CloneItem(source);
            if (shell == null) return null;
            pool[id] = shell;
            return shell;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.GetShell", ex);
            return null;
        }
    }

    /// <summary>复用净化源壳：只重灌标签/单件价值，不重新 Create。</summary>
    private static bool ResetSource(GameItem shell, GameItem source)
    {
        try
        {
            var cloned = GameRead.DeepCloneTags(source.modifiedState);
            if (cloned == null) return false;

            shell.modifiedState = cloned;
            shell.state = shell.modifiedState;
            try { shell.unitValue = source.unitValue; } catch { }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.ResetSource", ex);
            return false;
        }
    }

    private static void ClearShells()
    {
        foreach (var shell in ProbeShells.Values)
            try { shell.Destroy(); } catch { }
        foreach (var shell in SourceShells.Values)
            try { shell.Destroy(); } catch { }
        ProbeShells.Clear();
        SourceShells.Clear();
    }

    /// <summary>
    /// 预热：只创建缺失的探针/源壳，不跑净化模拟。
    /// 在存档加载后由 <see cref="WaterWarmupTicker"/> 调用，把工厂创建成本
    /// 挪到加载阶段，避免第一次悬停水质容器时付 60ms 级卡顿。
    /// </summary>
    internal static bool PrewarmOwned()
    {
        try
        {
            // 不按 WaterPurity 短路：玩家可能在进入游戏后才打开水质，
            // 先建壳可以让那次开关也直接命中，不必等下一次悬停才付 Create。
            var items = EmporiumEntry.Instance?.GetAllOwnedItems();
            if (items == null || items.Count == 0) return false;

            int probesBefore = ProbeShells.Count;
            int sourcesBefore = SourceShells.Count;

            foreach (var item in items)
            {
                if (item == null) continue;
                if (!GameRead.IsTag(item, "LIQUID_CONTAINER_TAG")) continue;
                if (GameRead.IsTag(item, "WINE_CONTAINER_TAG")) continue;
                if (SafePurity(item) < 0) continue;
                GetShell(ProbeShells, item);
            }

            var owned = PurifySources.Scan();
            foreach (var machine in owned.Machines) GetShell(SourceShells, machine);
            foreach (var filter in owned.Filters) GetShell(SourceShells, filter);

            Log.Msg($"WaterAdvice prewarm: probes +{ProbeShells.Count - probesBefore} " +
                    $"sources +{SourceShells.Count - sourcesBefore}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.Prewarm", ex);
            return true;   // 失败也不要每 0.5 秒重试刷屏
        }
    }

    internal static void Stop()
    {
        Cache.Clear();
        ClearShells();
#if ITEMDETAILS_DIAG
        _diagHits = 0;
        _diagMisses = 0;
        _diagAt = 0f;
#endif
        PurifySources.Reset();
    }

#if ITEMDETAILS_DIAG
    private static int _diagHits;
    private static int _diagMisses;
    private static float _diagAt;
    private static double _diagCloneCreateMs;
    private static double _diagCloneTagsMs;
    private static double _diagFeatureMs;
    private static int _diagCloneCount;

    /// <summary>
    /// 每 2 秒汇总一次缓存命中/重算。重算次数才是水质建议的真实成本来源：
    /// 在几瓶水之间来回划时，单槽缓存会 1:1 交替 miss。
    /// </summary>
    private static void TrackCache(bool hit, float now)
    {
        if (hit) _diagHits++;
        else _diagMisses++;
        if (_diagAt <= 0f) _diagAt = now + 2f;
        if (now < _diagAt) return;

        Log.Msg($"[perf] 水质建议缓存（2秒）: 命中={_diagHits} 重算={_diagMisses} 槽位={Cache.Count}");
        _diagHits = 0;
        _diagMisses = 0;
        _diagAt = now + 2f;
    }
#endif

    private delegate void PurifyAction(GameItem probe, GameItem source);

    /// <summary>
    /// 在探针容器上跑一次机器净化。机器同样深拷贝：<c>MachinePurifier.PurifyContainer</c>
    /// 会通过 <c>GetTagReadonly</c> 读 <c>TOTAL_PERCENTAGE_*_BONUS_INT</c>，而
    /// <c>TagSystem.GetTag</c> 缺键即插入 0 —— 直接传真实机器会把它的加成永久钉成 0，
    /// 表现为净水器只能净化到 99.0%。
    /// </summary>
    /// <summary>安全入口：直接传真实机器时先克隆，绝不改真实物品。</summary>
    internal static void ApplyMachine(GameItem probe, GameItem machine)
    {
        GameItem? clone = null;
        try
        {
            clone = CloneItem(machine);
            if (clone == null) return;
            MachinePurifier.PurifyContainer(clone, probe);
        }
        catch (Exception ex) { Log.Warn("WaterAdvice.ApplyMachine", ex); }
        finally
        {
            if (clone != null) { try { clone.Destroy(); } catch { } }
        }
    }

    /// <summary>池化路径：调用方已经拿到隔离壳并重置过标签。</summary>
    private static void ApplyMachineShell(GameItem probe, GameItem machineShell)
    {
        try { MachinePurifier.PurifyContainer(machineShell, probe); }
        catch (Exception ex) { Log.Warn("WaterAdvice.ApplyMachineShell", ex); }
    }

    /// <summary>在已重置的共享探针上跑一次手段，比较前后纯度。</summary>
    private static void RunOnProbe(GameItem probe, GameItem source,
        HashSet<string> usedNames, List<Method> result, PurifyAction action)
    {
        try
        {
            int before = SafePurity(probe);
            if (before < 0) return;

            action(probe, source);

            AddMethod(result, usedNames, GameRead.ItemName(source), before, SafePurity(probe));
        }
        catch (Exception ex) { Log.Warn("WaterAdvice.Simulate", ex); }
    }

    /// <summary>滤芯：共享容器探针 + 滤芯深拷贝，逐液体走原版 HandleFilter。</summary>
    private static void RunFilter(GameItem probe, GameItem filterSource, GameItem filterShell,
        HashSet<string> usedNames, List<Method> result)
    {
        try
        {
            int before = SafePurity(probe);
            if (before < 0) return;

            string waterTag = Liquid.GetCurrentPartFromId("water");
            foreach (var liquid in Liquid.Liquids)
            {
                if (liquid == null) continue;
                string part = liquid.currentPart;
                int amount = GameRead.TagInt(probe, part) ?? 0;
                if (amount <= 0) continue;

                int remaining = WaterHelper.HandleFilter(liquid.identifier, amount, probe, filterShell);
                int removed = amount - remaining;
                if (removed <= 0) continue;

                probe.ModifyTag(part, st => st.Enable().SetInt(remaining));
                int waterNow = GameRead.TagInt(probe, waterTag) ?? 0;
                probe.ModifyTag(waterTag, st => st.Enable().SetInt(waterNow + removed));
            }

            AddMethod(result, usedNames, GameRead.ItemName(filterSource), before, SafePurity(probe));
        }
        catch (Exception ex) { Log.Warn("WaterAdvice.Filter", ex); }
    }

    private static void AddMethod(List<Method> list, HashSet<string> usedNames,
        string name, int before, int after)
    {
        if (before < 0 || after <= before) return;

        // 名称取不到就不列：不编造占位名
        if (string.IsNullOrEmpty(name)) return;

        // 同一效果不重复列（例如容器里装的就是 UV 滤芯，可能同时被滤芯与化学通道覆盖）
        if (!usedNames.Add(name)) return;

        list.Add(new Method { Name = name, GainPct = (after - before) / 100.0 });
    }

    /// <summary>
    /// 复用探针壳：重灌容器的标签/单件价值，移除上一轮的水质特性并重新初始化。
    /// 只重建 TagState 和水质特性，不重新 DirectoryMaster.Create。
    /// </summary>
    private static bool ResetProbe(GameItem probe, GameItem item)
    {
        try
        {
            var cloned = GameRead.DeepCloneTags(item.modifiedState);
            if (cloned == null) return false;

            probe.modifiedState = cloned;
            probe.state = probe.modifiedState;
            try { probe.unitValue = item.unitValue; } catch { }

            try
            {
                probe.itemFeatures.RemoveAll(f => f != null && f.category == "CATEGORY_WATER_PURITY");
            }
            catch { }

#if ITEMDETAILS_DIAG
            var diagFeature = System.Diagnostics.Stopwatch.StartNew();
#endif
            try { WaterFeatureHelper.InitWaterFeature(probe); }
            catch (Exception ex) { Log.Warn("WaterAdvice.InitFeature", ex); }
#if ITEMDETAILS_DIAG
            diagFeature.Stop();
            _diagFeatureMs += diagFeature.Elapsed.TotalMilliseconds;
#endif
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.ResetProbe", ex);
            return false;
        }
    }

    /// <summary>按 identifier 造副本并深拷贝全部标签/单件价值；失败返回 null。</summary>
    private static GameItem? CloneItem(GameItem source)
    {
        GameItem? copy = null;
        try
        {
            string id = source.identifier;
            if (string.IsNullOrEmpty(id)) return null;

#if ITEMDETAILS_DIAG
            var diagCreate = System.Diagnostics.Stopwatch.StartNew();
#endif
            copy = DirectoryMaster.Create<GameItem>(id);
#if ITEMDETAILS_DIAG
            diagCreate.Stop();
            _diagCloneCreateMs += diagCreate.Elapsed.TotalMilliseconds;
#endif
            if (copy == null) return null;

#if ITEMDETAILS_DIAG
            var diagTags = System.Diagnostics.Stopwatch.StartNew();
#endif
            var cloned = GameRead.DeepCloneTags(source.modifiedState);
#if ITEMDETAILS_DIAG
            diagTags.Stop();
            _diagCloneTagsMs += diagTags.Elapsed.TotalMilliseconds;
#endif
            if (cloned == null) { Log.Warn("WaterAdvice.Clone", "标签克隆失败，跳过"); copy.Destroy(); return null; }

            copy.modifiedState = cloned;
            copy.state = copy.modifiedState;
            try { copy.unitValue = source.unitValue; } catch { }
#if ITEMDETAILS_DIAG
            _diagCloneCount++;
#endif
            return copy;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.CloneItem", ex);
            if (copy != null) { try { copy.Destroy(); } catch { } }
            return null;
        }
    }

    internal static int SafePurity(GameItem item)
    {
        try
        {
            // 不调 WaterHelper.GetWaterPurity：它用 GetTagReadonly 遍历所有液体，
            // 会给真实物品插入缺失标签。这里与 WaterInfo 一样只 peek 已有键。
            long water = 0;
            long total = 0;
            foreach (var liquid in Liquid.Liquids)
            {
                if (liquid == null) continue;
                int parts = GameRead.TagInt(item, liquid.currentPart) ?? 0;
                if (parts <= 0) continue;
                total += parts;
                if (liquid.identifier == "water") water += parts;
            }
            if (water <= 0 || total <= 0) return -1;
            return (int)(water * WaterInfo.PurityFull / total);
        }
        catch { return -1; }
    }
}

/// <summary>
/// 在 EmporiumEntry 完成场景初始化后预热水质探针。只建壳，不模拟。
/// 用 Start postfix 而不是自建 MonoBehaviour：模组创建的对象在主菜单/场景切换中
/// 不保证拿到 Update，而 EmporiumEntry.Start 是存档场景确定会走到的时机。
/// </summary>
internal static class WaterWarmupPatch
{
    private const string HarmonyId = "item_details.water_warmup";
    private static Harmony? _harmony;
    private static bool _patched;

    internal static void Apply()
    {
        if (_patched) return;
        try
        {
            var start = AccessTools.Method(typeof(EmporiumEntry), "Start", Type.EmptyTypes);
            if (start == null)
            {
                Log.Warn("WaterWarmup.Apply", "未找到 EmporiumEntry.Start，跳过预热");
                return;
            }
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(start,
                postfix: new HarmonyMethod(typeof(WaterWarmupPatch), nameof(AfterStart)));
            _patched = true;
            Log.Msg("WaterWarmup: patched EmporiumEntry.Start");
        }
        catch (Exception ex)
        {
            Log.Error("WaterWarmup.Apply", ex);
        }
    }

    internal static void Remove()
    {
        try { _harmony?.UnpatchAll(HarmonyId); } catch { }
        _harmony = null;
        _patched = false;
    }

    private static void AfterStart()
    {
        try { WaterAdvice.PrewarmOwned(); }
        catch (Exception ex) { Log.Warn("WaterWarmup.AfterStart", ex); }
    }
}
