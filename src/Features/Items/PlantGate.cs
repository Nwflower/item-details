namespace ItemDetails;

/// <summary>
/// 「植物分析」三档的运行时判定（写法同 <see cref="LiquidGate"/>）：
///   · 莓果品质 —— 靠外观就能分辨，不为关闭即显示，不要求分析仪；
///   · 未分析种子性状 —— 「有植物分析仪时」要求拥有植物分析仪（与右键菜单扫描种子的判定相同）。
/// 拥有判定交给 <see cref="ScannerInventory"/> 的 TTL 缓存。
/// </summary>
internal static class PlantGate
{
    internal static bool Berry => ModSettings.PlantAnalysis != ModSettings.PlantMode.Off;

    internal static bool SeedTraits
    {
        get
        {
            switch (ModSettings.PlantAnalysis)
            {
                case ModSettings.PlantMode.Always: return true;
                case ModSettings.PlantMode.WithAnalyser: return ScannerInventory.Has(ScannerInventory.PlantAnalyser);
                default: return false;
            }
        }
    }
}
