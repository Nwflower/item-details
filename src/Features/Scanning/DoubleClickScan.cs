using System;

namespace ItemDetails;

/// <summary>
/// 双击扫描：拥有对应设备时，双击物品直接打开该设备的扫描结果面板。
///
/// 对应关系（照 0.46D 原版各扫描器自己的目标判定）：
///   · 上锁补给箱（SUPPLY_CRATE + LOCK_STATUS）   → 便携式X射线扫描仪（xray_scanner）
///   · 化学品（CHEM_ITEM_TAG / CHEMICAL_PRODUCT_TAG）→ 化学扫描仪（chem_scanner）
///   · 普通液体容器（LIQUID_CONTAINER_TAG）        → 水质扫描仪优先，没有则化学扫描仪
///   · 种子（SEED_ITEM_TAG）                       → 植物分析仪（plant_analyser）
///
/// 接管前先问物品自己有没有“结果性动作”（见 <see cref="VanillaDoubleClickHasResult"/>）：
/// 原版双击会打开容器窗口，或执行物品自己的激活动作时，一律交还原版。判定照抄
/// <c>ItemMouseDoubleClickHandler.ClickAction</c> 的分支，不按物品名/标签开白名单——
/// 模组物品给自己注册了内容窗口或激活动作，同样会被让开。
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

        // 除上锁补给箱外，剩下的三类都要先确认这次双击"没人要"：物品自己有容器窗口
        // 或激活动作时，原版双击已经在做事，扫描不许顶掉它。
        if (VanillaDoubleClickHasResult(item)) return Kind.None;

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

    /// <summary>
    /// 原版双击对这件物品有没有“结果性动作”。有就说明这个手势已经有主，扫描必须让开。
    ///
    /// 判定照抄 <c>ItemMouseDoubleClickHandler.ClickAction</c> 的两条分支，不按物品名或标签
    /// 开白名单——模组物品给自己注册了内容窗口或激活动作，同样会命中：
    ///
    ///   · <c>contentWindow</c> 存在且未上锁 → 原版双击是开/关这个窗口。
    ///     水罐的过滤器槽、酒瓶的投料与发酵、保险箱与补给箱的内容格都走这条。
    ///   · <c>MayActivateSlotItem() &amp;&amp; CanActivateSlotItem()</c> → 原版双击会执行物品
    ///     自己的激活动作（食品券兑换、使用道具；模组物品注册的自定义双击通常也挂这里）。
    ///
    /// 锁着的容器窗口不算：原版双击打开它也只是看一个动不了的界面，上锁补给箱因此仍由
    /// X 射线扫描接管（见 <see cref="IsLockedSupplyCrate"/>）。
    ///
    /// “拿起 / 开关状态”（<c>MaySelectSlotItem</c>、<c>MayToggleSlotItem</c>）不在其中：
    /// 它们只是可逆的通用状态切换、不改变物品，玩家仍有拖拽等替代手势，而扫描对水质容器
    /// 给出的信息更直接。若哪天要连这两条也让开，把对应判定加进来即可。
    ///
    /// 任何一步判定抛异常都按“有动作”处理：宁可少一个快捷方式，也不吞掉原版双击。
    /// </summary>
    private static bool VanillaDoubleClickHasResult(GameItem item)
    {
        try
        {
            var window = item.contentWindow;
            if (window != null && !window.isLocked) return true;
        }
        catch { return true; }

        try { return item.MayActivateSlotItem() && item.CanActivateSlotItem(); }
        catch { return true; }
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
