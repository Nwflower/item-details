#if ITEMDETAILS_SELFTEST
using System;
using System.Collections.Generic;
using System.Text;

namespace ItemDetails;

/// <summary>
/// 自检的用例与探针断言：造样本物品、调游戏公式、与我们的实现逐项比对。
/// 由 <see cref="SelfTest.RunAll"/> 调用；每个方法返回一行给日志的结论，
/// 失败写在返回值里而不是抛出去，避免一条用例把整轮自检打断。
/// </summary>
internal static partial class SelfTest
{
    /// <summary>
    /// 验证"探针改写不影响真实物品"：深拷贝标签 → 在探针上 ModifyTag，
    /// 再比对真实物品标签有无被改动。
    /// 这条与 CheckTagIsolation 互补：那条测 TagSystem 层，这条测整条探针路径。
    /// </summary>
    private static string CheckProbeWriteIsolation(out bool ok)
    {
        ok = false;
        try
        {
            var real = TryCreate("plasma_fuel");
            if (real == null) return "probe write isolation: 无法创建样本";
            PlasmaHelper.InitPlasmaItem(real);

            var before = TagSnapshot(real);

            var probe = DirectoryMaster.Create<GameItem>("plasma_fuel");
            if (probe == null) return "probe write isolation: 无法创建探针";
            var clonedReal = GameRead.DeepCloneTags(real.modifiedState);
            if (clonedReal == null) return "probe write isolation: 标签克隆失败";
            probe.modifiedState = clonedReal;
            probe.state = probe.modifiedState;

            // 与各类探针相同的写法
            probe.ModifyTag("PLASMA_SEAL", t => t.Enable().SetString("NONE"));

            var after = TagSnapshot(real);
            int diff = 0;
            foreach (var kv in before)
                if (!after.TryGetValue(kv.Key, out string? v2) || v2 != kv.Value) diff++;

            bool probeApplied;
            try { probeApplied = probe.GetTagReadonly("PLASMA_SEAL").GetString() == "NONE"; }
            catch { probeApplied = false; }

            ok = diff == 0 && probeApplied;
            return $"probe write isolation: realChanged={diff} probeApplied={(probeApplied ? 1 : 0)} (期望 0/1)";
        }
        catch (Exception ex)
        {
            return "probe write isolation: 异常 " + ex.GetType().Name;
        }
    }

