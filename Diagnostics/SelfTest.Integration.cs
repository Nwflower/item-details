#if ITEMDETAILS_SELFTEST
using System;
using System.Collections.Generic;
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
        var setting = ModConfig.Find("item_details")?.Get("show_item_appraisal");
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
            bool gained = healthy.Exists(method => method.Name == GameRead.ItemName(filters[0]) && method.GainPct > 0);
            bool exhausted = !uncachedExhausted.Exists(method => method.Name == GameRead.ItemName(filters[1]));
            bool sharingCorrect = SameMethods(cachedExhausted, uncachedExhausted);

            WaterAdvice.Stop();
            _ = WaterAdvice.SimulateMethods(pitchers[0]);
            filters[0].ModifyTag("DURABILITY_TAG", state => state.Enable().SetInt(0));
            var afterWear = WaterAdvice.SimulateMethods(pitchers[0]);
            bool wearUpdated = SameMethods(afterWear, uncachedExhausted);
            ok = gained && exhausted && sharingCorrect && wearUpdated;
            return $"installed filter cache: healthyGain={gained} exhaustedNoGain={exhausted} cachedMatchesUncached={sharingCorrect} wearUpdated={wearUpdated}";
        }
        catch (Exception ex) { return "installed filter cache: " + ex; }
        finally
        {
            WaterAdvice.Stop();
            for (int i = items.Count - 1; i >= 0; i--) { try { items[i].Destroy(); } catch { } }
        }
    }

    private static string CheckPartialContainerTotals(out bool ok)
    {
        ok = false;
        var items = new List<GameItem>();
        try
        {
            var container = TryCreate("trashcan");
            if (container == null) return "partial container totals: cannot create container";
            items.Add(container);
            GeneralHelper.SetItemOwned(container, true);
            foreach (string id in new[] { "scrap_metal", "water_pitcher" })
            {
                var child = TryCreate(id);
                if (child == null) return "partial container totals: cannot create child";
                items.Add(child);
                GeneralHelper.SetItemOwned(child, true);
                if (id == "scrap_metal") child.unitValue = 100;
                if (GraphUtils.TryAcceptAll(container, child) != 0
                    || !ReferenceEquals(child.parentInventory, container.contentWindow?.child))
                    return "partial container totals: child insertion failed";
            }
            StorageInfo.Stop();
            var storage = StorageInfo.Collect(container);
            bool storageSuppressed = storage.Count > 0
                && !HasLine(storage, Loc.T("storage.total_value", "Total value: {0}", ""));
            container.EnableTag("LOCKED_CONTAINER_TAG");
            container.ModifyTag("LOCK_STATUS", state => state.Enable().SetBool(true));
            SealedBoxInfo.Stop();
            var sealedLines = SealedBoxInfo.Collect(container);
            bool sealedSuppressed = sealedLines.Count > 0
                && !HasLine(sealedLines, Loc.T("sealed.total", "Total inside: {0}", ""));
            ok = storageSuppressed && sealedSuppressed;
            return $"partial container totals: storageSuppressed={storageSuppressed} sealedSuppressed={sealedSuppressed}";
        }
        catch (Exception ex) { return "partial container totals: " + ex; }
        finally
        {
            StorageInfo.Stop();
            SealedBoxInfo.Stop();
            for (int i = items.Count - 1; i >= 0; i--) { try { items[i].Destroy(); } catch { } }
        }
    }
}
#endif
