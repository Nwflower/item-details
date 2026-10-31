using System;

namespace ItemDetails;

/// <summary>
/// 双击扫描：拥有对应设备时，双击物品直接打开该设备的扫描结果面板。
///
/// 对应关系（照 0.46D 原版各扫描器自己的目标判定）：
///   · 上锁补给箱（SUPPLY_CRATE + LOCK_STATUS）   → 便携式X射线扫描仪（xray_scanner）
///   · 化学品（CHEM_ITEM_TAG / CHEMICAL_PRODUCT_TAG）→ 化学扫描仪（chem_scanner）
///   · 其它液体容器（LIQUID_CONTAINER_TAG）        → 水质扫描仪优先，没有则化学扫描仪
///   · 种子（SEED_ITEM_TAG）                       → 植物分析仪（plant_analyser）
///
/// 不接管的三类：基因扫描仪是常开开关、没有结果面板；拾荒扫描仪与改造体扫描仪不针对
/// 物品 / 只针对顾客；成分光谱仪（spectral_analyser）在 0.46D 没有任何投放途径。
///
/// 走的都是原版公开入口，效果与玩家把设备拖到物品上一致（包括原版自己写入的扫描结果、
/// 补给箱的 LOOT_CRATE_SCANNED_TAG、化学品/水的对质按钮状态）。没有对应设备时返回 false，
/// 交还原版双击（打开检视窗口），不改任何原有行为。
/// </summary>
internal static class DoubleClickScan
{
    internal static bool Enabled => ModSettings.DoubleClickScan;

    /// <summary>这件物品当前有没有“已拥有设备 + 可打开面板”的对应关系。</summary>
    internal static bool CanHandle(GameItem? item)
    {
        if (!Enabled) return false;
        try { return Classify(item) != Kind.None; }
        catch { return false; }
    }

    /// <summary>执行扫描面板；处理了返回 true（调用方应吞掉这次双击）。</summary>
    internal static bool TryScan(GameItem? item, string source)
    {
        if (!Enabled) return false;
        try
        {
            switch (Classify(item))
            {
                case Kind.Xray:
                    StoreResponse.ShowSupplyCrateResponse(item!);
                    Log.Msg($"DoubleClickScan: {source} xray -> {GameRead.ItemName(item)}");
                    return true;

                case Kind.Chem:
                {
                    var scanner = ScannerInventory.Find(ScannerInventory.ChemScanner);
                    var ui = ChemScannerUIManager.Instance;
                    if (scanner == null || ui == null) return false;
                    ui.Open(scanner, item!);
                    Log.Msg($"DoubleClickScan: {source} chem -> {GameRead.ItemName(item)}");
                    return true;
                }

                case Kind.Water:
                {
                    var scanner = ScannerInventory.Find(ScannerInventory.WaterScanner);
                    var ui = WaterScannerUIManager.Instance;
                    if (scanner == null || ui == null) return false;
                    ui.Open(scanner, item!);
                    Log.Msg($"DoubleClickScan: {source} water -> {GameRead.ItemName(item)}");
                    return true;
                }

                case Kind.Plant:
                {
                    var ui = PlantAnalyserUIManager.Instance;
                    if (ui == null) return false;
                    ui.OpenUI(item!);
                    Log.Msg($"DoubleClickScan: {source} plant -> {GameRead.ItemName(item)}");
                    return true;
                }

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("DoubleClickScan", ex);
            return false;
        }
    }

    private enum Kind
    {
        None,
        Xray,
        Chem,
        Water,
        Plant,
    }

    /// <summary>
    /// 物品 → 设备。顺序即优先级：化学品先于液体容器（化学品往往也带 LIQUID_CONTAINER_TAG，
    /// 但它只能用化学扫描仪读到纯度与对质），补给箱最先（它同时是容器）。
    /// </summary>
    private static Kind Classify(GameItem? item)
    {
        if (item == null) return Kind.None;
        if (PlayerStore.Instance == null) return Kind.None;   // 只在店内生效

        if (IsLockedSupplyCrate(item) && ScannerInventory.Has(ScannerInventory.XrayScanner))
            return Kind.Xray;

        if (IsChem(item) && ScannerInventory.Has(ScannerInventory.ChemScanner))
            return Kind.Chem;

        if (GameRead.IsTag(item, "LIQUID_CONTAINER_TAG"))
        {
            if (ScannerInventory.Has(ScannerInventory.WaterScanner)) return Kind.Water;
            if (ScannerInventory.Has(ScannerInventory.ChemScanner)) return Kind.Chem;
        }

        if (GameRead.IsTag(item, "SEED_ITEM_TAG") && ScannerInventory.Has(ScannerInventory.PlantAnalyser))
            return Kind.Plant;

        return Kind.None;
    }

    /// <summary>与原版 X 射线扫描仪的目标判定一致：锁定中的补给箱。</summary>
    private static bool IsLockedSupplyCrate(GameItem item)
    {
        try
        {
            return item.IsGameItemType("SUPPLY_CRATE") && GameRead.TagBool(item, "LOCK_STATUS");
        }
        catch { return false; }
    }

    /// <summary>与原版化学扫描仪的目标判定一致：固体化学品与化学制品。</summary>
    private static bool IsChem(GameItem item)
        => GameRead.IsTag(item, "CHEM_ITEM_TAG") || GameRead.IsTag(item, "CHEMICAL_PRODUCT_TAG");
}
