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
    /// 验证"探针改写不影响真实物品"：模拟 GenericBreakdown 的 Apply 路径
    /// （深拷贝标签 → 在探针上 ModifyTag），再比对真实物品标签有无被改动。
    /// 这条与 CheckTagIsolation 互补：那条测 TagSystem 层，这条测整条探针路径。
    /// </summary>
    private static string CheckGenericDoesNotMutate(out bool ok)
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

            // 与 GenericBreakdown.Apply 相同的写法
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
        try
        {
            var normal = WineHelper.CreateFinishedWineForDebug(5);
            var nLines = WineInfo.Collect(normal, true);
            bool hasScore = HasLine(nLines, "酒分数");
            bool normalClean = !HasLine(nLines, "赝品");

            var fake = WineHelper.CreateFinishedWineForDebug(5, counterfeit: true);
            var fLines = WineInfo.Collect(fake, true);
            bool fakeMarked = HasLine(fLines, "赝品");

            ok = hasScore && normalClean && fakeMarked;
            return $"wine: score={hasScore} normalClean={normalClean} fakeMarked={fakeMarked} (期望 true/true/true)";
        }
        catch (Exception ex)
        {
            return "wine: 异常 " + ex.GetType().Name;
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

            // 与 WaterAdvice.DeepCloneTags 相同的做法：逐条 Clone
            var clone = new TagSystem();
            foreach (var kv in real.dict)
                if (kv.Value != null) clone.dict[kv.Key] = kv.Value.Clone();

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
