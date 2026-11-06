$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$ns = 'InfoSettingsTests' + [Guid]::NewGuid().ToString('N')
$source = "#nullable enable`n"
foreach ($file in @('src/Infrastructure/ModSettings.cs', 'src/Infrastructure/Patches/ConfigSectionRefresh.cs',
                    'src/Infrastructure/Hosting/PatchScope.cs', 'src/Features/Water/LiquidGate.cs')) {
    $body = (Get-Content -LiteralPath (Join-Path $repo $file) -Raw).Replace('namespace ItemDetails;', '')
    $body = $body.Replace('using HarmonyLib;', "using $ns.HarmonyLib;")
    $source += "namespace $ns {`n$body`n}`n"
}
# Compile the production settings and visibility code. Only the game's config storage,
# UI objects and patch installer are adapted; no game launch or configuration writes.
$source += @'
namespace TEST_NS.HarmonyLib {
 public static class AccessTools {
  const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
  public static System.Reflection.FieldInfo? Field(System.Type t, string n) => t.GetField(n, Flags);
  public static System.Reflection.MethodInfo? PropertySetter(System.Type t, string n) => t.GetProperty(n, Flags)?.GetSetMethod(true);
  public static System.Reflection.MethodInfo? Method(System.Type t, string n, System.Type[]? args = null) => args == null ? t.GetMethod(n, Flags) : t.GetMethod(n, Flags, null, args, null);
 }
 public class HarmonyMethod {
  public System.Reflection.MethodInfo? method;
  public int priority;
  public HarmonyMethod(System.Type t, string n) {}
  public HarmonyMethod(System.Reflection.MethodInfo m) { method = m; }
 }
 public class Harmony {
  public Harmony(string id) {}
  public void Patch(System.Reflection.MethodBase target, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null,
                    HarmonyMethod? transpiler = null, HarmonyMethod? finalizer = null) {}
  public void Unpatch(System.Reflection.MethodBase original, System.Reflection.MethodInfo patch) {}
  public void UnpatchAll(string id) {}
 }
}
namespace TEST_NS {
 using System;
 using System.Collections.Generic;
 using System.Linq;
 internal static class ScannerInventory { internal static bool HasLiquidScanner; }
 internal static class Loc {
  internal static string Code = "en";
  internal static bool IsChinese => false;
  internal static string T(string key, string en) => Code == "en" ? en : "translated:" + key;
 }
 internal static class Log {
  internal static void Msg(string s) {}
  internal static void Warn(string s, object e) { throw new Exception(s + ": " + e); }
  internal static void Error(string s, object e) { throw new Exception(s + ": " + e); }
 }
 public abstract class ConfigEntry {
  public string Key { get; internal set; } = "";
  public string Section { get; internal set; } = "";
  public string[]? Choices { get; internal set; }
  public abstract Type ValueType { get; }
  public abstract object BoxedValue { get; }
  public abstract object BoxedDefault { get; }
  internal abstract bool ApplyBoxed(object value);
 }
 public class ConfigEntry<T> : ConfigEntry where T : notnull {
  readonly T initial;
  public T Value;
  public ConfigEntry(T value) { initial = Value = value; }
  public override Type ValueType => typeof(T);
  public override object BoxedValue => Value;
  public override object BoxedDefault => initial;
  internal override bool ApplyBoxed(object v) { bool changed = !Equals(Value, v); Value = (T)v; return changed; }
  public void Set(T v) { Value = v; }
 }
 public class ModConfig {
  public static ModConfig Current = new ModConfig();
  public Dictionary<string, string> rawValues = new();
  public List<ConfigEntry> Entries = new();
  public int Saves;
  public static ModConfig For(string id) => Current;
  public static ModConfig Find(string id) => Current;
  public ConfigEntry? Get(string key) => Entries.Find(e => e.Key == key);
  public void Save() { Saves++; }
  ConfigEntry<T> Add<T>(string key, T value, string section, string[]? choices = null) where T : notnull {
   var entry = new ConfigEntry<T>(value) { Key = key, Section = section, Choices = choices };
   if (rawValues.TryGetValue(key, out var stored) && typeof(T) == typeof(bool)) entry.ApplyBoxed(bool.Parse(stored));
   Entries.Add(entry); return entry;
  }
  public ConfigEntry<bool> Define(string key, bool value, string section, Func<string> label, Func<string>? tip = null) => Add(key, value, section);
  public ConfigEntry<int> Define(string key, int value, string section, Func<string> label, int min, int max, Func<string>? tip = null) => Add(key, value, section);
  public ConfigEntry<string> Define(string key, string value, string section, Func<string> label, string[] choices, Func<string>? tip = null) => Add(key, value, section, choices);
 }
 public class Node {
  public readonly string name;
  public readonly List<Node> children = new();
  public Node? parent;
  public bool active = true;
  public Node(string n, Node? p = null) { name = n; parent = p; p?.children.Add(this); }
  public Node transform => this;
  public Node gameObject => this;
  public int childCount => children.Count;
  public int GetSiblingIndex() => parent!.children.IndexOf(this);
  public Node GetChild(int i) => children[i];
  public void SetActive(bool v) { active = v; }
 }
 public class CustomUIElement {
  public Node root;
  bool value;
  Action<bool>? changed;
  public CustomUIElement(Node node, bool initial = true) { root = node; value = initial; }
  public bool GetBool() => value;
  public void SetBool(bool v, bool notify = false) { value = v; if (notify) changed?.Invoke(v); }
  public CustomUIElement OnToggle(Action<bool> f) { changed += f; return this; }
 }
 public class CustomUIWindow {
  public string id = "mod_config:item_details";
  public bool IsAlive = true;
  public Dictionary<string, CustomUIElement> elements = new();
  public CustomUIElement? Get(string id) => elements.TryGetValue(id, out var e) ? e : null;
 }
 public class LoadedMod {}
 public static class ModConfigWindow {
  public static CustomUIWindow? Open(LoadedMod mod) => null;
  private static void ResetWidgets(CustomUIWindow w, ModConfig c) {}
 }
 public static class Cases {
  static void Check(bool value, string message) { if (!value) throw new Exception(message); }
  public static string Run() {
   var cfg = ModConfig.Current;
   // Previously removed keys must not turn off the newly introduced defaults.
   cfg.rawValues["show_wine"] = "false";
   ModSettings.Init("item_details");
   var liquidMode = (ConfigEntry<string>)cfg.Get("liquid_analysis")!;
   liquidMode.Set(ModSettings.LiquidChoiceValue(ModSettings.LiquidMode.WithScanner));
   Check(!LiquidGate.Enabled, "liquid gate requires an owned scanner");
   ScannerInventory.HasLiquidScanner = true;
   Check(LiquidGate.Enabled, "owning a scanner enables the liquid gate");
   liquidMode.Set(ModSettings.LiquidChoiceValue(ModSettings.LiquidMode.Off));
   Check(!LiquidGate.Enabled, "turning analysis off overrides scanner ownership");
   liquidMode.Set(ModSettings.LiquidChoiceValue(ModSettings.LiquidMode.Always));
   string[] keys = { "info_price_modifiers", "info_black_market", "info_lottery", "info_brewing", "info_meat", "info_machines", "info_batteries", "info_progress_bar", "info_container_units", "info_manufacturer_inline" };
   Func<bool>[] enabled = { () => ModSettings.PriceImpact, () => ModSettings.Contraband, () => ModSettings.Lottery, () => ModSettings.Wine, () => ModSettings.Meat, () => ModSettings.Machines, () => ModSettings.MachineBattery, () => ModSettings.BlockProgressBar, () => ModSettings.ContainerUnits, () => ModSettings.ManufacturerInline };
   Check(keys.All(k => Equals(cfg.Get(k)!.BoxedDefault, true)) && enabled.All(f => f()), "all ten default on");
   Check(cfg.Entries.Select(e => e.Section).Distinct().SequenceEqual(new[] { "Display", "Controls", "Reveal", ModSettings.SecInfoEn }), "four sections in order");
   Check(cfg.Get("show_info_enhance")!.Section == "Display", "master always in display");
   for (int i = 0; i < keys.Length; i++) {
    cfg.Get(keys[i])!.ApplyBoxed(false);
    for (int j = 0; j < enabled.Length; j++) Check(enabled[j]() == (i != j), "independent: " + keys[i] + " / " + keys[j]);
    if (i == 5) Check(!ModSettings.ModuleEffects && ModSettings.MachineBattery, "machines do not gate batteries");
    cfg.Get(keys[i])!.ApplyBoxed(true);
   }
   cfg.Get("tooltip_text_formatting")!.ApplyBoxed(false);
   Check(ModSettings.ContainerUnits && !ModSettings.TooltipFormatting, "volume units stay enabled without text formatting");
   cfg.Get("tooltip_text_formatting")!.ApplyBoxed(true);
   cfg.Get("info_container_units")!.ApplyBoxed(false);
   Check(!ModSettings.ContainerUnits && ModSettings.TooltipFormatting, "volume units can be disabled independently");
   cfg.Get("info_container_units")!.ApplyBoxed(true);
   cfg.Get(keys[3])!.ApplyBoxed(false);
   cfg.Get("show_info_enhance")!.ApplyBoxed(false);
   Check(enabled.All(f => !f()), "master gates every new feature");
   cfg.Get("show_info_enhance")!.ApplyBoxed(true);
   Check(!ModSettings.Wine && ModSettings.Meat, "master preserves child choices");
   ModSettings.RefreshSections();
   var w = new CustomUIWindow();
   var scroll = new Node("scroll");
   var display = new Node("display", scroll);
   var master = new CustomUIElement(new Node("master", display));
   w.elements["show_info_enhance"] = master;
   var header = new Node("header", scroll);
   w.elements["section_" + cfg.Get(keys[0])!.Section] = new CustomUIElement(new Node("header_text", header));
   new Node("separator", scroll);
   foreach (string key in keys) {
    var row = new Node("row", scroll);
    w.elements[key] = new CustomUIElement(new Node(key, row), (bool)cfg.Get(key)!.BoxedValue);
   }
   new Node("space", scroll);
   var footer = new Node("footer", scroll);
   int saves = cfg.Saves;
   var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
   typeof(ConfigSectionRefresh).GetMethod("OpenPostfix", flags)!.Invoke(null, new object[] { w });
   master.SetBool(false, true);
   Check(scroll.children.Skip(1).Take(keys.Length + 3).All(n => !n.active), "hide header, separator, option rows and gap");
   Check(display.active && footer.active, "keep other sections and footer visible");
   w.elements[keys[0]].SetBool(false);
   master.SetBool(true, true);
   Check(scroll.children.All(n => n.active) && !w.elements[keys[0]].GetBool(), "show group without resetting children");
   master.SetBool(false, true);
   master.SetBool(true); // vanilla ResetWidgets writes without notification
   typeof(ConfigSectionRefresh).GetMethod("ResetPostfix", flags)!.Invoke(null, new object[] { w });
   Check(scroll.children.All(n => n.active), "reset restores visibility");
   master.SetBool(false); // opening with a saved disabled master
   ConfigSectionRefresh.RefreshInfoVisibility(w);
   Check(!header.active, "saved disabled master hides group on open");
   w.id = "mod_config:another_mod";
   master.SetBool(true);
   ConfigSectionRefresh.RefreshInfoVisibility(w);
   Check(!header.active, "other mods are untouched");
   w.id = "mod_config:item_details";
   Loc.Code = "zh";
   ModSettings.RefreshSections();
   w.elements["section_" + cfg.Get(keys[0])!.Section] = new CustomUIElement(new Node("translated_header", header));
   ConfigSectionRefresh.RefreshInfoVisibility(w);
   Check(header.active, "localized section still toggles");
   Check(cfg.Saves == saves && ModSettings.InfoEnhance && !ModSettings.Wine, "UI visibility does not save or apply configuration");
   return "PASS ten defaults, independent gates, master preservation, section order, live visibility, reset, localization and other-mod isolation";
  }
 }
}
'@.Replace('TEST_NS', $ns)
$types = Add-Type -TypeDefinition $source -PassThru
$cases = $types | Where-Object Name -eq 'Cases'
$cases.GetMethod('Run').Invoke($null, @())
