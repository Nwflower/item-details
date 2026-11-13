$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$ns = 'InfoSettingsTests' + [Guid]::NewGuid().ToString('N')
$source = "#nullable enable`n"
foreach ($file in @('src/Infrastructure/ModSettings.cs', 'src/Infrastructure/Patches/ConfigSectionRefresh.cs',
                    'src/Infrastructure/Hosting/PatchScope.cs', 'src/Features/Water/LiquidGate.cs',
                    'src/Features/Items/PlantGate.cs')) {
    $body = (Get-Content -LiteralPath (Join-Path $repo $file) -Raw).Replace('namespace ItemDetails;', '')
    $body = $body.Replace('using HarmonyLib;', "using $ns.HarmonyLib;")
    $source += "namespace $ns {`n$body`n}`n"
}
# Compile the production settings and visibility code. Only the game's config storage,
# UI objects and patch installer are adapted; no game launch or configuration writes.
# ConfigSliderValue builds Unity UI objects, so it is replaced by a call counter here.
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
 // LiquidGate / PlantGate（生产代码）也要编译：它们按物品类型判定扫描器，这里给只读桩。
 public class GameItem { public string identifier = ""; }
 internal static class GameRead { internal static bool IsTag(GameItem item, string tag) => false; }
 internal static class ScannerInventory {
  internal static bool HasLiquidScanner;
  internal static bool HasPlantAnalyser;
  internal const string ChemScanner = "chem_scanner";
  internal const string PlantAnalyser = "plant_analyser";
  internal static bool Has(string scanner) => scanner == PlantAnalyser ? HasPlantAnalyser : HasLiquidScanner;
 }
 internal static class ConfigSliderValue {
  internal static int Attached, Refreshed;
  internal static void Attach(CustomUIWindow window) { Attached++; }
  internal static void Refresh() { Refreshed++; }
 }
 internal static class Loc {
  internal static string Code = "en";
  internal static bool IsChinese => false;
  internal static string T(string key, string en) => Code == "en" ? en : "translated:" + key;
  internal static string T(string key, string en, params object[] args) => string.Format(T(key, en), args);
 }
 internal static class Log {
  internal static readonly List<string> Warnings = new();
  internal static void Msg(string s) {}
  internal static void Warn(string s, object e) { Warnings.Add(s + ": " + e); }
  internal static void Error(string s, object e) { throw new Exception(s + ": " + e); }
 }
 public abstract class ConfigEntry {
  public ModConfig? Config;
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
  // Like the game: a changed value is saved at once.
  public void Set(T v) { if (ApplyBoxed(v)) Config?.Save(); }
 }
 public class ModConfig {
  public static ModConfig Current = new ModConfig();
  public Dictionary<string, string> rawValues = new();
  public List<ConfigEntry> Entries = new();
  public int Saves;
  public static ModConfig For(string id) => Current;
  public static ModConfig Find(string id) => Current;
  public ConfigEntry? Get(string key) => Entries.Find(e => e.Key == key);
  // Like the game: Save writes every registered key into rawValues.
  public void Save() { Saves++; foreach (var e in Entries) rawValues[e.Key] = e.BoxedValue.ToString()!; }
  ConfigEntry<T> Add<T>(string key, T value, string section, string[]? choices = null) where T : notnull {
   var entry = new ConfigEntry<T>(value) { Key = key, Section = section, Choices = choices, Config = this };
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
  public object? toggle, dropdown, slider;
  bool value;
  int index;
  Action<bool>? toggled;
  Action<int>? picked;
  public CustomUIElement(Node node, bool initial = true) { root = node; value = initial; toggle = new object(); }
  public CustomUIElement(Node node, int initialIndex) { root = node; index = initialIndex; dropdown = new object(); }
  public bool GetBool() => value;
  public void SetBool(bool v, bool notify = false) { value = v; if (notify) toggled?.Invoke(v); }
  public CustomUIElement OnToggle(Action<bool> f) { if (toggle == null) throw new Exception("not a toggle"); toggled += f; return this; }
  public int GetIndex() => dropdown != null ? index : -1;
  public void SetIndex(int i, bool notify = false) { index = i; if (notify) picked?.Invoke(i); }
  public CustomUIElement OnDropdown(Action<int> f) { if (dropdown == null) throw new Exception("not a dropdown"); picked += f; return this; }
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
  static readonly System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
  static void Invoke(string name, CustomUIWindow w) => typeof(ConfigSectionRefresh).GetMethod(name, Flags)!.Invoke(null, new object[] { w });

  static ModConfig Reinit(Dictionary<string, string> raw) {
   var cfg = ModConfig.Current;
   cfg.Entries.Clear();
   cfg.rawValues = raw;
   ModSettings.Init("item_details");
   return cfg;
  }

  // Same layout as ModConfigWindow.Open: per section a header row, a padded separator,
  // one row per entry (label, flexible space, control) and a gap; then the footer.
  static (CustomUIWindow Window, Node Content, List<Node> Headers) BuildWindow(ModConfig cfg) {
   var w = new CustomUIWindow();
   var content = new Node("content");
   var headers = new List<Node>();
   foreach (string section in cfg.Entries.Select(e => e.Section).Distinct()) {
    var headerRow = new Node("row", content);
    headers.Add(headerRow);
    w.elements["section_" + section] = new CustomUIElement(new Node("header_section_" + section, headerRow));
    new Node("separator_pad", content);
    foreach (var entry in cfg.Entries.Where(e => e.Section == section)) {
     var row = new Node("row", content);
     new Node("label_" + entry.Key, row);
     new Node("flexible_space", row);
     var control = new Node(entry.Key, row);
     if (entry.ValueType == typeof(bool)) w.elements[entry.Key] = new CustomUIElement(control, (bool)entry.BoxedValue);
     else if (entry.Choices != null) w.elements[entry.Key] = new CustomUIElement(control, Array.IndexOf(entry.Choices, (string)entry.BoxedValue));
     else w.elements[entry.Key] = new CustomUIElement(control) { toggle = null, slider = new object() };
    }
    new Node("space", content);
   }
   new Node("separator_pad", content);
   new Node("footer", content);
   return (w, content, headers);
  }

  public static string Run() {
   // ---- plant_analysis migration ----
   var cfg = Reinit(new() { ["show_authenticity"] = "false" });
   Check(ModSettings.PlantAnalysis == ModSettings.PlantMode.Off && !ModSettings.Auth, "authenticity off migrates plant analysis to off");
   Check(cfg.rawValues.ContainsKey("show_authenticity"), "old key is kept");
   cfg = Reinit(new() { ["show_authenticity"] = "true" });
   Check(ModSettings.PlantAnalysis == ModSettings.PlantMode.Always, "authenticity on migrates plant analysis to always");
   cfg = Reinit(new());
   Check(ModSettings.PlantAnalysis == ModSettings.PlantMode.Always && ModSettings.Auth, "fresh config defaults to always");
   cfg = Reinit(new() { ["show_authenticity"] = "false", ["plant_analysis"] = "总是开启 (Always)" });
   Check(ModSettings.PlantAnalysis == ModSettings.PlantMode.Always, "an existing plant_analysis key wins over the old toggle");
   // An earlier migration that changes a value saves at once; later migrations must still see the file as loaded.
   cfg = Reinit(new() { ["show_item_appraisal"] = "false", ["show_water_purity"] = "false" });
   Check(!ModSettings.Auth && ModSettings.PlantAnalysis == ModSettings.PlantMode.Off, "pre-split appraisal off reaches plant analysis");
   Check(ModSettings.LiquidAnalysis == ModSettings.LiquidMode.Off && !ModSettings.WaterAdvice, "water migrations still run after an earlier save");

   // ---- plant gate ----
   cfg = Reinit(new());
   var plantMode = (ConfigEntry<string>)cfg.Get("plant_analysis")!;
   int authIndex = cfg.Entries.IndexOf(cfg.Get("show_authenticity")!);
   Check(cfg.Entries.IndexOf(plantMode) == authIndex + 1 && plantMode.Section == "Reveal", "plant analysis follows authenticity in Reveal");
   Check(PlantGate.Berry && PlantGate.SeedTraits, "always shows berry quality and seed traits");
   plantMode.Set(ModSettings.PlantChoiceValue(ModSettings.PlantMode.WithAnalyser));
   ScannerInventory.HasPlantAnalyser = false;
   Check(PlantGate.Berry && !PlantGate.SeedTraits, "berry quality needs no analyser; seed traits do");
   ScannerInventory.HasPlantAnalyser = true;
   Check(PlantGate.Berry && PlantGate.SeedTraits, "owning a plant analyser enables seed traits");
   cfg.Get("show_authenticity")!.ApplyBoxed(false);
   Check(PlantGate.Berry && PlantGate.SeedTraits, "authenticity no longer gates plant information");
   plantMode.Set(ModSettings.PlantChoiceValue(ModSettings.PlantMode.Off));
   Check(!PlantGate.Berry && !PlantGate.SeedTraits, "off overrides analyser ownership");
   foreach (var entry in cfg.Entries) if (entry.ValueType == typeof(bool)) entry.ApplyBoxed(false);
   ((ConfigEntry<string>)cfg.Get("liquid_analysis")!).Set(ModSettings.LiquidChoiceValue(ModSettings.LiquidMode.Off));
   Check(!ModSettings.AnyEnabled, "everything off disables the mod");
   plantMode.Set(ModSettings.PlantChoiceValue(ModSettings.PlantMode.WithAnalyser));
   Check(ModSettings.AnyEnabled, "plant analysis alone keeps the mod enabled");

   // ---- expand key ----
   cfg = Reinit(new());
   var expand = (ConfigEntry<string>)cfg.Get("expand_key")!;
   Check(expand.Section == "Controls" && ModSettings.ExpandKey == ModSettings.ExpandKeyMode.Ctrl && ModSettings.ExpandKeyName == "Ctrl", "expand key defaults to Ctrl");
   expand.Set("Shift");
   Check(ModSettings.ExpandKey == ModSettings.ExpandKeyMode.Shift && ModSettings.ExpandKeyName == "Shift", "Shift selected");
   Loc.Code = "zh";
   Check(ModSettings.ExpandKeyName == "translated:config.expand_key.shift", "key name follows the language");
   Loc.Code = "en";
   expand.Set("Alt");
   Check(ModSettings.ExpandKey == ModSettings.ExpandKeyMode.Alt, "Alt selected");

   // ---- independent info gates (previous coverage) ----
   // Previously removed keys must not turn off the newly introduced defaults.
   cfg = Reinit(new() { ["show_wine"] = "false" });
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

   // ---- panel visibility ----
   ModSettings.RefreshSections();
   var (w, content, headers) = BuildWindow(cfg);
   Func<string, bool> row = key => w.elements[key].root.parent!.active;
   string[] displayGated = { "show_cost_performance", "show_container_contents", "tooltip_text_formatting", "show_type_badge", "mute_type_badge_colors", "show_container_name_display", "container_name_bg_opacity" };
   string[] infoKeys = cfg.Entries.Where(e => e.Section == ModSettings.SecInfoEn).Select(e => e.Key).ToArray();
   Check(infoKeys.Length == 11 && infoKeys.Last() == "info_manufacturer_inline", "info group has eleven entries");
   var master = w.elements["show_info_enhance"];
   int saves = cfg.Saves;
   Invoke("OpenPostfix", w);
   Check(Log.Warnings.Count == 0, "expected layout logs nothing: " + string.Join("; ", Log.Warnings));
   Check(ConfigSliderValue.Attached == 1, "slider value attached on open");
   // Defaults: names off → opacity hidden; prefill off → price hidden; liquid always → colors shown.
   Check(content.children.All(n => n.active || n.children.Any(c => c.name == "container_name_bg_opacity" || c.name == "result_prefill_price")), "only the two defaulted-off children start hidden");
   Check(!row("container_name_bg_opacity") && !row("result_prefill_price") && row("color_liquid_components") && row("mute_type_badge_colors"), "child rows follow their parent on open");

   master.SetBool(false, true);
   Check(displayGated.All(k => !row(k)) && infoKeys.All(k => !row(k)), "master hides every gated row in both groups");
   Check(row("show_info_enhance") && row("show_customer_history") && headers[0].active, "display group keeps the master and its own options");
   int infoHeader = headers[3].GetSiblingIndex();
   Check(!headers[3].active && !content.GetChild(infoHeader + 1).active && !content.children[content.children.IndexOf(w.elements["info_manufacturer_inline"].root.parent!) + 1].active, "hide info header, separator and gap");
   Check(content.children.Last().active && content.children[content.childCount - 2].active, "footer stays visible");
   w.elements["show_container_name_display"].SetBool(true, true);
   Check(!row("container_name_bg_opacity"), "a chained child stays hidden while the master is off");
   w.elements["info_brewing"].SetBool(false);
   master.SetBool(true, true);
   Check(row("container_name_bg_opacity") && headers[3].active && infoKeys.All(k => row(k)) && !w.elements["info_brewing"].GetBool(), "show group without resetting children");

   w.elements["show_type_badge"].SetBool(false, true);
   Check(!row("mute_type_badge_colors") && row("show_type_badge"), "muted colors follow the badge toggle");
   w.elements["show_type_badge"].SetBool(true, true);
   Check(row("mute_type_badge_colors"), "muted colors return");
   w.elements["liquid_analysis"].SetIndex((int)ModSettings.LiquidMode.Off, true);
   Check(!row("color_liquid_components") && row("water_advice"), "liquid colors follow analysis; advice stays independent");
   w.elements["liquid_analysis"].SetIndex((int)ModSettings.LiquidMode.WithScanner, true);
   Check(row("color_liquid_components"), "with-scanner counts as not off");
   w.elements["show_result_prefill"].SetBool(true, true);
   Check(row("result_prefill_price"), "prefill price follows prefill");

   // Vanilla ResetWidgets writes without notification; the postfix refreshes rows and the slider value.
   master.SetBool(false, true);
   master.SetBool(true);
   w.elements["show_result_prefill"].SetBool(false);
   int refreshed = ConfigSliderValue.Refreshed;
   Invoke("ResetPostfix", w);
   Check(infoKeys.All(k => row(k)) && headers[3].active && !row("result_prefill_price"), "reset restores visibility");
   Check(ConfigSliderValue.Refreshed == refreshed + 1, "reset refreshes the slider value");

   master.SetBool(false); // opening with a saved disabled master
   ConfigSectionRefresh.RefreshVisibility(w);
   Check(!headers[3].active && headers[2].active, "saved disabled master hides group on open");

   w.id = "mod_config:another_mod";
   master.SetBool(true);
   ConfigSectionRefresh.RefreshVisibility(w);
   Invoke("ResetPostfix", w);
   Check(!headers[3].active && ConfigSliderValue.Refreshed == refreshed + 1, "other mods are untouched");
   w.id = "mod_config:item_details";

   Loc.Code = "zh";
   ModSettings.RefreshSections();
   var renamed = cfg.Entries.Select(e => e.Section).Distinct().ToList();
   for (int i = 0; i < renamed.Count; i++) w.elements["section_" + renamed[i]] = new CustomUIElement(new Node("translated_header", headers[i]));
   ConfigSectionRefresh.RefreshVisibility(w);
   Check(headers[3].active && Log.Warnings.Count == 0, "localized sections still toggle");

   // Unexpected layout: rows of a group live elsewhere → that group is left as is and one warning is logged.
   var stray = new Node("elsewhere");
   w.elements["info_meat"].root.parent!.parent = stray;
   master.SetBool(false);
   ConfigSectionRefresh.RefreshVisibility(w);
   Check(headers[3].active && Log.Warnings.Count > 0 && !row("show_cost_performance"), "unexpected layout leaves that group alone and logs");
   Loc.Code = "en";

   Check(cfg.Saves == saves && ModSettings.InfoEnhance && !ModSettings.Wine, "UI visibility does not save or apply configuration");
   return "PASS plant/authenticity migration (incl. save-during-migration), plant gate, expand key, ten defaults, independent gates, master preservation, section order, dependency-table visibility, chained children, reset, localization, layout fallback and other-mod isolation";
  }
 }
}
'@.Replace('TEST_NS', $ns)
$types = Add-Type -TypeDefinition $source -PassThru
$cases = $types | Where-Object Name -eq 'Cases'
$cases.GetMethod('Run').Invoke($null, @())
