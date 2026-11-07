#if ITEMDETAILS_SELFTEST
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace ItemDetails;

internal static partial class SelfTest
{
    private static string NativeTooltip(GameItem item)
    {
        var method = AccessTools.Method(typeof(GameItemElement), "GetTooltipBasic", Type.EmptyTypes)
            ?? throw new MissingMethodException("GameItemElement.GetTooltipBasic");
        var builder = method.Invoke(item, null) as RichTextBuilder
            ?? throw new InvalidOperationException("Native tooltip returned no builder.");
        return builder.Build()?.nakedText ?? "";
    }

    private static string CheckAppraisalDisabledPrice(out bool ok)
    {
        ok = false;
        GameItem? item = null;
        var setting = ModConfig.Find("item_details")?.Get("show_valuation");
        object? previous = setting?.BoxedValue;
        var apply = AccessTools.Method(typeof(ConfigEntry), "ApplyBoxed");
        try
        {
            if (setting == null || apply == null) return "native appraisal price: missing configuration";
            item = TryCreate("plasma_fuel");
            if (item == null) return "native appraisal price: cannot create sample";
            PlasmaHelper.InitPlasmaItem(item);
            if (ValueBreakdown.Analyze(item) == null) return "native appraisal price: unsupported sample";
            apply.Invoke(setting, new object[] { false });
            string expected = Loc.T("price.sell", "Sell: {0}", ItemValueCache.Get(item).Sell);
            string text = NativeTooltip(item);
            ok = text.Contains(expected, StringComparison.Ordinal);
            return $"native appraisal price: replacementDisabled={!ModSettings.Value} priceVisible={ok}";
        }
        catch (Exception ex) { return "native appraisal price: " + ex; }
        finally
        {
            try
            {
                if (setting != null && apply != null && previous != null) apply.Invoke(setting, new[] { previous });
            }
            finally { if (item != null) { try { item.Destroy(); } catch { } } }
        }
    }

    private static string CheckNativeCalorieOrigin(out bool ok)
    {
        ok = false;
        GameItem? item = null;
        try
        {
            item = TryCreate("scrap_metal");
            if (item == null) return "native calorie origin: cannot create sample";
            item.shortDescription = "Durability: 100%";
            item.ModifyTag("CALORIE_VALUE_TAG", state => state.Enable().SetInt(100));
            bool both = true;
            foreach (bool meat in new[] { false, true })
            {
                item.ModifyTag("MEAT_FOOD_TAG", state => state.SetEnabled(meat));
                string native = LocHelper.GetLocalizedMechanic(meat ? "mech_food_calories_meat" : "mech_food_calories", 100);
                int at = native.IndexOf("100", StringComparison.Ordinal);
                if (at < 0) return "native calorie origin: native template lacks calorie value";
                string tail = native.Substring(at + 3).Trim();
                if (tail.Length == 0) tail = Loc.T("calorie.unit", "kcal");
                string expected = Loc.T("calorie.line", "Energy: {0} {1}", 100, tail);
                string text = NativeTooltip(item);
                both &= text.Contains("Durability: 100%", StringComparison.Ordinal)
                    && text.Contains(expected, StringComparison.Ordinal);
            }
            ok = both;
            return $"native calorie origin: nativeFoodRewrittenAndDescriptionPreserved={both}";
        }
        catch (Exception ex) { return "native calorie origin: " + ex; }
        finally { if (item != null) { try { item.Destroy(); } catch { } } }
    }

    private static string CheckCalorieCreditIcon(out bool ok)
    {
        ok = false;
        GameItem? item = null;
        try
        {
            item = TryCreate("scrap_metal");
            if (item == null) return "calorie credit icon: sample missing";
            item.ModifyTag("CALORIE_VALUE_TAG", tag => tag.Enable().SetInt(180));
            var tooltip = ((GameItemElement)item).GetTooltip();
            string rich = tooltip.richTextForeground ?? "";
            string icon = GeneralHelper.CreditIcon();
            bool sprite = rich.Contains("/" + icon, StringComparison.Ordinal);
            bool noWord = !rich.Contains("/信用点", StringComparison.Ordinal) && !rich.Contains("/credit", StringComparison.Ordinal);
            ok = sprite && noWord;
            ModHelper.WriteModDataFileText("item_details", "calorie-credit-icon.txt", rich);
            return $"calorie credit icon: nativeSprite={sprite} noCurrencyWord={noWord} text='{tooltip.nakedText}'";
        }
        catch (Exception ex) { return "calorie credit icon: " + ex; }
        finally { if (item != null) { try { item.Destroy(); } catch { } } }
    }

