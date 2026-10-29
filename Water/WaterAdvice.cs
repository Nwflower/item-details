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
///   - 机器只读（PurifyContainer 只读机器的性能/品质加成，不改机器）；
///   - 所有探针在 finally 中销毁，创建/调用失败一律不产出建议。
///
/// 效果口径全部复用原版函数，不自己写公式：
///   机器     MachinePurifier.PurifyContainer
///   便携     WaterHelper.PurifyToBaseWater
///   化学剂   ChemicalHelper.DissolveInto + ChemicalHelper.HandleTemperatureReaction
///            （净水片就是「含 15ml 氯的可溶化学物品」，走同一条链，不再特判）
///   滤芯     WaterHelper.HandleFilter（逐液体，被去除量转成水；uv_filter 由其内部处理）
///
/// 这条路径比其它模块贵得多，所以按「物品 + 净化前纯度」缓存 2 秒。
/// </summary>
internal static class WaterAdvice
{
    private const float CacheSeconds = 2f;

    /// <summary>某种手段的模拟结果。</summary>
    internal sealed class Method
    {
        internal string Name = "";
        internal double GainPct;      // 纯度提升（百分点）
    }

    private static List<Method>? _cache;
    private static int _cacheId = int.MinValue;
    private static int _cachePurity = -1;
    private static float _cacheAt = float.MinValue;

    internal static List<Method> SimulateMethods(GameItem? item)
    {
        var result = new List<Method>();
        try
        {
            if (item == null) return result;
            if (!GameRead.IsTag(item, "LIQUID_CONTAINER_TAG")) return result;

            int purity = SafePurity(item);
            if (purity < 0) return result;

            int uid = GameRead.UniqueId(item);
            if (_cache != null && _cacheId == uid && _cachePurity == purity
                && UnityEngine.Time.unscaledTime - _cacheAt <= CacheSeconds)
                return _cache;

            var owned = PurifySources.Scan();
            var usedNames = new HashSet<string>(StringComparer.Ordinal);

            // 1) 机器：黑盒试 PurifyContainer（只读机器，自带模块/品质加成）
            foreach (var machine in owned.Machines)
            {
                GameItem m = machine;
                RunOnProbe(item, m, usedNames, result,
                    (probe, _) => MachinePurifier.PurifyContainer(m, probe));
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

            _cache = result;
            _cacheId = uid;
            _cachePurity = purity;
            _cacheAt = UnityEngine.Time.unscaledTime;
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.SimulateMethods", ex);
            return result;
        }
    }

    internal static void Stop()
    {
        _cache = null;
        _cacheId = int.MinValue;
        _cachePurity = -1;
        _cacheAt = float.MinValue;
        PurifySources.Reset();
    }

    private delegate void PurifyAction(GameItem probe, GameItem source);

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
        try { return WaterHelper.GetWaterPurity(item); } catch { return -1; }
    }
}
