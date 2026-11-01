$ErrorActionPreference = 'Stop'
$testRepo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$testNamespace = 'SelfTestFixtures' + [Guid]::NewGuid().ToString('N')
$testSource = '#define ITEMDETAILS_SELFTEST' + [Environment]::NewLine + '#nullable enable' + [Environment]::NewLine
foreach ($testFile in @('SelfTest.Request.cs', 'SelfTest.Settings.cs')) {
    $testSource += "namespace $testNamespace {" + [Environment]::NewLine +
        (Get-Content -LiteralPath (Join-Path $testRepo ('src/Diagnostics/' + $testFile)) -Raw).Replace(
        'namespace ItemDetails;', '').Replace(
        'using HarmonyLib;', "using $testNamespace.HarmonyLib;") + '}' + [Environment]::NewLine
}
$testSource += @'
namespace TEST_NAMESPACE.HarmonyLib {
 public static class AccessTools {
  public static System.Reflection.MethodInfo? Method(System.Type type, string name) => type.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
 }
}
namespace TEST_NAMESPACE {
 public class ConfigEntry {
  public static int Saves;
  public object Value = false;
  public System.Type ValueType = typeof(bool);
  public object BoxedValue => Value;
  internal bool ApplyBoxed(object value) { bool changed = !object.Equals(Value, value); Value = value; return changed; }
  public void Set(bool value) { Value = value; Saves++; }
 }
 public class ModConfig {
  public static readonly ModConfig Current = new ModConfig();
  public readonly System.Collections.Generic.Dictionary<string, ConfigEntry> Entries = new();
  public static ModConfig Find(string id) => Current;
  public ConfigEntry? Get(string key) => Entries.TryGetValue(key, out var entry) ? entry : null;
 }
 public static class ModSettings {
  internal enum LiquidMode { Off, WithScanner, Always }
  internal const string LiquidOffChoice = "关闭 (Off)";
  internal const string LiquidAlwaysChoice = "总是开启 (Always)";
  internal static string LiquidChoiceValue(LiquidMode mode) =>
      mode == LiquidMode.Always ? LiquidAlwaysChoice : LiquidOffChoice;
 }
 public static class Smoke {
  public static string Parse(string[] args, string? trigger) => SelfTest.ParseTest(args, trigger);
  public static bool Settings(bool throwInside, bool missingEntry) => SelfTest.SettingsTest(throwInside, missingEntry);
 }
 internal static partial class SelfTest {
  internal static string ParseTest(string[] args, string? trigger) {
   bool accepted = TryReadSlot(args, trigger, out _saveSlot, out string error);
   return accepted ? _saveSlot.ToString() : "rejected";
  }
  internal static bool SettingsTest(bool throwInside, bool missingEntry) {
   var entries = ModConfig.Current.Entries;
   entries.Clear(); ConfigEntry.Saves = 0;
   foreach (string key in new[] { "show_authenticity", "show_valuation", "show_info_enhance", "double_click_inspect", "context_menu_scan" })
    entries.Add(key, new ConfigEntry());
   entries.Add("liquid_analysis", new ConfigEntry { Value = ModSettings.LiquidOffChoice, ValueType = typeof(string) });
   if (missingEntry) entries.Remove("double_click_inspect");
   bool applied = false, threw = false;
   try {
    using (new TestSettings()) {
     applied = System.Linq.Enumerable.All(entries, pair => pair.Key == "liquid_analysis"
       ? object.Equals(pair.Value.Value, ModSettings.LiquidAlwaysChoice)
       : object.Equals(pair.Value.Value, true));
     if (throwInside) throw new System.InvalidOperationException("test failure");
    }
   } catch (System.InvalidOperationException) { threw = true; }
   bool restored = System.Linq.Enumerable.All(entries, pair => pair.Key == "liquid_analysis"
     ? object.Equals(pair.Value.Value, ModSettings.LiquidOffChoice)
     : object.Equals(pair.Value.Value, false));
   return restored && ConfigEntry.Saves == 0 && (missingEntry ? threw : applied && threw == throwInside);
  }
 }
}
'@.Replace('TEST_NAMESPACE', $testNamespace)
$testTypes = Add-Type -TypeDefinition $testSource -PassThru
$testSmoke = $testTypes | Where-Object Name -eq 'Smoke'
foreach ($testCase in @(
    @{ Args = [string[]]@(); Trigger = $null; Expected = 'rejected' },
    @{ Args = [string[]]@('-itemdetails-selftest-slot'); Trigger = $null; Expected = 'rejected' },
    @{ Args = [string[]]@('-itemdetails-selftest-slot','bad'); Trigger = $null; Expected = 'rejected' },
    @{ Args = [string[]]@('-itemdetails-selftest-slot','-1'); Trigger = $null; Expected = 'rejected' },
    @{ Args = [string[]]@('-itemdetails-selftest-slot','3'); Trigger = $null; Expected = '3' },
    @{ Args = [string[]]@(); Trigger = " 2`n"; Expected = '2' },
    @{ Args = [string[]]@(); Trigger = ''; Expected = 'rejected' },
    @{ Args = [string[]]@('-itemdetails-selftest-slot','3'); Trigger = '3'; Expected = '3' },
    @{ Args = [string[]]@('-itemdetails-selftest-slot','3'); Trigger = '2'; Expected = 'rejected' }
)) {
    $testActual = $testSmoke.GetMethod('Parse').Invoke($null, @($testCase.Args, $testCase.Trigger))
    if ($testActual -ne $testCase.Expected) { throw ('Slot request mismatch: expected ' + $testCase.Expected + ', got ' + $testActual) }
}
foreach ($testCase in @(@($false,$false), @($true,$false), @($false,$true))) {
    if (-not $testSmoke.GetMethod('Settings').Invoke($null, $testCase)) { throw 'Temporary settings were not applied/restored without saving.' }
}
Write-Output 'PASS: explicit slot requests and temporary settings restore after success, execution failure, and partial setup failure.'