    private static void FillTestWater(GameItem item, int waterParts, string contaminant)
    {
        foreach (var liquid in Liquid.Liquids)
            item.ModifyTag(liquid.currentPart, state => state.Enable().SetInt(0));
        item.ModifyTag(Liquid.GetCurrentPartFromId("water"), state => state.Enable().SetInt(waterParts));
        item.ModifyTag(Liquid.GetCurrentPartFromId(contaminant), state => state.Enable().SetInt(1000000 - waterParts));
        WaterFeatureHelper.InitWaterFeature(item);
    }

    private static bool SameMethods(List<WaterAdvice.Method> left, List<WaterAdvice.Method> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (left[i].Name != right[i].Name || Math.Abs(left[i].GainPct - right[i].GainPct) > 0.000001) return false;
        return true;
    }

    private static string CheckWaterAdviceInstalledFilter(out bool ok)
    {
        ok = false;
        var items = new List<GameItem>();
        try
        {
            var pitchers = new List<GameItem>();
            var filters = new List<GameItem>();
            foreach (int durability in new[] { 400, 0 })
            {
                var pitcher = TryCreate("water_pitcher");
                if (pitcher == null) return "installed filter cache: cannot create pitcher";
                items.Add(pitcher);
                var filter = TryCreate("water_filter");
                if (filter == null) return "installed filter cache: cannot create filter";
                items.Add(filter);
                GeneralHelper.SetItemOwned(pitcher, true);
                GeneralHelper.SetItemOwned(filter, true);
                filter.ModifyTag("DURABILITY_TAG", state => state.Enable().SetInt(durability));
                if (GraphUtils.TryAcceptAll(pitcher, filter) != 0 || !ReferenceEquals(WaterHelper.GetFilterItem(pitcher), filter))
                    return "installed filter cache: filter insertion failed";
                FillTestWater(pitcher, 990000, "organic_waste");
                pitchers.Add(pitcher);
                filters.Add(filter);
            }
            WaterAdvice.Stop();
            var healthy = WaterAdvice.SimulateMethods(pitchers[0]);
            var cachedExhausted = WaterAdvice.SimulateMethods(pitchers[1]);
            WaterAdvice.Stop();
            var uncachedExhausted = WaterAdvice.SimulateMethods(pitchers[1]);
            // 耗尽滤芯应与未安装滤芯一样回退到拥有物；同名建议可能来自另一支可用滤芯。
            var noFilter = TryCreate("water_pitcher");
            if (noFilter == null) return "installed filter cache: cannot create fallback baseline";
            items.Add(noFilter);
            GeneralHelper.SetItemOwned(noFilter, true);
            FillTestWater(noFilter, 990000, "organic_waste");
            var fallback = WaterAdvice.SimulateMethods(noFilter);
            bool gained = healthy.Exists(method => method.Name == GameRead.ItemDisplayName(filters[0]) && method.GainPct > 0);
            bool exhausted = SameMethods(uncachedExhausted, fallback);
            bool sharingCorrect = SameMethods(cachedExhausted, uncachedExhausted);

            WaterAdvice.Stop();
            _ = WaterAdvice.SimulateMethods(pitchers[0]);
            filters[0].ModifyTag("DURABILITY_TAG", state => state.Enable().SetInt(0));
            var afterWear = WaterAdvice.SimulateMethods(pitchers[0]);
            bool wearUpdated = SameMethods(afterWear, uncachedExhausted);
            ok = gained && exhausted && sharingCorrect && wearUpdated;
            return $"installed filter cache: healthyGain={gained} exhaustedMatchesFallback={exhausted} cachedMatchesUncached={sharingCorrect} wearUpdated={wearUpdated}";
        }
        catch (Exception ex) { return "installed filter cache: " + ex; }
        finally
        {
            WaterAdvice.Stop();
            for (int i = items.Count - 1; i >= 0; i--) { try { items[i].Destroy(); } catch { } }
        }
    }