    /// <summary>
    /// 只读读取的回归：GameRead 的 TagInt/TagString/TagDouble 不得向真实物品插入缺失键，
    /// 且必须能读到按 double 写入的值（化学品纯度就是这种存储方式）。
    /// </summary>
    private static string CheckTagReadIsolation(out bool ok)
    {
        ok = false;
        GameItem? item = null;
        try
        {
            item = TryCreate("plasma_fuel");
            if (item == null) return "tag read isolation: 无法创建样本";
            PlasmaHelper.InitPlasmaItem(item);

            int before = item.modifiedState.dict.Count;
            _ = GameRead.TagInt(item, "ITEMDETAILS_PROBE_MISSING_INT");
            _ = GameRead.TagString(item, "ITEMDETAILS_PROBE_MISSING_STR");
            _ = GameRead.TagDouble(item, "ITEMDETAILS_PROBE_MISSING_DBL");
            int inserted = item.modifiedState.dict.Count - before;

            item.ModifyTag("ITEMDETAILS_PROBE_DOUBLE", t => t.Enable().SetDouble(87.5));
            double? d = GameRead.TagDouble(item, "ITEMDETAILS_PROBE_DOUBLE");
            bool doubleOk = d is double v && Math.Abs(v - 87.5) < 0.0001;

            ok = inserted == 0 && doubleOk;
            return $"tag read isolation: inserted={inserted} double={(doubleOk ? 1 : 0)} (期望 0/1)";
        }
        catch (Exception ex)
        {
            return "tag read isolation: 异常 " + ex.GetType().Name;
        }
        finally
        {
            if (item != null) { try { item.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 机器净化模拟不得动到真实机器。PurifyContainer 内部用 <c>GetTagReadonly</c> 读
    /// <c>TOTAL_PERCENTAGE_*_BONUS_INT</c>，而 TagSystem.GetTag 缺键即插入 0 ——
    /// 直接传真实机器会把品质加成钉死，净水器从此只能净化到 99.0%。
    /// 这里拿一台真实机器跑一次模拟，比对标签快照。
    /// </summary>
    private static string CheckMachineSimIsolation(out bool ok)
    {
        ok = false;
        GameItem? machine = null;
        GameItem? container = null;
        try
        {
            machine = TryCreate("water_purifier");
            container = TryCreate("water_tank");
            if (machine == null || container == null) return "machine sim isolation: 无法创建样本";

            // 一杯脏水：900ml 水 + 100ml 重金属
            container.ModifyTag(Liquid.GetCurrentPartFromId("water"), st => st.Enable().SetInt(900000));
            container.ModifyTag(Liquid.GetCurrentPartFromId("heavy_metal"), st => st.Enable().SetInt(100000));

            var before = TagSnapshot(machine);
            int purityBefore = WaterAdvice.SafePurity(container);

            // 容器用探针，避免测量本身改动样本
            var probe = DirectoryMaster.Create<GameItem>("water_tank");
            if (probe == null) return "machine sim isolation: 无法创建容器探针";
            try
            {
                var cloned = GameRead.DeepCloneTags(container.modifiedState);
                if (cloned == null) return "machine sim isolation: 标签克隆失败";
                probe.modifiedState = cloned;
                probe.state = probe.modifiedState;
                WaterFeatureHelper.InitWaterFeature(probe);

                WaterAdvice.ApplyMachine(probe, machine);
            }
            finally { try { probe.Destroy(); } catch { } }

            var after = TagSnapshot(machine);
            int changed = 0;
            foreach (var kv in before)
                if (!after.TryGetValue(kv.Key, out string? v2) || v2 != kv.Value) changed++;
            int added = 0;
            foreach (var kv in after) if (!before.ContainsKey(kv.Key)) added++;

            ok = changed == 0 && added == 0;
            return $"machine sim isolation: changed={changed} added={added} purityBefore={purityBefore} (期望 0/0)";
        }
        catch (Exception ex)
        {
            return "machine sim isolation: 异常 " + ex.GetType().Name;
        }
        finally
        {
            if (machine != null) { try { machine.Destroy(); } catch { } }
            if (container != null) { try { container.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 水质行不得给真实物品添加特性。<c>GetCurrentWaterDisplay</c> 内部的
    /// <c>GetWaterFeature</c> 在缺 <c>CATEGORY_WATER_PURITY</c> 时会 <c>AddItemFeature</c>，
    /// 等于悬停即改真实物品。
    /// </summary>
    private static string CheckWaterInfoDoesNotAddFeature(out bool ok)
    {
        ok = false;
        GameItem? item = null;
        try
        {
            item = TryCreate("water_tank");
            if (item == null) return "water info feature isolation: 无法创建样本";

            item.ModifyTag(Liquid.GetCurrentPartFromId("water"), st => st.Enable().SetInt(900000));

            // 造出"没有水质特性"的容器：第三方模组新建的、或特性被移除过
            item.itemFeatures.RemoveAll(f => f != null && f.category == "CATEGORY_WATER_PURITY");
            int before = item.itemFeatures.Count;

            WaterInfo.Analyze(item);

            int after = item.itemFeatures.Count;
            bool added = item.FindItemFeatureByCategory("CATEGORY_WATER_PURITY") != null;
            ok = after == before && !added;
            return $"water info feature isolation: before={before} after={after} added={added} (期望数量不变、added=False)";
        }
        catch (Exception ex)
        {
            return "water info feature isolation: 异常 " + ex.GetType().Name;
        }
        finally
        {
            if (item != null) { try { item.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 自动售水误判回归：一瓶标签与实物相符的水，经过水质显示、完整净化建议和
    /// 完整 tooltip 后，液体标签、水质特性与游戏自己的错标签判定都必须保持不变。
    /// </summary>
    private static string CheckWaterSaleStateIsolation(out bool ok)
    {
        ok = false;
        GameItem? item = null;
        try
        {
            item = TryCreate("bottled_water");
            if (item == null) return "water sale isolation: 无法创建样本";

            // 735ml 水 + 15ml 重金属（总容量 750ml）：可出售的 Base Water，标签与真实条件初始一致。
            item.ModifyTag(Liquid.GetCurrentPartFromId("water"), st => st.Enable().SetInt(735000));
            item.ModifyTag(Liquid.GetCurrentPartFromId("heavy_metal"), st => st.Enable().SetInt(15000));
            WaterFeatureHelper.InitWaterFeature(item);

            var originalFeature = item.FindItemFeatureByCategory("CATEGORY_WATER_PURITY");
            if (originalFeature == null) return "water sale isolation: 样本缺水质特性";

            var tagsBefore = TagSnapshot(item);
            string featureBefore = WaterFeatureSnapshot(item);
            long valueBefore = item.unitValue;
            bool mislabelledBefore = VendingMachineHelper.IsMislabelled(item);

            WaterAdvice.Stop();
            _ = WaterInfo.Analyze(item);
            _ = WaterAdvice.SimulateMethods(item);

            // 清掉建议缓存，再让模组渲染器独立跑一次；原版 tooltip 补丁由集成用例覆盖。
            WaterAdvice.Stop();
            _ = ModEntry.RenderTooltipText(item);

            var tagsAfter = TagSnapshot(item);
            string featureAfter = WaterFeatureSnapshot(item);
            bool sameFeature = ReferenceEquals(originalFeature,
                item.FindItemFeatureByCategory("CATEGORY_WATER_PURITY"));
            bool mislabelledAfter = VendingMachineHelper.IsMislabelled(item);
            bool tagsSame = TagsEqual(tagsBefore, tagsAfter);
            bool featureUnchanged = sameFeature && featureBefore == featureAfter;
            bool valueUnchanged = valueBefore == item.unitValue;

            ok = !mislabelledBefore && !mislabelledAfter && tagsSame
                 && featureUnchanged && valueUnchanged;
            return $"water sale isolation: mislabelled={mislabelledBefore}/{mislabelledAfter} " +
                   $"tagsSame={tagsSame} featureSame={featureUnchanged} valueSame={valueUnchanged} " +
                   "(期望 False/False、其余 True)";
        }
        catch (Exception ex)
        {
            return "water sale isolation: 异常 " + ex.GetType().Name + " - " + ex.Message;
        }
        finally
        {
            WaterAdvice.Stop();
            if (item != null) { try { item.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 真实售水结算验收：相符的 Base Water 经模组完整分析后，直接调用游戏自己的
    /// SimulateWaterSale。必须真的出水并入账，且上下层声誉的误标损失都为零增量。
    /// </summary>
    private static string CheckWaterFountainTransaction(out bool ok)
    {
        ok = false;
        GameItem? source = null;
        GameItem? machine = null;
        try
        {
            source = TryCreate("bottled_water");
            machine = TryCreate("vending_fountain");
            if (source == null || machine == null) return "water fountain transaction: 无法创建样本";

            source.ModifyTag(Liquid.GetCurrentPartFromId("water"), st => st.Enable().SetInt(735000));
            source.ModifyTag(Liquid.GetCurrentPartFromId("heavy_metal"), st => st.Enable().SetInt(15000));
            WaterFeatureHelper.InitWaterFeature(source);
            GeneralHelper.SetItemOwned(source, own: true);
            GeneralHelper.SetItemOwned(machine, own: true);

            var input = MachineHelper.GetSlot(machine, 1) as GameSlotInventory;
            if (input == null) return "water fountain transaction: 找不到 Base Water 输入槽";
            int remaining = GraphUtils.TryAcceptAll(input, source);
            if (remaining != 0 || input.childItem != source)
                return $"water fountain transaction: 无法把水源放入输入槽 remaining={remaining} " +
                       $"owned={GeneralHelper.IsItemOwned(source)}/{GeneralHelper.IsItemOwned(machine)} " +
                       $"waterType={source.IsGameItemType("WATER")} " +
                       $"liquidContainer={source.IsTag("LIQUID_CONTAINER_TAG")} " +
                       $"mayAccept={VendingFountainHelper.MayAcceptWaterSource(source)}";

            int volumeBefore = LiquidPartsTotal(source);
            int waterBefore = GameRead.TagInt(source, Liquid.GetCurrentPartFromId("water")) ?? 0;
            int capacity = GameRead.TagInt(source, "LIQUID_CONTAINER_CAPACITY") ?? 0;
            bool validFilledBottle = waterBefore > 0 && volumeBefore > 0 && volumeBefore == capacity;
            int cashBefore = GameRead.TagInt(machine, "VENDING_CASH") ?? -1;
            double lossLLBefore = PlayerStore.Instance.fountainMislabelRepLossLL;
            double lossULBefore = PlayerStore.Instance.fountainMislabelRepLossUL;

            WaterAdvice.Stop();
            _ = WaterInfo.Analyze(source);
            _ = WaterAdvice.SimulateMethods(source);
            WaterAdvice.Stop();
            _ = ModEntry.RenderTooltipText(source);

            // labelPurity=2 与实测 98% 一致；出售容器和水源都由游戏函数处理。
            VendingFountainHelper.SimulateWaterSale(2, "bottled_water", input, machine);

            int volumeAfter = LiquidPartsTotal(source);
            int cashAfter = GameRead.TagInt(machine, "VENDING_CASH") ?? -1;
            double lossLLDelta = PlayerStore.Instance.fountainMislabelRepLossLL - lossLLBefore;
            double lossULDelta = PlayerStore.Instance.fountainMislabelRepLossUL - lossULBefore;
            bool transferred = validFilledBottle && volumeAfter < volumeBefore;
            bool paid = cashBefore >= 0 && cashAfter > cashBefore;
            bool noMislabelLoss = lossLLDelta == 0.0 && lossULDelta == 0.0;

            ok = transferred && paid && noMislabelLoss;
            return $"water fountain transaction: water={waterBefore} volume={volumeBefore}->{volumeAfter} " +
                   $"capacity={capacity} cash={cashBefore}->{cashAfter} " +
                   $"repLossDelta={lossLLDelta}/{lossULDelta} " +
                   "(期望瓶中有水且装满、水量下降、现金增加、损失 0/0)";
        }
        catch (Exception ex)
        {
            return "water fountain transaction: 异常 " + ex.GetType().Name + " - " + ex.Message;
        }
        finally
        {
            WaterAdvice.Stop();
            if (source != null) { try { source.Destroy(); } catch { } }
            if (machine != null) { try { machine.Destroy(); } catch { } }
        }
    }

    private static int LiquidPartsTotal(GameItem item)
    {
        long total = 0;
        foreach (var liquid in Liquid.Liquids)
            if (liquid != null) total += GameRead.TagInt(item, liquid.currentPart) ?? 0;
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    private static string WaterFeatureSnapshot(GameItem item)
    {
        var f = item.FindItemFeatureByCategory("CATEGORY_WATER_PURITY");
        if (f == null) return "missing";
        string fake = f.fakeCondition?.identifier ?? "";
        string real = f.realCondition?.identifier ?? "";
        return $"{item.itemFeatures.Count}|{fake}|{real}|{f.customIntValue1}|" +
               $"{f.IsExposed()}|{f.IsFeatureDiscovered()}|{f.IsFeatureMatch()}|{f.isDisabled}";
    }

    private static bool TagsEqual(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out string? value) || value != kv.Value) return false;
        return true;
    }

    private static Dictionary<string, string> TagSnapshot(GameItem item)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var kv in item.modifiedState.dict)
            {
                var t = kv.Value;
                if (t == null) continue;
                string s; int i; bool en;
                try { s = t.GetString() ?? ""; } catch { s = ""; }
                try { i = t.GetInt(); } catch { i = 0; }
                try { en = t.IsEnabled(); } catch { en = false; }
                map[kv.Key] = $"{en}|{i}|{s}";
            }
        }
        catch { }
        return map;
    }
    /// <summary>
    /// 验证酒的透视数据：正常酒应有分数、无赝品标记；赝品酒必须被标出。
    /// 用 WineHelper.CreateFinishedWineForDebug 造样本，不依赖玩家存档里的酒。
    /// </summary>
    private static string CheckWineInfo(out bool ok)
    {
        ok = false;
        GameItem? normal = null;
        GameItem? fake = null;
        try
        {
            normal = WineHelper.CreateFinishedWineForDebug(5);
            var nLines = WineInfo.Collect(normal);
            // 断言用本地化后的前缀，不写死中文：模组已支持多语种，中文文案只在 zh 下成立。
            string scoreHead = Loc.T("wine.score_part", ", score {0}", "");
            string fakeHead = Loc.T("wine.counterfeit", "Counterfeit: illegal yeast");
            bool hasScore = HasLine(nLines, scoreHead);
            bool normalClean = !HasLine(nLines, fakeHead);

            fake = WineHelper.CreateFinishedWineForDebug(5, counterfeit: true);
            var fLines = WineInfo.Collect(fake);
            bool fakeMarked = HasLine(fLines, fakeHead);

            ok = hasScore && normalClean && fakeMarked;
            return $"wine: score={hasScore} normalClean={normalClean} fakeMarked={fakeMarked} (期望 true/true/true)";
        }
        catch (Exception ex)
        {
            return "wine: 异常 " + ex.GetType().Name;
        }
        finally
        {
            if (fake != null) { try { fake.Destroy(); } catch { } }
            if (normal != null) { try { normal.Destroy(); } catch { } }
        }
    }

    /// <summary>酒瓶不得进入净化模拟；纯度 peek 不得给真实容器插入缺失液体标签。</summary>
    private static string CheckWaterAdviceReadIsolation(out bool ok)
    {
        ok = false;
        GameItem? wine = null;
        GameItem? water = null;
        try
        {
            wine = WineHelper.CreateFinishedWineForDebug(5);
            int wineBefore = wine.modifiedState.dict.Count;
            var methods = WaterAdvice.SimulateMethods(wine);
            int wineInserted = wine.modifiedState.dict.Count - wineBefore;
            bool wineSkipped = GameRead.IsTag(wine, "WINE_CONTAINER_TAG") && methods.Count == 0;

            water = TryCreate("water_tank");
            if (water == null) return "water advice isolation: 无法创建水容器";
            water.ModifyTag(Liquid.GetCurrentPartFromId("water"), st => st.Enable().SetInt(900000));
            int waterBefore = water.modifiedState.dict.Count;
            int purity = WaterAdvice.SafePurity(water);
            int waterInserted = water.modifiedState.dict.Count - waterBefore;

            ok = wineSkipped && wineInserted == 0 && waterInserted == 0 && purity == WaterInfo.PurityFull;
            return $"water advice isolation: wineSkipped={wineSkipped} wineInserted={wineInserted} " +
                   $"waterInserted={waterInserted} purity={purity} (期望 true/0/0/{WaterInfo.PurityFull})";
        }
        catch (Exception ex)
        {
            return "water advice isolation: 异常 " + ex.GetType().Name;
        }
        finally
        {
            if (wine != null) { try { wine.Destroy(); } catch { } }
            if (water != null) { try { water.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 用确定组成的独立样本验证多槽缓存：A/B 交替后再次访问 A/B，
    /// 必须返回各自第一次的同一个结果实例，而不是重建出新的列表。
    /// </summary>
    private static string CheckWaterAdviceCache(out bool ok)
    {
        ok = false;
        var picked = new List<GameItem>();
        var first = new List<List<WaterAdvice.Method>>();
        var firstMs = new List<double>();
        try
        {
            foreach (int waterParts in new[] { 735000, 720000 })
            {
                var item = TryCreate("bottled_water");
                if (item == null) return "water advice cache: 无法创建含水样本";
                picked.Add(item);
                foreach (var liquid in Liquid.Liquids)
                    item.ModifyTag(liquid.currentPart, state => state.Enable().SetInt(0));
                item.ModifyTag(Liquid.GetCurrentPartFromId("water"), state => state.Enable().SetInt(waterParts));
                item.ModifyTag(Liquid.GetCurrentPartFromId("heavy_metal"), state => state.Enable().SetInt(750000 - waterParts));
                WaterFeatureHelper.InitWaterFeature(item);
            }

            // 从干净缓存开始，测量每种水的首次真实模拟成本。
            WaterAdvice.Stop();
            foreach (var item in picked)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = WaterAdvice.SimulateMethods(item);
                sw.Stop();
                first.Add(result);
                firstMs.Add(sw.Elapsed.TotalMilliseconds);
            }

            // A/B 两轮交替：单槽缓存会在第二次访问 A 时重建，引用必然不同。
            int calls = 0, hits = 0;
            double hitMaxMs = 0;
            for (int round = 0; round < 2; round++)
            {
                for (int i = 0; i < picked.Count; i++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var result = WaterAdvice.SimulateMethods(picked[i]);
                    sw.Stop();
                    calls++;
                    if (ReferenceEquals(result, first[i])) hits++;
                    if (sw.Elapsed.TotalMilliseconds > hitMaxMs) hitMaxMs = sw.Elapsed.TotalMilliseconds;
                }
            }

            ok = hits == calls;
            string firstText = string.Join("/", firstMs.ConvertAll(ms => ms.ToString("0.00")));
            return $"water advice cache: items={picked.Count} firstMs={firstText} " +
                   $"hits={hits}/{calls} hitMax={hitMaxMs:0.00}ms " +
                   $"(期望 hits 等于 calls，即交替悬停不重算)";
        }
        catch (Exception ex)
        {
            return "water advice cache: 异常 " + ex.GetType().Name + " - " + ex.Message;
        }
        finally
        {
            WaterAdvice.Stop();
            foreach (var item in picked) { try { item.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 药物与杂质分流：活性成分按精确量，acid/base 这类试剂必须留在百分比一类，
    /// 避免把腐蚀性试剂误报成药物剂量。
    /// </summary>
    private static string CheckLiquidRoles(out bool ok)
    {
        ok = false;
        GameItem? bottle = null;
        try
        {
            bool Classified(string id, LiquidRole expected)
            {
                var liquid = Liquid.GetLiquidFromId(id);
                return liquid != null && LiquidRoleClassifier.Classify(liquid) == expected;
            }

            bool water = Classified("water", LiquidRole.Water);
            bool zyanide = Classified("zyanide", LiquidRole.Active);
            bool synaprest = Classified("synaprest", LiquidRole.Active);
            bool contraceptive = Classified("contraceptive", LiquidRole.Active);
            bool caffeine = Classified("caffeine", LiquidRole.Active);
            bool heavyMetal = Classified("heavy_metal", LiquidRole.Other);
            bool acid = Classified("acid", LiquidRole.Other);
            bool baseLiquid = Classified("base", LiquidRole.Other);
            bool alcohol = Classified("alcohol", LiquidRole.Other);

            // 700 ml 水 + 600 µl 氰化物 + 5 ml 重金属：药物单独成项并保留精确 parts。
            bottle = TryCreate("bottled_water");
            if (bottle == null) return "liquid roles: 无法创建容器样本";
            bottle.ModifyTag(Liquid.GetCurrentPartFromId("water"), st => st.Enable().SetInt(700000));
            bottle.ModifyTag(Liquid.GetCurrentPartFromId("zyanide"), st => st.Enable().SetInt(600));
            bottle.ModifyTag(Liquid.GetCurrentPartFromId("heavy_metal"), st => st.Enable().SetInt(5000));
            var r = WaterInfo.Analyze(bottle);

            bool activeOk = r.Actives.Count == 1 && r.Actives[0].Parts == 600;
            bool impurityOk = r.Impurities.Exists(p => p.Parts == 5000);
            bool amountOk = WaterInfo.Amount(600) == "600 uL";

            ok = water && zyanide && synaprest && contraceptive && caffeine
                 && heavyMetal && acid && baseLiquid && alcohol
                 && activeOk && impurityOk && amountOk;
            return $"liquid roles: water={water} zya={zyanide} syn={synaprest} bc={contraceptive} " +
                   $"caf={caffeine} imp={heavyMetal} acid={acid} base={baseLiquid} alc={alcohol} " +
                   $"actives={r.Actives.Count}/{activeOk} impOk={impurityOk} uL={amountOk} (期望全 True)";
        }
        catch (Exception ex)
        {
            return "liquid roles: 异常 " + ex.GetType().Name + " - " + ex.Message;
        }
        finally
        {
            if (bottle != null) { try { bottle.Destroy(); } catch { } }
        }
    }

    /// <summary>缓存 key 必须区分已安装滤芯与设备状态，防止返回另一状态的旧结果。</summary>
    private static string CheckCacheKeyFreshness(out bool ok)
    {
        ok = false;
        GameItem? water = null;
        GameItem? filterA = null;
        GameItem? filterB = null;
        try
        {
            water = TryCreate("bottled_water");
            filterA = TryCreate("water_filter");
            filterB = TryCreate("water_filter_adv");
            if (water == null || filterA == null || filterB == null)
                return "cache key freshness: 无法创建样本";

            var waterMethod = typeof(WaterAdvice).GetMethod("TrySimulationSignature",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var report = new PurifySources.Report
            {
                SignaturesValid = true,
                CommonSignature = 111,
                FiltersSignature = 222,
            };
            long WaterKey(GameItem filter)
            {
                object[] args = { water, report, filter, 0L };
                bool valid = waterMethod != null
                    && (bool)waterMethod.Invoke(null, args)!;
                return valid ? (long)args[3] : 0L;
            }

            long filterKeyA = WaterKey(filterA);
            long filterKeyAAgain = WaterKey(filterA);
            long filterKeyB = WaterKey(filterB);
            report.CommonSignature = 333;
            long equipmentKey = WaterKey(filterA);
            bool waterOk = filterKeyA != 0 && filterKeyA == filterKeyAAgain
                && filterKeyA != filterKeyB && filterKeyA != equipmentKey;

            ok = waterOk;
            return $"cache key freshness: water={waterOk} (期望 True)";
        }
        catch (Exception ex)
        {
            return "cache key freshness: 异常 " + ex.GetType().Name + " - " + ex.Message;
        }
        finally
        {
            if (water != null) { try { water.Destroy(); } catch { } }
            if (filterA != null) { try { filterA.Destroy(); } catch { } }
            if (filterB != null) { try { filterB.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 走游戏原生的双击回调验证补丁：应打开鉴定面板并把目标物品设为当前鉴定物。
    /// 不依赖鼠标坐标，直接验证“游戏判定为双击后”的接管逻辑。
    /// </summary>
    /// <summary>第三方鉴定分解必须显式注册，提供方注销后不再参与；总价仍与游戏值核对。</summary>
    private static string CheckAppraisalInjection(out bool ok)
    {
        ok = false;
        GameItem? item = null;
        IDisposable? registration = null;
        try
        {
            item = TryCreate("plasma_fuel");
            if (item == null) return "appraisal injection: 无法创建样本";
            PlasmaHelper.InitPlasmaItem(item);
            int total = GameRead.TrueValue(item) ?? 0;
            if (total <= 0) return "appraisal injection: 无法取得游戏总价";

            bool absent = ThirdPartyProviders.Appraisal(item) == null;
            registration = ItemDetailsApi.RegisterAppraisalBreakdown("plasma_fuel",
                _ => new ItemDetailsBreakdown(total,
                    new[] { new ItemDetailsBreakdownPart("injected", 25) }));
            var provided = ThirdPartyProviders.Appraisal(item);
            bool present = provided != null && provided.Total == total
                && provided.Parts.Count == 1 && provided.Parts[0].Label == "injected";
            registration.Dispose();
            registration = null;
            bool removed = ThirdPartyProviders.Appraisal(item) == null;

            ok = absent && present && removed;
            return $"appraisal injection: absent={absent} present={present} removed={removed} (期望全部 True)";
        }
        catch (Exception ex)
        {
            return "appraisal injection: 异常 " + ex.GetType().Name + " - " + ex.Message;
        }
        finally
        {
            registration?.Dispose();
            if (item != null) { try { item.Destroy(); } catch { } }
        }
    }

    /// <summary>未被其它模组改动时，GamePerfect 必须与照抄版 Perfect 完全相同（含整数截断边界）。</summary>
    private static string CheckGamePerfectRange(out bool ok)
    {
        ok = false;
        try
        {
            int[] values = { 1, 19, 20, 21, 39, 40, 99, 100, 101, 137, 300, 360, 999, 1000, 4321 };
            int diff = 0;
            string first = "";
            foreach (int v in values)
            {
                var game = AppraisalRanges.GamePerfect(v);
                var pure = AppraisalRanges.Perfect(v);
                if (game == pure) continue;
                diff++;
                if (first.Length == 0) first = $" 首个不一致 {v}: game={game.Low}-{game.High} pure={pure.Low}-{pure.High}";
            }
            ok = diff == 0;
            return $"game perfect range: {values.Length - diff}/{values.Length} 与内置公式一致{first}";
        }
        catch (Exception ex)
        {
            return "game perfect range: 异常 " + ex.GetType().Name;
        }
    }

    private static string CheckDoubleClickInspect(out bool ok)
    {
        ok = false;
        GameItem? item = null;
        GameItem? injector = null;
        try
        {
            item = TryCreate("plasma_fuel");
            if (item == null) return "double click inspect: 无法创建鉴定样本";
            PlasmaHelper.InitPlasmaItem(item);

            injector = TryCreate("food_stamp");
            if (injector == null) return "double click inspect: 无法创建真伪样本";
            InsStampHelper.Init(injector);
            if (Authenticity.DetectKind(injector) == Authenticity.ItemKind.None)
                return "double click inspect: 粮票样本不是真伪鉴定类型";

            var handler = ItemMouseDoubleClickHandler.current;
            var insp = InspectionUIManager.Instance;
            if (handler == null) return "double click inspect: ItemMouseDoubleClickHandler 未就绪";
            if (insp == null) return "double click inspect: InspectionUIManager 未就绪";

            var method = typeof(ItemMouseDoubleClickHandler).GetMethod(
                "DoubleClickAction",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (method == null) return "double click inspect: 找不到 DoubleClickAction";

            method.Invoke(handler, new object[] { item, UnityEngine.Vector2.zero });
            bool openedAppraisal = ReferenceEquals(insp.currentInspectingItem, item);

            method.Invoke(handler, new object[] { injector, UnityEngine.Vector2.zero });
            bool openedAuthenticity = ReferenceEquals(insp.currentInspectingItem, injector);

            ok = openedAppraisal && openedAuthenticity;
            return $"double click inspect: appraisal={openedAppraisal} authenticity={openedAuthenticity} " +
                   "(期望 true/true，价格鉴定与真伪鉴定都能打开面板)";
        }
        catch (Exception ex)
        {
            return "double click inspect: 异常 " + ex.GetType().Name + " - " + ex.Message;
        }
        finally
        {
            try { InspectionUIManager.Instance?.CloseUI(); } catch { }
            if (injector != null) { try { injector.Destroy(); } catch { } }
            if (item != null) { try { item.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 上锁容器不能被“容器信息”路径展示；密封箱模块仍应能独立读到内容。
    /// 直接造一个带内容的安全箱并上锁，分别跑两个采集器和完整 tooltip。
    /// </summary>
    private static string CheckLockedContainerGating(out bool ok)
    {
        ok = false;
        GameItem? safe = null;
        GameItem? content = null;
        try
        {
            // trashcan 是自检已确认可创建的网格容器。手动加物资箱的第二套锁标签，
            // 模拟 sec_box 的 LOCKED_CONTAINER_TAG + LOCK_STATUS，不依赖目录里是否有 sec_box。
            safe = TryCreate("trashcan");
            content = TryCreate("scrap_metal");
            if (safe == null) return "locked container gate: 无法创建容器样本";
            if (content == null) return "locked container gate: 无法创建内容样本";

            int remaining = GraphUtils.TryAcceptAll(safe, content);
            safe.EnableTag("LOCKED_CONTAINER_TAG");
            safe.ModifyTag("LOCK_ID", st => st.Enable().SetString("LOCK_ID_SEC"));
            safe.ModifyTag("LOCK_STATUS", st => st.Enable().SetBool(true));

            var storage = StorageInfo.Collect(safe);
            var sealedLines = SealedBoxInfo.Collect(safe);
            string text = ModEntry.RenderTooltipText(safe) ?? "";
            string contains = Loc.T("storage.contains", "Contains: ");
            bool tooltipLeak = contains.Length > 0 && text.Contains(contains, StringComparison.Ordinal);
            bool leaked = storage.Count > 0 || tooltipLeak;
            bool sealedHasContent = sealedLines.Count > 0;
            bool contentAdded = remaining == 0
                && ReferenceEquals(content.parentInventory, safe.contentWindow?.child);

            ok = !leaked && sealedHasContent && contentAdded;
            return $"locked container gate: remaining={remaining} inserted={contentAdded} storage={storage.Count} sealed={sealedLines.Count} " +
                   $"tooltipLeak={tooltipLeak} (期望 0/True/0/>0/False)";
        }
        catch (Exception ex)
        {
            return "locked container gate: 异常 " + ex.GetType().Name + " - " + ex.Message;
        }
        finally
        {
            if (content != null) { try { content.Destroy(); } catch { } }
            if (safe != null) { try { safe.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 容器缓存必须识别堆叠变化和同数量替换；密封箱完整估价不带加号，
    /// 遇到只计数不计价的嵌套容器时，总价必须用“+”表示仍有未计入部分。
    /// </summary>
    private static string CheckContainerCacheRefresh(out bool ok)
    {
        ok = false;
        GameItem? box = null;
        GameItem? content = null;
        GameItem? nested = null;
        try
        {
            box = TryCreate("trashcan");
            content = TryCreate("scrap_metal");
            nested = TryCreate("trashcan");
            if (box == null || content == null || nested == null)
                return "container cache refresh: 无法创建样本";

            int added = GraphUtils.TryAcceptAll(box, content);
            if (added <= 0) return "container cache refresh: 内容物放入失败";

            StorageInfo.Stop();
            _ = StorageInfo.Collect(box, includeRatio: false, includeContents: true);
            content.unitCount = 2;
            var stacked = StorageInfo.Collect(box, includeRatio: false, includeContents: true);
            string stackedText = string.Join("\n", stacked.ConvertAll(l => l.Text));
            bool stackRefreshed = stackedText.Contains("×2", StringComparison.Ordinal);

            box.EnableTag("LOCKED_CONTAINER_TAG");
            box.ModifyTag("LOCK_ID", st => st.Enable().SetString("LOCK_ID_SEC"));
            box.ModifyTag("LOCK_STATUS", st => st.Enable().SetBool(true));
            SealedBoxInfo.Stop();
            var complete = SealedBoxInfo.Collect(box);
            // 合计写在清单行开头：「箱内（合计 140+）：…」，按模板里 {0} 与 {1} 之间的文字定位加号
            string t = Loc.T("sealed.contents_total", "Inside (total {0}): {1}{2}", "\u0001", "\u0002", "");
            int i0 = t.IndexOf('\u0001'), i1 = t.IndexOf('\u0002');
            string totalHead = i0 > 0 ? t.Substring(0, i0) : "\u0001";
            string plusMark = "+" + (i1 > i0 && i0 >= 0 ? t.Substring(i0 + 1, i1 - i0 - 1) : "\u0001");
            string completeTotal = complete.Count > 0 ? complete[0].Text : "";
            bool completeHasNoPlus = completeTotal.StartsWith(totalHead, StringComparison.Ordinal)
                && !completeTotal.Contains(plusMark, StringComparison.Ordinal);

            var grid = box.contentWindow?.child as GameGridInventory;
            bool removed = grid != null && grid.Expel(content);
            int nestedAdded = removed ? GraphUtils.TryAcceptAll(box, nested) : 0;
            var partial = SealedBoxInfo.Collect(box);
            string partialTotal = partial.Count > 0 ? partial[0].Text : "";
            bool replacementRefreshed = partialTotal.Contains(plusMark, StringComparison.Ordinal)
                && partial.Exists(l => l.Text.Contains(
                    ContainerItemSignature.DisplayName(nested), StringComparison.Ordinal));

            ok = stackRefreshed && completeHasNoPlus && replacementRefreshed;
            return $"container cache refresh: stack={stackRefreshed} completeNoPlus={completeHasNoPlus} " +
                   $"replacementPlus={replacementRefreshed} (期望 True/True/True)";
        }
        catch (Exception ex)
        {
            return "container cache refresh: 异常 " + ex.GetType().Name + " - " + ex.Message;
        }
        finally
        {
            StorageInfo.Stop();
            SealedBoxInfo.Stop();
            if (content != null) { try { content.Destroy(); } catch { } }
            if (nested != null) { try { nested.Destroy(); } catch { } }
            if (box != null) { try { box.Destroy(); } catch { } }
        }
    }

    /// <summary>机器电池槽装满电电池时，应输出一行 current/max/percent。</summary>
    private static string CheckMachineBattery(out bool ok)
    {
        ok = false;
        GameItem? machine = null;
        GameItem? battery = null;
        try
        {
            machine = TryCreate("water_purifier");
            if (machine == null) return "machine battery: 无法创建机器样本";

            var slot = MachineHelper.GetBatterySlot(machine);
            if (slot == null) return "machine battery: 样本机器没有电池槽";

            battery = PreBuiltItemHelper.EnergyCredit(100);
            if (battery == null) return "machine battery: 无法创建电池样本";
            try { GeneralHelper.SetItemOwned(battery, true); } catch { }

            int remaining = GraphUtils.TryAcceptAll(slot, battery);
            var tooltip = new RichTextBuilder(80);
            MachineryHelper.CreateMachineryTooltip(tooltip, machine);
            string text = tooltip.Build()?.nakedText ?? "";

            int drawn = GameRead.TagInt(machine, "machinery_drawn_power") ?? 0;
            int baseDrawn = GameRead.TagInt(machine, "MACHINERY_DRAWN_POWER_BASE") ?? 0;
            int days = drawn > 0 ? 100 / drawn : 0;
            string required = "";
            try { required = LocHelper.GetLocalizedMechanic("mech_machinery_required_energy") ?? ""; } catch { }
            string energyLine = Loc.T("machine.energy_label", "Power: ")
                + (baseDrawn > 0 && baseDrawn != drawn ? baseDrawn + "→" : "")
                + drawn + Loc.T("machine.energy_unit", "/day")
                + Loc.T("machine.energy_battery", " ({0} left, {1} days)", 100, days);

            bool merged = text.IndexOf(energyLine, StringComparison.Ordinal) >= 0;
            bool vanillaGone = required.Trim().Length == 0
                || text.IndexOf(required.Trim(), StringComparison.Ordinal) < 0;

            bool inserted = remaining == 0 && ReferenceEquals(slot.childItem, battery);
            ok = inserted && merged && vanillaGone;
            return $"machine battery: remaining={remaining} inserted={inserted} drawn={drawn} base={baseDrawn} " +
                   $"merged={merged} vanillaGone={vanillaGone} text='{text.Replace('\n', ' ')}' " +
                   $"(期望原版所需能量行被合并为「{energyLine}」)";
        }
        catch (Exception ex)
        {
            return "machine battery: 异常 " + ex.GetType().Name + " - " + ex.Message;
        }
        finally
        {
            if (battery != null) { try { battery.Destroy(); } catch { } }
            if (machine != null) { try { machine.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 机器内部：自定义 role、没有电池面板的假机器要能通用列出，且第 0 格的杂物不能被当成电池；
    /// 只有空窗口的 B 类机器什么都不显示；水壶里伪装过的耗尽滤芯要写出真实耐久和"已耗尽"。
    /// </summary>
    private static string CheckMachineInterior(out bool ok)
    {
        ok = false;
        GameItem? custom = null, bare = null, scrap = null, pitcher = null, filter = null;
        try
        {
            const string label = "SelfTestHopper";
            var hopper = new GameSlotInventory();
            custom = ItemDirectory.CreateEmptyItem("itemdetails_selftest_machine")
                .SetContentWindow(MachineHelper.CreateMachineryWindow(false,
                    MachineHelper.Panel(label, hopper, "selftest_hopper")));
            custom.SetGameItemType("MACHINE");
            custom.ModifyTag("machinery_drawn_power", st => st.Enable().SetInt(5));
            scrap = TryCreate("scrap_metal");
            if (scrap == null) return "machine interior: 无法创建内容样本";
            try { GeneralHelper.SetItemOwned(scrap, true); } catch { }
            int added = GraphUtils.TryAcceptAll(hopper, scrap);

            bare = ItemDirectory.CreateEmptyItem("itemdetails_selftest_bare")
                .SetContentWindow(new PixelWindow(false, null, WindowStyle.Machinery));
            bare.SetGameItemType("MACHINE");

            pitcher = TryCreate("water_pitcher");
            filter = TryCreate("water_filter");
            if (pitcher == null || filter == null) return "machine interior: 无法创建水壶或滤芯";
            try { GeneralHelper.SetItemOwned(filter, true); } catch { }
            int max = GameRead.TagInt(filter, "MAX_DURABILITY_TAG") ?? 0;
            DurabilityHelper.SetDurability(filter, 0, max, max, max);
            int filterAdded = GraphUtils.TryAcceptAll(pitcher, filter);

            MachineInterior.Stop();
            var customLines = MachineInterior.Collect(custom, true, true, true, false);
            var bareLines = MachineInterior.Collect(bare, true, true, true, false);
            var pitcherLines = MachineInterior.Collect(pitcher, true, true, true, false);
            var battery = MachineBatteryInfo.Collect(custom);

            bool listed = HasLine(customLines, label)
                && HasLine(customLines, ContainerItemSignature.DisplayName(scrap));
            bool noBattery = battery == null;
            bool bareSilent = bareLines.Count == 0;
            string spent = Loc.T("inner.filter_spent", " (spent, no longer filters)").Trim();
            bool filterSpent = HasLine(pitcherLines, ContainerItemSignature.DisplayName(filter))
                && HasLine(pitcherLines, spent);

            ok = added > 0 && filterAdded > 0 && listed && noBattery && bareSilent && filterSpent;
            string Join(List<TooltipLine> ls) => string.Join(" | ", ls.ConvertAll(l => l.Text));
            return $"machine interior: added={added}/{filterAdded} listed={listed} noBattery={noBattery} " +
                   $"bareSilent={bareSilent} filterSpent={filterSpent} (期望 >0/>0/True/True/True/True) " +
                   $"custom='{Join(customLines)}' pitcher='{Join(pitcherLines)}'";
        }
        catch (Exception ex)
        {
            return "machine interior: 异常 " + ex.GetType().Name + " - " + ex.Message;
        }
        finally
        {
            MachineInterior.Stop();
            foreach (var it in new[] { scrap, filter, custom, bare, pitcher })
                if (it != null) { try { it.Destroy(); } catch { } }
        }
    }

    /// <summary>自学习进度只读 LEARNING_ALGO_MAX_* 标签：上限任意，被禁用的那一项不显示。</summary>
    private static string CheckModuleLearning(out bool ok)
    {
        ok = false;
        GameItem? module = null;
        try
        {
            module = TryCreate("scrap_metal");
            if (module == null) return "module learning: 无法创建样本";

            module.ModifyTag("LEARNING_ALGO_MAX_PERFORMANCE_INT", st => st.Enable().SetInt(37));
            module.ModifyTag("BONUS_PERCENTAGE_PERFORMANCE_INT", st => st.Enable().SetInt(12));
            module.ModifyTag("LEARNING_ALGO_MAX_QUALITY_INT", st => st.Enable().SetInt(16));
            module.ModifyTag("BONUS_PERCENTAGE_QUALITY_INT", st => st.Enable().SetInt(16));
            module.ModifyTag("LEARNING_ALGO_MAX_EFFICIENCY_INT", st => st.SetInt(50));
            module.DisableTag("LEARNING_ALGO_MAX_EFFICIENCY_INT");

            var lines = ModuleLearning.Collect(module);
            string text = lines.Count > 0 ? lines[0].Text : "";
            int remaining = ModuleLearning.Remaining(module);
            bool shown = text.Contains("12/37", StringComparison.Ordinal);
            bool noDisabled = !text.Contains("/50", StringComparison.Ordinal);
            bool remainingOk = remaining == 25;

            ok = lines.Count == 1 && shown && noDisabled && remainingOk;
            return $"module learning: line='{text}' remaining={remaining} " +
                   "(期望含 12/37、不含 /50、remaining=25)";
        }
        catch (Exception ex)
        {
            return "module learning: 异常 " + ex.GetType().Name + " - " + ex.Message;
        }
        finally
        {
            if (module != null) { try { module.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 加成注释差分：原版机器按能力表调用游戏函数；未登记机器不写；
    /// 第三方显式注入优先；全程不改真实机器。
    /// </summary>
    private static string CheckModuleStatEffects(out bool ok)
    {
        ok = false;
        GameItem? projector = null, purifier = null;
        IDisposable? injection = null;
        try
        {
            projector = TryCreate("mirage_projector");
            purifier = TryCreate("water_purifier");
            if (projector == null || purifier == null) return "module stat effects: 无法创建样本";

            projector.ModifyTag("TOTAL_PERCENTAGE_QUALITY_BONUS_INT", st => st.Enable().SetInt(40));
            projector.ModifyTag("TOTAL_PERCENTAGE_POWER_BONUS_INT", st => st.Enable().SetInt(30));
            ModuleHelper.ApplyBasicModuleEffect(null!, null!, projector);
            int expectAttract = MachineProjector.GetAttractivenessBoost(projector);
            projector.ModifyTag("TOTAL_PERCENTAGE_QUALITY_BONUS_INT", st => st.SetInt(0));
            expectAttract -= MachineProjector.GetAttractivenessBoost(projector);
            projector.ModifyTag("TOTAL_PERCENTAGE_QUALITY_BONUS_INT", st => st.SetInt(40));
            int drawnBefore = GameRead.TagInt(projector, "machinery_drawn_power") ?? -1;

            ModuleStatEffects.Stop();
            string q = ModuleStatEffects.Note('q', projector);
            string e = ModuleStatEffects.Note('e', projector);
            bool attractOk = q.Contains("+" + expectAttract, StringComparison.Ordinal);
            bool effOk = e.StartsWith("-", StringComparison.Ordinal) && !e.StartsWith("-0", StringComparison.Ordinal);
            bool untouched = GameRead.TagInt(projector, "machinery_drawn_power") == drawnBefore
                && GameRead.TagInt(projector, "TOTAL_PERCENTAGE_QUALITY_BONUS_INT") == 40;

            purifier.ModifyTag("TOTAL_PERCENTAGE_QUALITY_BONUS_INT", st => st.Enable().SetInt(60));
            ModuleStatEffects.Stop();
            string pq = ModuleStatEffects.Note('q', purifier);
            double mult = MachinePurifier.GetPurityMult(60);
            bool residualOk = pq.Contains(mult.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

            // 模拟涡轮增压等外部改动：耗电不再符合原版公式，效率注释应消失
            projector.ModifyTag("machinery_drawn_power", st => st.SetInt(drawnBefore + 7));
            ModuleStatEffects.Stop();
            bool mismatchSilent = ModuleStatEffects.Note('e', projector).Length == 0;

            // 未登记 ID 不走通用猜测；显式注册后由提供方接管，注销恢复原版表。
            string vanillaId = projector.identifier;
            projector.identifier = "itemdetails_unregistered_machine";
            ModuleStatEffects.Stop();
            bool unregisteredSilent = ModuleStatEffects.Note('q', projector).Length == 0;
            projector.identifier = vanillaId;

            injection = ItemDetailsApi.RegisterMachineEffects(vanillaId,
                (_, stat) => stat == ItemDetailsModuleStat.Quality ? "injected-quality" : null);
            bool injected = ModuleStatEffects.Note('q', projector) == "injected-quality";
            injection.Dispose();
            injection = null;
            ModuleStatEffects.Stop();
            bool restored = ModuleStatEffects.Note('q', projector).Length > 0;

            ok = attractOk && effOk && untouched && residualOk && mismatchSilent
                && unregisteredSilent && injected && restored;
            return $"module stat effects: q='{q}' e='{e}' purifier='{pq}' attract={attractOk} eff={effOk} " +
                   $"untouched={untouched} residual={residualOk} mismatchSilent={mismatchSilent} " +
                   $"unregisteredSilent={unregisteredSilent} injected={injected} restored={restored}";
        }
        catch (Exception ex)
        {
            return "module stat effects: 异常 " + ex.GetType().Name + " - " + ex.Message;
        }
        finally
        {
            injection?.Dispose();
            ModuleStatEffects.Stop();
            foreach (var it in new[] { projector, purifier })
                if (it != null) { try { it.Destroy(); } catch { } }
        }
    }

    private static bool HasLine(List<TooltipLine> lines, string keyword)
    {
        foreach (var l in lines)
            if (!string.IsNullOrEmpty(l.Text) && l.Text.Contains(keyword, StringComparison.Ordinal)) return true;
        return false;
    }
    private static string CheckTagIsolation(out bool ok)
    {
        ok = false;
        try
        {
            var real = new TagSystem();
            real.EnableTag("TEST_TAG_ISO").GetTag("TEST_TAG_ISO").SetInt(5);

            var clone = GameRead.DeepCloneTags(real);
            if (clone == null) return "tag isolation: production clone failed";

            clone.EnableTag("TEST_TAG_ISO").GetTag("TEST_TAG_ISO").SetInt(9);

            int realValue = real.GetTag("TEST_TAG_ISO").GetInt();
            int cloneValue = clone.GetTag("TEST_TAG_ISO").GetInt();

            ok = realValue == 5 && cloneValue == 9;
            return $"tag isolation: real={realValue} clone={cloneValue} (期望 5/9)";
        }
        catch (Exception ex)
        {
            return "tag isolation: 异常 " + ex.GetType().Name;
        }
    }
    /// <summary>
    /// 验证泛型分解依赖的探针机制（离线测不到的部分）：
    ///   1. 造同类型探针 → 克隆真实物品全部标签 → 调游戏函数，应等于直接读真实值
    ///   2. 改一个标签 → 值应按游戏公式变化（消融测量依赖这条链路）
    /// </summary>
    private static string CheckProbeMechanism(out bool ok)
    {
        ok = false;
        try
        {
            var real = TryCreate("plasma_fuel");
            if (real == null) return "probe: 无法创建基准物品";

            PlasmaHelper.InitPlasmaItem(real);
            real.ModifyTag("PLASMA_CASING_CONDITION_WEAR", t => t.Enable().SetString("WEAR_HEAVY"));
            real.ModifyTag("PLASMA_SEAL", t => t.Enable().SetString("NONE"));

            int direct = AppraisalHelper.GetCorrectBaseValue(real);

            var probe = DirectoryMaster.Create<GameItem>("plasma_fuel");
            if (probe == null) return "probe: 无法创建探针";

            var clonedBench = GameRead.DeepCloneTags(real.modifiedState);
            if (clonedBench == null) return "probe: 标签克隆失败";
            probe.modifiedState = clonedBench;
            probe.state = probe.modifiedState;
            int cloned = PlasmaHelper.GetCorrectValue(probe);

            // 重度磨损 -90：改成 NONE 后应 +90
            probe.ModifyTag("PLASMA_CASING_CONDITION_WEAR", t => t.Enable().SetString("NONE"));
            int afterChange = PlasmaHelper.GetCorrectValue(probe);

            ok = direct == cloned && afterChange == direct + 90;
            return $"probe: direct={direct} cloned={cloned} afterChange={afterChange} " +
                   $"(期望 {direct}/{direct}/{direct + 90})";
        }
        catch (Exception ex)
        {
            return "probe: 异常 " + ex.GetType().Name + " - " + ex.Message;
        }
    }

    private static string Evaluate(Case c, out bool ok)
    {
        ok = false;
        try
        {
            if (c.Item == null) { return $"{c.Name}: 物品创建失败"; }

            int? gameVal = GameRead.TrueValue(c.Item);
            var ours = ValueBreakdown.Analyze(c.Item);
            int ourVal = ours?.Total ?? -999;
            string parts = ours == null ? "(未产出拆解)" : Describe(ours);

            if (gameVal == null)
                return $"{c.Name}: 游戏值读取失败 ours={ourVal}";

            bool contract = ourVal == gameVal.Value;
            bool expected = !c.Expected.HasValue || gameVal.Value == c.Expected.Value;
            ok = contract && expected;

            string expect = c.Expected.HasValue ? $" expect={c.Expected.Value}" : "";
            return $"{c.Name}: game={gameVal.Value} ours={ourVal}{expect} parts=[{parts}]";
        }
        catch (Exception ex)
        {
            return $"{c.Name}: 异常 {ex.GetType().Name} - {ex.Message}";
        }
    }

    private static string Describe(ValueBreakdown.Result r)
    {
        var sb = new StringBuilder();
        foreach (var p in r.Parts)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(p.En).Append('=').Append(p.Amount);
        }
        return sb.ToString();
    }

    // ---------------- 等离子 ----------------

    private static void AddPlasmaCases(List<Case> list)
    {
        // 默认封印是 LL + 标准色 → 300 + 60 = 360
        list.Add(Plasma("plasma/default-LL-standard", "NONE", "NONE", "NONE", "NONE", "STANDARD", "LL", 360));
        // 重度磨损、无封印 → 300 - 90 = 210（与实测截图一致）
        list.Add(Plasma("plasma/wear_heavy-no-seal", "WEAR_HEAVY", "NONE", "NONE", "NONE", "STANDARD", "NONE", 210));
        // 破裂 + 低级颜色 → 0 → 取下限 90
        list.Add(Plasma("plasma/crack-poor-clamp", "NONE", "CRACK", "NONE", "NONE", "POOR1", "NONE", 90));
        // 点火实验室 + 高级颜色 → 300 + 150 + 750 = 1200
        list.Add(Plasma("plasma/ignition-lab-high", "NONE", "NONE", "NONE", "NONE", "HIGH_QUALITY", "IGNITION_LAB", 1200));
        // 磨损 + 螺丝外露 + 加固 → 300 - 45 - 30 + 60 = 285
        list.Add(Plasma("plasma/wear-uncovered-reinforced", "WEAR", "NONE", "UNCOVERED", "REINFORCED", "STANDARD", "NONE", 285));
        // 中华船舶：全 NONE + 标准色 → 300 + 120 = 420
        list.Add(Plasma("plasma/zhonghua", "NONE", "NONE", "NONE", "NONE", "STANDARD", "ZHONGHUA", 420));
    }

    private static Case Plasma(string name, string wear, string crack, string uncov, string reinf,
                               string color, string seal, int expected)
    {
        GameItem? it = TryCreate("plasma_fuel");
        if (it != null)
        {
            try
            {
                PlasmaHelper.InitPlasmaItem(it);
                SetStr(it, "PLASMA_CASING_CONDITION_WEAR", wear);
                SetStr(it, "PLASMA_CASING_CONDITION_CRACK", crack);
                SetStr(it, "PLASMA_CASING_CONDITION_UNCOVERED", uncov);
                SetStr(it, "PLASMA_CASING_CONDITION_REINFORCED", reinf);
                SetStr(it, "PLASMA_COLOR", color);
                SetStr(it, "PLASMA_SEAL", seal);
            }
            catch (Exception ex) { Log.Warn("SelfTest.Plasma", ex); }
        }
        return new Case { Name = name, Item = it, Expected = expected };
    }

    // ---------------- 机械手 ----------------

    private static void AddHandCases(List<Case> list)
    {
        list.Add(Hand("hand/plain", "ALUMINUM_ALLOY", "EMG", "NONE", "NONE", "NONE", 100));
        // 战术组合：100 + 50 + 45 + 50 + 80 + 100 = 425
        list.Add(Hand("hand/tactical-set", "CARBON_COMPOSITE", "EMG_PLUS", "NONE", "ARMORED", "BLADED", 425));
        // 豪华组合：100 + 200 + 120 + 200 = 620
        list.Add(Hand("hand/deluxe-set", "GOLD_PLATED", "NEURAL", "NONE", "NONE", "NONE", 620));
    }

    private static Case Hand(string name, string mat, string iface, string cond, string armored, string bladed, int expected)
    {
        GameItem? it = TryCreate("hand");
        if (it != null)
        {
            try
            {
                it.EnableTag("HAND_TAG");
                SetStr(it, "HAND_MATERIAL", mat);
                SetStr(it, "HAND_INTERFACE", iface);
                SetStr(it, "HAND_CONDITION", cond);
                SetStr(it, "HAND_ADDON_ARMORED", armored);
                SetStr(it, "HAND_ADDON_BLADED", bladed);
            }
            catch (Exception ex) { Log.Warn("SelfTest.Hand", ex); }
        }
        return new Case { Name = name, Item = it, Expected = expected };
    }

    // ---------------- 肋明治 ----------------

    private static void AddRibwichCases(List<Case> list)
    {
        // 已开封直接固定 40
        list.Add(Ribwich("ribwich/opened-wrap1", 1, 0, 0, 0, 5, 430, 3, 40));
        // 普通未开封：基础 150 + 厂号 3（非高非低）= 150
        list.Add(Ribwich("ribwich/plain", 0, 0, 0, 0, 5, 432, 3, 150));   // 432 属第二批次；430 是无效年份会被判赝品
    }

    private static Case Ribwich(string name, int wrap, int meat, int bun, int pickles,
                                 int month, int year, int fac, int? expected)
    {
        GameItem? it = TryCreate("ribwich");
        if (it != null)
        {
            try
            {
                it.EnableTag("RIBWICH_TAG");
                SetInt(it, "RIBWICH_WRAP_TAG", wrap);
                SetInt(it, "RIBWICH_MEAT_TAG", meat);
                SetInt(it, "RIBWICH_BUN_TAG", bun);
                SetInt(it, "RIBWICH_PICKLES_TAG", pickles);
                SetInt(it, "RIBWICH_PRODUCTION_MONTH_TAG", month);
                SetInt(it, "RIBWICH_PRODUCTION_YEAR_TAG", year);
                SetInt(it, "RIBWICH_FACILITY_NUMBER_TAG", fac);

                // 补上防伪标记，否则游戏会按赝品判（固定 40），用例就测不到真值公式：
                // 达夫雷标志 = 0；第一批次(425–429) 的 Ribwich 贴纸必须为 -1，第二批次(432–438) 为 0。
                SetInt(it, "RIBWICH_DAVRE_LOGO_TAG", 0);
                SetInt(it, "RIBWICH_PRODUCTION_TAG", 0);
                SetInt(it, "RIBWICH_FACILITY_TAG", 0);
                bool firstRun = year >= 425 && year <= 429;
                SetInt(it, "RIBWICH_RIBWICH_LOGO_TAG", firstRun ? -1 : 0);
            }
            catch (Exception ex) { Log.Warn("SelfTest.Ribwich", ex); }
        }
        return new Case { Name = name, Item = it, Expected = expected };
    }

    // ---------------- 工具 ----------------

    private static GameItem? TryCreate(string id)
    {
        try { return DirectoryMaster.Create<GameItem>(id); }
        catch (Exception ex) { Log.Warn("SelfTest.Create." + id, ex); return null; }
    }

    /// <summary>
    /// 多语种回归护栏：确认当前语言的 Localization/&lt;code&gt;.csv 真被读到、键能解析。
    ///
    /// 只判「有没有」，不比对具体译文——译文可以随时改，改译文不该让自检变红。
    /// en 是设计上的例外：英文就是代码里的兜底，本来就没有 CSV。
    /// </summary>
    private static string CheckLocalization(out bool ok)
    {
        ok = false;
        try
        {
            string code = Loc.Code;
            if (code.Length == 0)
                return "loc: 读不到 locale（主菜单早期或本地化未就绪），本项跳过";

            int entries = Loc.TableEntries;
            string genuine = Loc.T("auth.genuine", "√ Genuine");
            string counter = Loc.T("auth.counterfeit", "× Counterfeit ({0})", "--");
            bool noRawKey = genuine != "auth.genuine" && counter != "auth.counterfeit";

            if (code == "en")
            {
                ok = noRawKey;
                return $"loc: locale=en 走代码内英文兜底（无 CSV），样例='{genuine}' noRawKey={noRawKey}";
            }

            bool translated = entries > 0;
            ok = translated && noRawKey;
            return $"loc: locale={code} 词条={entries} 样例='{genuine}' translated={translated} noRawKey={noRawKey}";
        }
        catch (Exception ex)
        {
            return "loc: 异常 " + ex.GetType().Name;
        }
    }

    private static void SetStr(GameItem item, string tag, string value)
    {
        item.ModifyTag(tag, st => st.Enable().SetString(value));
    }

    private static void SetInt(GameItem item, string tag, int value)
    {
        item.ModifyTag(tag, st => st.Enable().SetInt(value));
    }
}
#endif
