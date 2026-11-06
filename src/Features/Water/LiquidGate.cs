namespace ItemDetails;

/// <summary>
/// 「液体组分分析」三档的运行时判定：
///   · 关闭       —— 一律不显示；
///   · 有扫描器时 —— 按物品类型要求对应的扫描器：化学品（药片、粉末、注射器等）要化学扫描仪，
///                    液体容器有水质扫描仪或化学扫描仪任一件即可（与右键菜单「扫描」的判定一致）；
///   · 总是开启   —— 无条件显示。
///
/// 拥有判定交给 <see cref="ScannerInventory"/> 的 TTL 缓存，悬停时不会每次遍历全部容器。
/// 门控只决定“显示不显示”，数据读取本身仍是只读的 <see cref="WaterInfo"/>。
/// </summary>
internal static class LiquidGate
{
    /// <summary>这件物品是否可以显示液体组分：「有扫描器时」按物品类型分别判断，其余两档与 <see cref="Enabled"/> 相同。</summary>
    internal static bool Allows(GameItem? item)
    {
        if (item == null) return false;
        if (ModSettings.LiquidAnalysis != ModSettings.LiquidMode.WithScanner) return Enabled;
        if (GameRead.IsTag(item, "CHEM_ITEM_TAG") || GameRead.IsTag(item, "CHEMICAL_PRODUCT_TAG"))
            return ScannerInventory.Has(ScannerInventory.ChemScanner);
        return ScannerInventory.HasLiquidScanner;
    }

    /// <summary>全局判定：开着且（总是开启，或手上有任一种液体扫描器）。</summary>
    internal static bool Enabled
    {
        get
        {
            switch (ModSettings.LiquidAnalysis)
            {
                case ModSettings.LiquidMode.Always: return true;
                case ModSettings.LiquidMode.WithScanner: return ScannerInventory.HasLiquidScanner;
                default: return false;
            }
        }
    }
}