    /// <summary>密封箱标题估价：完整总价不带 +，嵌套容器带 +，堆叠变化立即刷新。</summary>
    private static string CheckSealedBoxTitleEstimate(out bool ok)
    {
        ok = false;
        var items = new List<GameItem>();
        try
        {
            var container = TryCreate("storage_bay");
            var child = TryCreate("scrap_metal");
            var nested = TryCreate("water_pitcher");
            if (container == null || child == null || nested == null)
                return "sealed title estimate: cannot create samples";
            items.Add(container);
            items.Add(child);
            items.Add(nested);
            OwnAll(container, child, nested);
            child.unitValue = 100;
            if (GraphUtils.TryAcceptAll(container, child) != 0)
                return "sealed title estimate: child insertion failed";

            StorageInfo.Stop();
            bool storageTextSilent = StorageInfo.Collect(container,
                includeRatio: false, includeContents: true).Count == 0;
            container.EnableTag("LOCKED_CONTAINER_TAG");
            container.ModifyTag("LOCK_STATUS", state => state.Enable().SetBool(true));
            var grid = container.contentWindow?.child as GameGridInventory;
            if (grid == null) return "sealed title estimate: missing grid";

            SealedBoxInfo.Stop();
            string complete = SealedBoxInfo.EstimateTitleValue(container, grid, grid.childItems);
            if (GraphUtils.TryAcceptAll(container, nested) != 0)
                return "sealed title estimate: nested insertion failed";
            string partial = SealedBoxInfo.EstimateTitleValue(container, grid, grid.childItems);
            child.unitCount = 2;
            string stacked = SealedBoxInfo.EstimateTitleValue(container, grid, grid.childItems);

            ok = storageTextSilent && complete == "100" && partial == "100+" && stacked == "200+";
            return $"sealed title estimate: textSilent={storageTextSilent} complete={complete} " +
                   $"partial={partial} stacked={stacked} (期望 True/100/100+/200+)";
        }
        catch (Exception ex) { return "sealed title estimate: " + ex; }
        finally
        {
            StorageInfo.Stop();
            SealedBoxInfo.Stop();
            for (int i = items.Count - 1; i >= 0; i--) { try { items[i].Destroy(); } catch { } }
        }
    }

    /// <summary>第三方动态锚点使用真实 RectTransform；null 与注销都回退原版解析。</summary>
    private static string CheckTooltipAnchorInjection(out bool ok)
    {
        ok = false;
        UnityEngine.GameObject? go = null;
        IDisposable? registration = null;
        try
        {
            go = new UnityEngine.GameObject("ItemDetailsAnchorSelfTest", typeof(UnityEngine.RectTransform));
            var rect = go.GetComponent<UnityEngine.RectTransform>();
            UnityEngine.RectTransform? current = rect;
            registration = ItemDetailsApi.RegisterTooltipAnchor(() => current);
            bool present = ReferenceEquals(TypeBadge.ResolveTooltipAnchor(null), rect);
            current = null;
            bool nullFallback = TypeBadge.ResolveTooltipAnchor(null) == null;
            registration.Dispose();
            registration = null;
            bool removed = TypeBadge.ResolveTooltipAnchor(null) == null;
            ok = present && nullFallback && removed;
            return $"tooltip anchor injection: present={present} nullFallback={nullFallback} removed={removed} (期望全部 True)";
        }
        catch (Exception ex) { return "tooltip anchor injection: " + ex; }
        finally
        {
            registration?.Dispose();
            if (go != null) { try { UnityEngine.Object.Destroy(go); } catch { } }
        }
    }

