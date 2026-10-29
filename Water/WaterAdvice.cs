using System;
using System.Collections.Generic;

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
/// 这条路径比其它模块贵得多，所以按「液体组成 + 容器类型 + 净化前纯度」缓存。
/// 不按物品实例缓存：玩家常有多瓶同纯度、同杂质的水，按实例会让每瓶首次悬停
/// 都重跑一次完整模拟（实测单次 60ms 左右）。同组成的水共享结果，首次只付一次成本。
/// 100 槽只是容量上限，条目仍受 30 秒 TTL 约束；水的纯度/杂质变化会改变 key，仍会重算。
/// </summary>
internal static class WaterAdvice
{
    private const float CacheSeconds = 30f;
    // 容量只是上限：条目仍受 10 秒 TTL 约束。100 槽足以覆盖任何正常的水容器对比，
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
        internal float At;
        internal List<Method> Methods = new();
    }

    private static readonly List<CacheEntry> Cache = new();

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

            var owned = PurifySources.Scan();
            var usedNames = new HashSet<string>(StringComparer.Ordinal);

            // 1) 机器：黑盒试 PurifyContainer。机器必须走深拷贝——函数内部用
            //    GetTagReadonly 读品质/性能加成，而 TagSystem.GetTag 对缺键会插入 0，
            //    那会把真实机器的加成钉死，净水器从此只能净化到 99.0%（GetPurityMult(0)=1.0）。
            foreach (var machine in owned.Machines)
            {
                GameItem m = machine;
                RunOnProbe(item, m, usedNames, result,
                    (probe, _) => ApplyMachine(probe, m));
            }

            // 2) 便携净水器
            if (owned.Portable != null)
            {
                RunOnProbe(item, owned.Portable, usedNames, result,
                    (probe, _) => WaterHelper.PurifyToBaseWater(probe));
            }

            // 3) 化学剂：溶入探针后跑游戏自己的反应链（净水片、以及第三方沉淀剂都走这里）
            foreach (var agent in owned.ChemAgents)
            {
                GameItem a = agent;
                RunOnProbe(item, a, usedNames, result, (probe, _) =>
                {
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
            GameItem? installedFilter = null;
            try { installedFilter = WaterHelper.GetFilterItem(item); } catch { }

            if (installedFilter != null)
            {
                RunFilter(item, installedFilter, usedNames, result);
            }
            else
            {
                foreach (var f in owned.Filters)
                    RunFilter(item, f, usedNames, result);
            }

            result.RemoveAll(m => m.GainPct < 0.01);
            result.Sort((a, b) => b.GainPct.CompareTo(a.GainPct));

            Cache.Add(new CacheEntry
            {
                Signature = signature,
                Purity = purity,
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

    internal static void Stop()
    {
        Cache.Clear();
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

    /// <summary>单容器手段：造探针 → 跑一次 → 比较纯度。任何一步失败都不产出建议。</summary>
    private static void RunOnProbe(GameItem container, GameItem source,
        HashSet<string> usedNames, List<Method> result, PurifyAction action)
    {
        GameItem? probe = null;
        try
        {
            probe = NewProbe(container);
            if (probe == null) return;

            int before = SafePurity(probe);
            if (before < 0) return;

            action(probe, source);

            AddMethod(result, usedNames, GameRead.ItemName(source), before, SafePurity(probe));
        }
        catch (Exception ex) { Log.Warn("WaterAdvice.Simulate", ex); }
        finally
        {
            if (probe != null) { try { probe.Destroy(); } catch { } }
        }
    }

    /// <summary>滤芯：容器探针 + 滤芯深拷贝，逐液体走原版 HandleFilter。</summary>
    private static void RunFilter(GameItem container, GameItem filterSource,
        HashSet<string> usedNames, List<Method> result)
    {
        GameItem? probe = null;
        GameItem? filter = null;
        try
        {
            probe = NewProbe(container);
            filter = CloneItem(filterSource);
            if (probe == null || filter == null) return;

            int before = SafePurity(probe);
            if (before < 0) return;

            string waterTag = Liquid.GetCurrentPartFromId("water");
            foreach (var liquid in Liquid.Liquids)
            {
                if (liquid == null) continue;
                string part = liquid.currentPart;
                int amount = GameRead.TagInt(probe, part) ?? 0;
                if (amount <= 0) continue;

                int remaining = WaterHelper.HandleFilter(liquid.identifier, amount, probe, filter);
                int removed = amount - remaining;
                if (removed <= 0) continue;

                probe.ModifyTag(part, st => st.Enable().SetInt(remaining));
                int waterNow = GameRead.TagInt(probe, waterTag) ?? 0;
                probe.ModifyTag(waterTag, st => st.Enable().SetInt(waterNow + removed));
            }

            AddMethod(result, usedNames, GameRead.ItemName(filterSource), before, SafePurity(probe));
        }
        catch (Exception ex) { Log.Warn("WaterAdvice.Filter", ex); }
        finally
        {
            if (probe != null) { try { probe.Destroy(); } catch { } }
            if (filter != null) { try { filter.Destroy(); } catch { } }
        }
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

    /// <summary>造一个与真实容器标签隔离的探针（深拷贝，见 GameRead.DeepCloneTags）。</summary>
    private static GameItem? NewProbe(GameItem item)
    {
        var probe = CloneItem(item);
        if (probe == null) return null;

        // 探针必须拥有自己的水质特性。共享 itemFeatures 会让 UpdateWaterFeature
        // 改写真实物品的暴露状态和价值修正。
        try { WaterFeatureHelper.InitWaterFeature(probe); }
        catch (Exception ex) { Log.Warn("WaterAdvice.InitFeature", ex); }

        return probe;
    }

    /// <summary>按 identifier 造副本并深拷贝全部标签/单件价值；失败返回 null。</summary>
    private static GameItem? CloneItem(GameItem source)
    {
        GameItem? copy = null;
        try
        {
            string id = source.identifier;
            if (string.IsNullOrEmpty(id)) return null;

            copy = DirectoryMaster.Create<GameItem>(id);
            if (copy == null) return null;

            var cloned = GameRead.DeepCloneTags(source.modifiedState);
            if (cloned == null) { Log.Warn("WaterAdvice.Clone", "标签克隆失败，跳过"); copy.Destroy(); return null; }

            copy.modifiedState = cloned;
            copy.state = copy.modifiedState;
            try { copy.unitValue = source.unitValue; } catch { }
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
