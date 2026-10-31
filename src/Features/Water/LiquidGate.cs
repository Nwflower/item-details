namespace ItemDetails;

/// <summary>
/// 「液体组分分析」三档的运行时判定：
///   · 关闭       —— 一律不显示；
///   · 有扫描器时 —— 当前拥有水质扫描仪（<c>aquascan</c>）或化学扫描仪（<c>chem_scanner</c>）才显示；
///   · 总是开启   —— 无条件显示。
///
/// 拥有判定交给 <see cref="ScannerInventory"/> 的 TTL 缓存，悬停时不会每次遍历全部容器。
/// 门控只决定“显示不显示”，数据读取本身仍是只读的 <see cref="WaterInfo"/>。
/// </summary>
internal static class LiquidGate
{
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