    /// <summary>在真实游戏 UI 中检查第四组、实时切换和重置；不应用或写盘。</summary>
    private static string CheckInfoSettingsWindow(out bool ok)
    {
        ok = false;
        var config = ModConfig.Find("item_details");
        var setting = config?.Get("show_info_enhance");
        var apply = AccessTools.Method(typeof(ConfigEntry), "ApplyBoxed");
        if (config == null || setting == null || apply == null) return "info settings UI: config missing";
        object saved = setting.BoxedValue;
        CustomUIWindow? window = null;
        try
        {
            var mod = new LoadedMod { Manifest = new ModManifest { ID = "item_details", Name = "Item Details" } };
            apply.Invoke(setting, new object[] { true });
            window = ModConfigWindow.Open(mod);
            if (window == null) return "info settings UI: window missing";
            string section = config.Get("info_price_modifiers").Section;
            var header = window.Get("section_" + section).root.transform.parent.gameObject;
            var master = window.Get("show_info_enhance");
            bool shown = header.activeSelf;
            string[] keys = { "info_price_modifiers", "info_black_market", "info_lottery", "info_brewing", "info_meat",
                "info_machines", "info_batteries", "info_progress_bar", "info_container_units", "info_manufacturer_inline" };
            bool defaults = true;
            foreach (string key in keys) defaults &= config.Get(key).BoxedDefault is true;
            master.SetBool(false, notify: true);
            bool hidden = !header.activeSelf;
            foreach (string key in keys) hidden &= !window.Get(key).root.transform.parent.gameObject.activeSelf;
            window.Get("info_meat").SetBool(false);
            master.SetBool(true, notify: true);
            bool preserved = header.activeSelf && !window.Get("info_meat").GetBool();
            master.SetBool(false, notify: true);
            var reset = AccessTools.Method(typeof(ModConfigWindow), "ResetWidgets",
                new[] { typeof(CustomUIWindow), typeof(ModConfig) });
            if (reset == null) return "info settings UI: ResetWidgets missing";
            reset.Invoke(null, new object[] { window, config });
            bool resetShown = header.activeSelf && master.GetBool() && window.Get("info_meat").GetBool();
            window.Close();
            window = null;
            apply.Invoke(setting, new object[] { false });
            window = ModConfigWindow.Open(mod);
            bool reopenHidden = window != null && !window.Get("section_" + config.Get(keys[0]).Section)
                .root.transform.parent.gameObject.activeSelf;
            bool unapplied = !ModSettings.InfoEnhance;
            ok = defaults && shown && hidden && preserved && resetShown && reopenHidden && unapplied;
            return $"info settings UI: defaults={defaults} shown={shown} hidden={hidden} preserved={preserved} " +
                $"reset={resetShown} reopenHidden={reopenHidden} unapplied={unapplied}";
        }
        catch (Exception ex) { return "info settings UI: " + ex; }
        finally
        {
            try { window?.Close(); } catch { }
            apply.Invoke(setting, new[] { saved });
        }
    }

    /// <summary>信息增强组位于透视后；全部设置关闭时 AnyEnabled 与渲染输出都为空。</summary>
    private static string CheckSettingsLayoutAndDisable(out bool ok)
    {
        ok = false;
        var config = ModConfig.Find("item_details");
        if (config == null) return "settings layout/disable: config missing";
        var apply = AccessTools.Method(typeof(ConfigEntry), "ApplyBoxed");
        if (apply == null) return "settings layout/disable: ApplyBoxed missing";
        var saved = new List<(ConfigEntry Entry, object Value)>();
        GameItem? item = null;
        try
        {
            ModSettings.RefreshSections();
            string infoSection = Loc.T("config.section.info", ModSettings.SecInfoEn);
            string[] displayKeys =
            {
                "show_info_enhance", "show_cost_performance", "show_container_contents",
                "tooltip_text_formatting", "show_type_badge", "mute_type_badge_colors",
                "show_container_name_display", "container_name_bg_opacity",
            };
            string[] infoKeys =
            {
                "info_price_modifiers", "info_black_market", "info_lottery", "info_brewing", "info_meat",
                "info_machines", "info_batteries", "info_progress_bar", "info_container_units", "info_manufacturer_inline",
            };
            bool grouped = true;
            foreach (string key in infoKeys)
            {
                var entry = config.Get(key);
                if (entry == null || entry.Section != infoSection) grouped = false;
            }
            foreach (string key in displayKeys)
            {
                var entry = config.Get(key);
                if (entry == null || entry.Section != config.Get("show_customer_history")?.Section) grouped = false;
            }
            int reveal = -1, info = -1;
            for (int i = 0; i < config.Entries.Count; i++)
            {
                if (ReferenceEquals(config.Entries[i], config.Get("result_prefill_price"))) reveal = i;
                if (ReferenceEquals(config.Entries[i], config.Get("info_price_modifiers"))) info = i;
            }
            bool ordered = reveal >= 0 && info > reveal;

            foreach (var entry in config.Entries)
            {
                saved.Add((entry, entry.BoxedValue));
                if (entry.ValueType == typeof(bool)) apply.Invoke(entry, new object[] { false });
                else if (ReferenceEquals(entry, config.Get("liquid_analysis")))
                    apply.Invoke(entry, new object[] { ModSettings.LiquidChoiceValue(ModSettings.LiquidMode.Off) });
                else if (entry.ValueType == typeof(int)) apply.Invoke(entry, new object[] { 0 });
            }

            bool gated = !ModSettings.AnyEnabled && !ModSettings.InfoEnhance
                && !ModSettings.ValueEnhance && !ModSettings.Container && !ModSettings.TooltipFormatting && !ModSettings.ContainerUnits
                && !ModSettings.TypeBadge && !ModSettings.ContentsGrid
                && !LiquidGate.Enabled
                && !ModSettings.PriceImpact && !ModSettings.Contraband && !ModSettings.Lottery
                && !ModSettings.Wine && !ModSettings.Meat && !ModSettings.Machines && !ModSettings.ModuleEffects
                && !ModSettings.MachineBattery && !ModSettings.BlockProgressBar && !ModSettings.ManufacturerInline;
            item = TryCreate("plasma_fuel");
            if (item == null) return "settings layout/disable: cannot create tooltip sample";
            PlasmaHelper.InitPlasmaItem(item);
            string rendered = ModEntry.RenderTooltipText(item) ?? "";
            bool silent = rendered.Length == 0;
            ok = grouped && ordered && gated && silent;
            return $"settings layout/disable: grouped={grouped} ordered={ordered} gated={gated} " +
                   $"silent={silent} renderedLength={rendered.Length} (期望全部 True/0)";
        }
        catch (Exception ex) { return "settings layout/disable: " + ex; }
        finally
        {
            for (int i = saved.Count - 1; i >= 0; i--)
                try { apply.Invoke(saved[i].Entry, new[] { saved[i].Value }); } catch { }
            if (item != null) { try { item.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 区块协议实机保真：同一物品、同一状态下，观察者的捕获+回放必须逐字复现无观察者的原生输出；
    /// 接管区块后原生正文不再包含该区块，但快照仍保留它的结构化字段。
    /// 样本用 price：普通物品都有价格行，且它是可替换的原地改写区块。
    /// </summary>
    private static string CheckSectionReplay(out bool ok)
    {
        ok = false;
        GameItem? item = null;
        IDisposable? watch = null, claim = null;
        try
        {
            // 评估价（price）区块必然出现在普通物品上，用它的可替换性验证接管。
            item = TryCreate("plasma_fuel");
            if (item == null) return "section replay: 无法创建样本";
            var get = AccessTools.Method(typeof(GameItemElement), "GetTooltipBasic", Type.EmptyTypes)
                ?? throw new MissingMethodException("GameItemElement.GetTooltipBasic");
            RichTextBuilder Build() => (RichTextBuilder)get.Invoke(item, null)!;
            string Text(RichTextBuilder b) => b.Build()?.richTextForeground ?? "";

            // 只观察：不应回放任何内容，原生输出保持不变。
            string direct = Text(Build());
            watch = Integration.ItemDetailsClient.WatchTooltipSections();
            var watchedBuilder = Build();
            string watched = Text(watchedBuilder);
            bool identical = direct.Length > 0 && direct == watched;
            bool snapshot = Integration.ItemDetailsClient.TryGetTooltipSections(watchedBuilder, out var sections)
                && sections.Count > 0;

            // 有接管方但全部拒绝：同样不应回放，输出与只观察一致。
            claim = Integration.ItemDetailsClient.RegisterTooltipSections(
                (gameItem, builder, section) => false);
            var rejectedBuilder = Build();
            bool rejectedKeeps = Text(rejectedBuilder) == watched;

            // 真接管：price 区块被抑制，字段仍可从快照取到。
            claim.Dispose();
            claim = Integration.ItemDetailsClient.RegisterTooltipSections(
                (gameItem, builder, section) => section.Id == Integration.TooltipSectionIds.Price);
            var claimedBuilder = Build();
            bool suppressed = Text(claimedBuilder).Length < watched.Length;
            bool kept = Integration.ItemDetailsClient.TryGetTooltipSections(claimedBuilder, out var claimed)
                && claimed.Any(s => s.Id == Integration.TooltipSectionIds.Price);

            ok = identical && snapshot && rejectedKeeps && suppressed && kept;
            return $"section replay: identical={identical} snapshot={snapshot} rejectedKeeps={rejectedKeeps} " +
                   $"suppressed={suppressed} kept={kept} " +
                   $"lengths={direct.Length}/{watched.Length}/{Text(rejectedBuilder).Length} (期望全部 True 且三份等长)";
        }
        catch (Exception ex) { return "section replay: " + ex; }
        finally
        {
            claim?.Dispose();
            watch?.Dispose();
            if (item != null) { try { item.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 原地改写行（Direct* + Emit）在观察者开启时的两条硬要求：
    ///   1. 真实 builder 仍要写出这一行——否则一开捕获，售价/耗能/作业速度行就整段消失；
    ///   2. 快照里要留下同样的 runs——否则接管方拿到空数据，画不出自己的面板。
    /// 样本用 machine-power：它完全走 Direct，是唯一不经过 AddLine 改写路径的区块。
    /// </summary>
    private static string CheckInPlaceSection(out bool ok)
    {
        ok = false;
        GameItem? item = null;
        IDisposable? watch = null;
        try
        {
            item = TryCreate("plasma_fuel");
            if (item == null) return "in-place section: 无法创建样本";
            const string line = "耗能 4/天";

            var plain = new RichTextBuilder(40);
            var direct = new TooltipSections.Writer(plain, item, Integration.TooltipSectionIds.MachinePower);
            direct.Field("currentPerDay", 4);
            direct.Direct(line, RenderHandler.ColorPalette.LightCyan);
            direct.DirectNextLine();
            direct.Emit();
            bool written = (plain.Build()?.nakedText ?? "").IndexOf(line, StringComparison.Ordinal) >= 0;

            watch = Integration.ItemDetailsClient.WatchTooltipSections();
            var captured = new RichTextBuilder(40);
            var output = new TooltipSections.Writer(captured, item, Integration.TooltipSectionIds.MachinePower);
            output.Field("currentPerDay", 4);
            output.Direct(line, RenderHandler.ColorPalette.LightCyan);
            output.DirectNextLine();
            output.Emit();
            bool kept = (captured.Build()?.nakedText ?? "").IndexOf(line, StringComparison.Ordinal) >= 0;
            bool snapshot = Integration.ItemDetailsClient.TryGetTooltipSections(captured, out var sections)
                && sections.Any(s => s.Id == Integration.TooltipSectionIds.MachinePower && s.Runs.Count >= 2);

            ok = written && kept && snapshot;
            return $"in-place section: written={written} keptUnderWatch={kept} snapshotRuns={snapshot}";
        }
        catch (Exception ex) { return "in-place section: " + ex; }
        finally
        {
            watch?.Dispose();
            if (item != null) { try { item.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 带锁物资箱的“当前幸运预览”：预览内容必须随幸运变化，并且不得改动任何会进存档的状态。
    ///
    /// 本作只在过夜时写盘，所以不能靠“跑完看文件变没变”证明悬停干净。这里直接对比游戏自己的
    /// 序列化结果：<see cref="SaveManager.Save"/> 产出的 saveJson（物品树）、PlayerStore 的
    /// currentUniqueId / 现金 / 声望 / modData / 许可数量 / 幸运，以及 UnityEngine.Random.state
    /// 和“已拥有物品”集合。探针物品还必须是本地负数 uniqueId，且不在任何库存里。
    /// </summary>
    private static string CheckLockedCrateLuckPreview(out bool ok)
    {
        ok = false;
        GameItem? crate = null;
        HealthData? health = null;
        int oldLuck = 0;
        try
        {
            try { health = PlayerStore.Instance?.healthData; } catch { }
            if (health == null) return "locked crate luck preview: 找不到 HealthData";
            oldLuck = health.luck;

            // 用低幸运生成样本，保证“提高幸运”一定落在重填条件内，且掉落池有明显变化。
            health.luck = 0;
            crate = PreBuiltItemHelper.LootCrateSecurity();
            if (crate == null) { health.luck = oldLuck; return "locked crate luck preview: 无法创建物资箱"; }
            int spawnLuck = LotteryHelper.GetSpawnLuck(crate);
            health.luck = oldLuck;
            var grid = crate.contentWindow?.child as GameGridInventory;
            if (grid == null) return "locked crate luck preview: 物资箱没有网格库存";
            var real = grid.childItems;
            if (real.Count == 0) return "locked crate luck preview: 物资箱为空";

            // 先热身一次，把可能存在的惰性 uniqueId 分配做掉，再取基线。
            SaveManager.Save();
            string before = StoreFingerprint();
            int idBefore = PlayerStore.Instance!.currentUniqueId;
            var rngBefore = UnityEngine.Random.state;
            int ownedBefore = EmporiumEntry.Instance.GetAllOwnedItems().Count;

            health.luck = spawnLuck + 100;
            bool used = SealedBoxInfo.TryGetLuckPreview(crate, real, out var preview, out string estimate);
            health.luck = oldLuck;

            bool differs = used && !SameItemSequence(real, preview);
            bool idsNegative = preview.Count > 0 && estimate.Length > 0;
            foreach (var p in preview)
            {
                if (p == null || p.uniqueId >= 0) { idsNegative = false; break; }
            }

            var owned = EmporiumEntry.Instance.GetAllOwnedItems();
            bool noLeak = true;
            foreach (var p in preview)
            {
                if (p == null) continue;
                foreach (var o in owned)
                {
                    if (ReferenceEquals(o, p)) { noLeak = false; break; }
                }
                if (!noLeak) break;
            }

            int idAfter = PlayerStore.Instance!.currentUniqueId;
            var rngAfter = UnityEngine.Random.state;
            bool idsRestored = idAfter == idBefore;
            bool rngRestored = rngBefore.Equals(rngAfter);

            SaveManager.Save();
            string after = StoreFingerprint();
            bool saveUnchanged = string.Equals(before, after, StringComparison.Ordinal);

            SealedBoxInfo.Stop();
            bool ownedUnchanged = EmporiumEntry.Instance.GetAllOwnedItems().Count == ownedBefore;

            ok = differs && idsNegative && noLeak && idsRestored && rngRestored && saveUnchanged && ownedUnchanged;
            return $"locked crate luck preview: used={used} differs={differs} probeIdsNegative={idsNegative} " +
                   $"noLeak={noLeak} idsRestored={idsRestored} rngRestored={rngRestored} " +
                   $"saveUnchanged={saveUnchanged} ownedUnchanged={ownedUnchanged} " +
                   $"real={real.Count} preview={preview.Count} est={estimate} (期望全部 True)";
        }
        catch (Exception ex) { return "locked crate luck preview: " + ex; }
        finally
        {
            try { if (health != null) health.luck = oldLuck; } catch { }
            SealedBoxInfo.Stop();
            if (crate != null)
            {
                // GameItem.Destroy 不会销毁子物品；测试箱也要像生产路径那样先清子物，避免残留。
                try
                {
                    var g = crate.contentWindow?.child as GameGridInventory;
                    if (g != null)
                    {
                        foreach (var c in g.childItems) { try { c.Destroy(); } catch { } }
                    }
                }
                catch { }
                try { crate.Destroy(); } catch { }
            }
        }
    }

    /// <summary>会进存档的 PlayerStore 标量 + 游戏自己的物品树序列化结果，用于前后逐字对比。</summary>
    private static string StoreFingerprint()
    {
        var store = PlayerStore.Instance;
        return string.Join("|",
            store.currentUniqueId,
            store.playerCash,
            store.wildFavor,
            store.runNumber,
            store.modData?.Count ?? -1,
            store.ActivePermits?.Count ?? -1,
            store.healthData?.luck ?? int.MinValue,
            SaveManager.saveJson ?? "");
    }

    private static bool SameItemSequence(List<GameItem> a, List<GameItem> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count && i < b.Count; i++)
        {
            if (!ReferenceEquals(a[i], b[i])) return false;
        }
        return true;
    }
}
#endif
