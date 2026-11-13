#if ITEMDETAILS_SELFTEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 一次性 tooltip 快照导出：ModData/item_details/tooltipsnap.flag。
/// flag 内容是存档槽位 ID，或若干行 key=value：
///   slot=25                            必填，可丢弃的存档槽位
///   variant=item_details               写进 meta.variant 的变体名（区分"只有我们"与"我们 + 别的排版模组"）
///   suspend=amres.sectioned_tooltips   导出前经模组开关探针临时停用的模组（逗号分隔；没在运行则跳过）
///   require=amres.sectioned_tooltips   必须正在运行的模组，不在就整体失败，免得录出标错变体的数据
///   gene=1                             每件物品再按"基因扫描仪开着"录一遍，显示不同的另存一条（只改内存里的开关）
/// 读档后不修改、不存档：遍历各库存（递归）里的物品，按"标识 + 标签签名"去重、每种标识最多
/// <see cref="PerIdentifier"/> 件，对每件走实机悬停同一条路径（<c>GameItemElement.GetTooltip</c>，
/// 再交给 <c>OverlayHandler.ShowTooltip</c>），把结果写进 tooltip-snapshot.json，状态写 tooltipsnap.txt 后退出。
/// 格式见 tools/TOOLTIP_SNAPSHOT.md；HTML 由 tools/tooltip_preview.py 离线生成。只在 SelfTest 构建里编译。
/// </summary>
internal static class TooltipSnapshotExport
{
    private const string TriggerFile = "tooltipsnap.flag";
    private const string ReportFile = "tooltipsnap.txt";
    internal const string OutputFile = "tooltip-snapshot.json";
    private const int PerIdentifier = 3;
    private const int MaxItems = 1500;
    private const int PerFrame = 8;

    private static string _modId = "item_details";
    private static string _modVersion = "";
    private static int _slot = -1;
    private static string _variant = "";
    private static readonly List<string> _suspend = new();
    private static readonly List<string> _require = new();
    private static readonly List<string> _suspended = new();
    private static bool _gene;
    private static bool _geneWasOn;
    private static int _geneDiffers;
    private static readonly List<(string Key, string Value)> _fontInfo = new();
    private static bool _armed;
    private static bool _started;

    internal static void MaybeStart(string modId, string modVersion)
    {
        try
        {
            if (!ModHelper.ModDataFileExists(modId, TriggerFile)) return;
            _modId = modId;
            _modVersion = modVersion ?? "";
            string text = (ModHelper.ReadModDataFileText(modId, TriggerFile) ?? "").Trim();
            ModHelper.DeleteModDataFile(modId, TriggerFile);
            if (!ParseFlag(text))
            {
                Finish(false, "flag 不是有效槽位：" + text);
                return;
            }
            _armed = true;
            Write("RUNNING", "等待读取存档槽位 " + _slot);
            ModHook.OnGameLoadedLate += Arm;
            var go = new GameObject("ItemDetails_TooltipSnapshotAutoLoad");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<AutoLoader>();
        }
        catch (Exception ex)
        {
            Log.Error("TooltipSnapshot.MaybeStart", ex);
            if (_armed) Finish(false, "启动失败：" + ex);
        }
    }

    private static bool ParseFlag(string text)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _slot)) return _slot >= 0;
        _slot = -1;
        foreach (string raw in text.Split(new[] { (char)10, (char)13, ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string value = line.Substring(eq + 1).Trim();
            switch (key)
            {
                case "slot":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _slot)) _slot = -1;
                    break;
                case "variant": _variant = value; break;
                case "suspend": AddIds(_suspend, value); break;
                case "require": AddIds(_require, value); break;
                case "gene": _gene = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
            }
        }
        return _slot >= 0;
    }

    private static void AddIds(List<string> list, string value)
    {
        foreach (string id in value.Split(','))
            if (id.Trim().Length > 0) list.Add(id.Trim());
    }

    /// <summary>模组开关探针（Modding/ModToggleProbe）登记的入口：(模组 ID, 是否停用) → 空串表示成功，否则是原因。</summary>
    private const string ToggleSlot = "mod_toggle_probe.suspend.v1";

    private static bool Running(string id)
    {
        foreach (var mod in ModLoader.Mods)
            if (mod?.Manifest != null && mod.Manifest.ID == id) return mod.IsEnabled && mod.Instance != null;
        return false;
    }

    /// <summary>按 flag 核对/停用模组；返回 null 表示可以开录，否则是失败原因。</summary>
    private static string? PrepareMods(List<string> report)
    {
        foreach (string id in _require)
            if (!Running(id)) return $"要求正在运行的模组 {id} 不在（没装、没启用或被加载器拒绝）";
        foreach (string id in _suspend)
        {
            if (!Running(id)) { report.Add($"{id} 本来就没在运行，无需停用"); continue; }
            if (!(AppDomain.CurrentDomain.GetData(ToggleSlot) is Func<string, bool, string> toggle))
                return $"要停用 {id}，但模组开关探针没有登记 {ToggleSlot}（没装、没启用或是旧版）";
            string result = toggle(id, true);
            if (!string.IsNullOrEmpty(result)) return $"停用 {id} 失败：{result}";
            _suspended.Add(id);
            report.Add($"已经由模组开关探针临时停用 {id}（只在本次运行内有效）");
        }
        return null;
    }

    /// <summary>与 TooltipDump 相同的自动读档：主菜单点"开始"读指定槽位，最多试 3 次。</summary>
    private sealed class AutoLoader : MonoBehaviour
    {
        private float _elapsed;
        private float _requestedAt = -1f;
        private int _tries;
        private bool _done;

        private void Update()
        {
            if (_done || !_armed) return;
            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed < 4f) return;
            try
            {
                bool loaded;
                try { loaded = PlayerStore.Instance != null; }
                catch (Exception ex) { Log.Warn("TooltipSnapshot.Loaded", ex); loaded = false; }
                if (loaded) { _done = true; Arm(); return; }
                if (_requestedAt >= 0f && Time.unscaledTime - _requestedAt < 20f) return;
                if (_tries >= 3) { _done = true; Finish(false, "自动读档失败"); return; }
                var slots = SaveUIManager.GetOrderedSlotIds();
                if (slots == null || !slots.Contains(_slot)) { _done = true; Finish(false, $"槽位 {_slot} 不存在"); return; }
                var menu = MainMenuUIController.Instance;
                var newGame = NewGameData.Instance;
                if (menu == null || newGame == null) return;
                _tries++;
                _requestedAt = Time.unscaledTime;
                newGame.saveSlot = _slot;
                newGame.saveFile = null;
                menu.StartNewGameClick();
            }
            catch (Exception ex) { Log.Warn("TooltipSnapshot.AutoLoad", ex); }
        }
    }

    private static void Arm()
    {
        try
        {
            ModHook.OnGameLoadedLate -= Arm;
            if (!_armed || _started) return;
            if (PlayerStore.Instance.saveSlotId != _slot) { Finish(false, $"读到的槽位是 {PlayerStore.Instance.saveSlotId}"); return; }
            _started = true;
            var go = new GameObject("ItemDetails_TooltipSnapshot");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Runner>();
        }
        catch (Exception ex) { Log.Error("TooltipSnapshot.Arm", ex); Finish(false, "无法启动：" + ex); }
    }

    // ---------------- 主流程 ----------------

    private sealed class Runner : MonoBehaviour
    {
        private float _elapsed;
        private float _readyAt = -1f;
        private bool _done;
        private List<GameItemElement>? _queue;
        private int _next;
        private PatchScope? _scope;
        private readonly List<string> _report = new();
        private readonly List<string> _items = new();
        private readonly Stopwatch _watch = new();
        private int _failed;

        private void Update()
        {
            if (_done) return;
            _elapsed += Time.deltaTime;
            if (_readyAt < 0f)
            {
                GameItem? probe = null;
                try { probe = DirectoryMaster.Create<GameItem>("plasma_fuel"); }
                catch (Exception ex) { Log.Warn("TooltipSnapshot.Catalog", ex); }
                if (probe == null) { if (_elapsed > 40f) Stop(false, "物品目录一直没就绪"); return; }
                try { probe.Destroy(); }
                catch (Exception ex) { Log.Warn("TooltipSnapshot.ProbeDestroy", ex); }
                _readyAt = _elapsed;
                return;
            }
            if (_elapsed - _readyAt < 2f) return;

            try
            {
                if (_queue == null)
                {
                    _watch.Start();
                    string? blocked = PrepareMods(_report);
                    if (blocked != null) { Stop(false, blocked); return; }
                    if (_gene)
                    {
                        _geneWasOn = PlayerStore.Instance.isGeneScannerScanning;
                        if (_geneWasOn) _report.Add("基因扫描仪在存档里已经开着：只录一遍，不另存开/关差异");
                    }
                    _queue = Collect(_report);
                    _scope = TooltipSnapshotRecorder.Install(_report);
                    WarmUp(_queue, _report);
                    return;
                }
                int end = Math.Min(_queue.Count, _next + PerFrame);
                for (; _next < end; _next++)
                {
                    var item = _queue[_next];
                    string? json = CaptureOne(item, null, out string? shown, out string? error);
                    if (json != null) _items.Add(json);
                    else { _failed++; _report.Add($"失败 {item.identifier}: {error}"); }
                    if (json == null || !_gene || _geneWasOn) continue;
                    // 基因扫描仪是个开关物品，开着时原版把 PlayerStore.isGeneScannerScanning 置真。
                    // 这里只改内存里的这一个布尔，录完立刻改回；不动物品标签、不存档。
                    string? scanned;
                    string? shownScanned;
                    PlayerStore.Instance.isGeneScannerScanning = true;
                    try { scanned = CaptureOne(item, "gene-scanner", out shownScanned, out _); }
                    finally { PlayerStore.Instance.isGeneScannerScanning = false; }
                    if (scanned != null && !string.Equals(shown, shownScanned, StringComparison.Ordinal))
                    {
                        _items.Add(scanned);
                        _geneDiffers++;
                    }
                }
                if (_next < _queue.Count) return;
                Stop(true, null);
            }
            catch (Exception ex) { Stop(false, "导出异常：" + ex); }
        }

        private void Stop(bool ok, string? error)
        {
            _done = true;
            try { OverlayHandler.current?.HideTooltip(); }
            catch (Exception ex) { _report.Add("HideTooltip 失败：" + ex.Message); }
            try { _scope?.Dispose(); }
            catch (Exception ex) { _report.Add("撤销录制补丁失败：" + ex.Message); }
            string? path = null;
            if (ok)
            {
                try { path = WriteDocument(_items); }
                catch (Exception ex) { ok = false; error = "写文件失败：" + ex; }
            }
            if (_gene && !_geneWasOn) _report.Add($"基因扫描仪开着时显示不同的物品：{_geneDiffers} 件（另存为 state=gene-scanner）");
            foreach (string stack in TooltipSnapshotRecorder.UnknownStacks) _report.Add("未归属的调用栈样本：" + stack);
            var summary = new System.Text.StringBuilder();
            summary.AppendLine($"slot={_slot} variant={_variant} items={_items.Count} failed={_failed} elapsed={_watch.Elapsed.TotalSeconds:0.0}s");
            if (path != null) summary.AppendLine("output=" + path);
            if (error != null) summary.AppendLine("error=" + error);
            foreach (string line in _report) summary.AppendLine(line);
            Finish(ok, summary.ToString());
        }
    }

    /// <summary>遍历 EmporiumEntry 上的全部库存，递归进每件物品的子库存；按签名去重，每种标识设上限。</summary>
    private static List<GameItemElement> Collect(List<string> report)
    {
        var result = new List<GameItemElement>();
        var seenItems = new HashSet<GameItem>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var perId = new Dictionary<string, int>(StringComparer.Ordinal);
        int visited = 0, capped = 0, notElement = 0;

        void Visit(GameItem item, int depth)
        {
            if (item == null || depth > 6 || !seenItems.Add(item)) return;
            visited++;
            string id = item.identifier ?? "";
            if (result.Count < MaxItems && item is GameItemElement element && signatures.Add(TooltipSnapshot.Signature(item)))
            {
                perId.TryGetValue(id, out int n);
                if (n < PerIdentifier) { perId[id] = n + 1; result.Add(element); }
                else capped++;
            }
            else if (!(item is GameItemElement)) notElement++;
            foreach (var inv in TooltipSnapshot.Inventories(item))
            {
                List<GameItem>? children = null;
                try { children = inv.childItems; }
                catch (Exception ex) { Log.Warn("TooltipSnapshot.Collect", ex); }
                if (children == null) continue;
                foreach (var child in children.ToArray()) Visit(child, depth + 1);
            }
        }

        var emporium = EmporiumEntry.Instance;
        int roots = 0;
        foreach (var prop in typeof(EmporiumEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!typeof(GameInventory).IsAssignableFrom(prop.PropertyType) || prop.GetIndexParameters().Length > 0) continue;
            GameInventory? inv = null;
            try { inv = prop.GetValue(emporium) as GameInventory; }
            catch (Exception ex) { report.Add($"读库存 {prop.Name} 失败：{ex.GetType().Name}"); }
            if (inv?.childItems == null) continue;
            roots++;
            foreach (var item in inv.childItems.ToArray()) Visit(item, 0);
        }
        report.Add($"库存根 {roots} 个；遍历物品 {visited} 件，录制 {result.Count} 件，" +
                   $"同标识超过 {PerIdentifier} 件跳过 {capped} 件，非 GameItemElement 跳过 {notElement} 件；上限 {MaxItems}");
        return result;
    }

    // ---------------- 反射：本模组自己的私有状态（只读） ----------------

    private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly FieldInfo? BadgeTemplate = AccessTools.Field(typeof(TypeBadge), "_textTemplate");
    private static readonly FieldInfo? BadgeObject = AccessTools.Field(typeof(TypeBadge), "_badge");
    private static readonly MethodInfo? BadgeColors = typeof(TypeBadge).GetMethod("ChipColors", Static);
    private static readonly MethodInfo? BadgeWarning = typeof(TypeBadge).GetMethod("IsWarning", Static);
    private static readonly FieldInfo? GridItem = AccessTools.Field(typeof(ContentsGrid), "_pendingItem");
    private static readonly FieldInfo? GridContents = AccessTools.Field(typeof(ContentsGrid), "_pendingContents");
    private static readonly FieldInfo? GridSealed = AccessTools.Field(typeof(ContentsGrid), "_pendingSealed");
    private static readonly FieldInfo? GridEstimate = AccessTools.Field(typeof(ContentsGrid), "_pendingSealedEstimate");
    private static readonly FieldInfo? GridTemplate = AccessTools.Field(typeof(ContentsGrid), "_template");
    private static readonly FieldInfo? BarTable = AccessTools.Field(typeof(ProgressBarStyle), "RichTextBars");
    private static readonly FieldInfo? TooltipElement = AccessTools.Field(typeof(OverlayHandler), "tooltipElement");
    private static readonly FieldInfo? TextNode = AccessTools.Field(typeof(RichTextElement), "textNode");
    private static readonly PropertyInfo? NodeText = AccessTools.Property(typeof(TreeNodeRender), "text");

    /// <summary>
    /// 徽章模板与内容物格模板只在第一次真正显示 tooltip 时取到；没取到之前原版「[类型]」不会被抑制、
    /// 内容物也不会被认领。先对前几件物品走一遍显示，让两者进入实机悬停过一次之后的状态。
    /// </summary>
    private static void WarmUp(List<GameItemElement> items, List<string> report)
    {
        int tries = 0;
        foreach (var item in items)
        {
            if (Ready(BadgeTemplate) && Ready(GridTemplate)) break;
            if (++tries > 30) break;
            try { Show(item, item.GetTooltip()); }
            catch (Exception ex) { report.Add($"预热 {item.identifier} 失败：{ex.GetType().Name}"); }
        }
        report.Add($"预热 {tries} 件：徽章模板={(Ready(BadgeTemplate) ? "就绪" : "未就绪")}，" +
                   $"内容物格模板={(Ready(GridTemplate) ? "就绪" : "未就绪")}");
    }

    private static bool Ready(FieldInfo? field)
    {
        if (field == null) return false;
        return field.GetValue(null) is UnityEngine.Object o && o != null;
    }

    /// <summary>与 ItemTooltipHandler 相同的窗口字符上限。</summary>
    private static int WindowCap(GameItem item)
    {
        if (item.IsTag("FEED_DISPENSER_TAG")) return 90;
        if (item.IsGameItemType("MACHINE")) return 72;
        if (item.IsTag("MACHINE_MANUAL_TAG")) return 120;
        return 60;
    }

    private static void Show(GameItem item, RichText text)
    {
        var overlay = OverlayHandler.current;
        if (overlay == null || text == null) return;
        var position = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        overlay.ShowTooltip(position, Vector2.zero, text, WindowCap(item));
    }

    private static string? CaptureOne(GameItemElement item, string? state, out string? shown, out string? error)
    {
        error = null;
        shown = null;
        RichText? result = null;
        TooltipSnapshotRecorder.BeginSession();
        try { result = item.GetTooltip(); }
        catch (Exception ex) { error = "GetTooltip：" + ex.GetType().Name + " " + ex.Message; }
        finally { TooltipSnapshotRecorder.EndSession(); }
        if (result == null) { error ??= "GetTooltip 返回 null"; return null; }

        TooltipSnapshotRecorder.Frame? frame = null;
        foreach (var f in TooltipSnapshotRecorder.Completed)
            if (ReferenceEquals(f.Final, result) && f.Depth == 0) frame = f;
        var capture = new TooltipSnapshot.ItemCapture { Item = item, Source = "ingame", State = state };
        if (frame == null)
        {
            foreach (var f in TooltipSnapshotRecorder.Completed)
                if (f.Depth == 0) frame = f;
            capture.Notes.Add("没找到返回值对应的 Build 帧，取最后一次外层 Build");
        }
        if (frame == null || frame.Raw == null) { error = "没录到 Build（录制补丁可能没装上）"; return null; }

        capture.Folded = frame.Folded;
        capture.Raw = frame.Raw;
        capture.RawWidth = frame.RawWidth;
        capture.Final = result;
        capture.MaxCharWidth = frame.MaxCharWidth;
        capture.BuildMaxCharWidth = frame.BuildMaxCharWidth;
        capture.Company = frame.Company;
        capture.SpriteBars = Flag(() => ModSettings.BlockProgressBar, capture.Notes);
        capture.Bars = Bars(result, capture.Notes) ?? Bars(frame.Raw, capture.Notes);
        capture.Badges = Badges(capture.Notes);
        capture.Contents = Contents(item, capture.Notes);
        var calls = new List<TooltipSnapshotRecorder.Call>(TooltipSnapshotRecorder.Calls);
        capture.WriteCalls = w => TooltipSnapshotRecorder.WriteCalls(w, calls);
        capture.RootBuilder = TooltipSnapshotRecorder.BuilderId(frame.Builder);
        capture.Window = Window(item, result);
        // 比较两种状态是否显示不同：优先用交给 TMP 的最终文字，拿不到就用 Build 出来的前景层。
        shown = capture.Window.Text ?? result.richTextForeground;

        var json = new JsonOut();
        TooltipSnapshot.WriteItem(json, capture);
        return json.ToString();
    }

    private static bool Flag(Func<bool> read, List<string> notes)
    {
        try { return read(); }
        catch (Exception ex) { notes.Add("读设置失败：" + ex.GetType().Name); return false; }
    }

    private static List<ProgressBarStyle.BarRecord>? Bars(RichText text, List<string> notes)
    {
        if (BarTable == null) { notes.Add("ProgressBarStyle.RichTextBars 不在，条只记格数"); return null; }
        if (!(BarTable.GetValue(null) is ConditionalWeakTable<RichText, List<ProgressBarStyle.BarRecord>> table)) return null;
        lock (table)
        {
            return table.TryGetValue(text, out var list) ? new List<ProgressBarStyle.BarRecord>(list) : null;
        }
    }

    private static List<TooltipSnapshot.Badge>? Badges(List<string> notes)
    {
        if (BadgeColors == null || BadgeWarning == null)
        {
            notes.Add("TypeBadge 的私有成员不在，徽章不记录");
            return null;
        }
        bool enabled = Flag(() => ModSettings.TypeBadge, notes);
        if (!enabled) return null;
        var result = new List<TooltipSnapshot.Badge>();
        foreach (var (type, text) in TypeBadge.PendingForSnapshot())
        {
            var args = new object?[] { type, null, null };
            BadgeColors.Invoke(null, args);
            result.Add(new TooltipSnapshot.Badge
            {
                Type = type,
                Text = text,
                Background = args[1] is Color bg ? TooltipSnapshot.Hex(bg) : null,
                Border = args[2] is Color border ? TooltipSnapshot.Hex(border) : null,
                Warning = BadgeWarning.Invoke(null, new object[] { type }) is true,
            });
        }
        return result;
    }

    private static TooltipSnapshot.Contents? Contents(GameItem item, List<string> notes)
    {
        if (GridItem == null || GridContents == null || GridSealed == null || GridEstimate == null)
        {
            notes.Add("ContentsGrid 的私有成员不在，内容物格不记录");
            return null;
        }
        if (!ReferenceEquals(GridItem.GetValue(null), item)) return null;
        if (!(GridContents.GetValue(null) is ContainerContents contents)) return null;
        var rollup = ContentsRollup.Build(contents.Facts, 15);
        var result = new TooltipSnapshot.Contents
        {
            Sealed = GridSealed.GetValue(null) is true,
            Estimate = GridEstimate.GetValue(null) as string,
            Total = rollup.Total,
            Overflow = rollup.Overflow,
        };
        for (int i = 0; i < rollup.Groups.Count && i < rollup.ShownCount; i++)
        {
            var g = rollup.Groups[i];
            string id = g.FirstIndex >= 0 && g.FirstIndex < contents.Items.Count ? contents.Items[g.FirstIndex].identifier ?? "" : "";
            result.Cells.Add(new TooltipSnapshot.ContentsCell { Id = id, Name = g.Name, Count = g.Count });
        }
        return result;
    }

    /// <summary>交给原版窗口显示一次，读回提交给 TMP 的最终前景文字与窗口尺寸（边界 2）。</summary>
    private static TooltipSnapshot.Window Window(GameItem item, RichText text)
    {
        var window = new TooltipSnapshot.Window { MaxChars = WindowCap(item) };
        try
        {
            Show(item, text);
            var overlay = OverlayHandler.current;
            if (overlay == null || TooltipElement == null) { window.Error = "OverlayHandler 或 tooltipElement 不在"; return window; }
            if (TooltipElement.GetValue(overlay) is RichTextElement element)
            {
                window.WidthPx = element.widthPixels;
                window.HeightPx = element.heightPixels;
                if (TextNode != null && NodeText != null && TextNode.GetValue(element) is object node
                    && NodeText.GetValue(node) is TMPro.TMP_Text tmp)
                {
                    window.Text = tmp.text;
                    if (_fontInfo.Count == 0) ReadFont(tmp);
                }
            }
            if (BadgeObject != null)
                window.BadgesShown = BadgeObject.GetValue(null) is GameObject badge && badge != null && badge.activeSelf;
        }
        catch (Exception ex) { window.Error = ex.GetType().Name + " " + ex.Message; }
        return window;
    }

    /// <summary>tooltip 实际用的字体：预览页据此从游戏资源里挑同一款字体文件。只读一次。</summary>
    private static void ReadFont(TMPro.TMP_Text tmp)
    {
        try
        {
            var font = tmp.font;
            if (font == null) { _fontInfo.Add(("fontError", "tooltip 文本没有字体资源")); return; }
            _fontInfo.Add(("font", font.name ?? ""));
            _fontInfo.Add(("fontFamily", FaceName(font)));
            _fontInfo.Add(("fontSize", tmp.fontSize.ToString("0.##", CultureInfo.InvariantCulture)));
            _fontInfo.Add(("fontLineHeight", font.faceInfo.lineHeight.ToString("0.##", CultureInfo.InvariantCulture)
                                             + "/" + font.faceInfo.pointSize.ToString("0.##", CultureInfo.InvariantCulture)));
            var names = new List<string>();
            var queue = new List<TMPro.TMP_FontAsset> { font };
            for (int i = 0; i < queue.Count && i < 32; i++)
            {
                var table = queue[i].fallbackFontAssetTable;
                if (table == null) continue;
                foreach (var fallback in table)
                {
                    if (fallback == null || queue.Contains(fallback)) continue;
                    queue.Add(fallback);
                    names.Add((fallback.name ?? "") + "=" + FaceName(fallback));
                }
            }
            var global = TMPro.TMP_Settings.fallbackFontAssets;
            if (global != null)
                foreach (var fallback in global)
                {
                    if (fallback == null || queue.Contains(fallback)) continue;
                    queue.Add(fallback);
                    names.Add((fallback.name ?? "") + "=" + FaceName(fallback));
                }
            _fontInfo.Add(("fontFallbacks", string.Join("; ", names)));
        }
        catch (Exception ex) { _fontInfo.Add(("fontError", ex.GetType().Name + " " + ex.Message)); }
    }

    private static string FaceName(TMPro.TMP_FontAsset font)
        => (font.faceInfo.familyName ?? "") + "|" + (font.faceInfo.styleName ?? "");

    // ---------------- 写出 ----------------

    private static string WriteDocument(List<string> items)
    {
        var meta = new TooltipSnapshot.Meta
        {
            Source = "ingame",
            GameVersion = Application.version,
            AssemblyVersionId = TooltipSnapshot.AssemblyMvid(),
            ModVersion = _modVersion,
            Language = Loc.Code,
            ExportedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            SaveSlot = _slot,
        };
        try
        {
            if (UISettings.current != null)
            {
                meta.CharWidthScale = UISettings.current.GetCharWidthScale();
                meta.EffectiveFontWidth = UISettings.current.effectiveFontWidth;
            }
        }
        catch (Exception ex) { meta.Extra.Add(("uiSettingsError", ex.GetType().Name)); }
        var notes = new List<string>();
        meta.Settings.Add(("infoEnhance", Flag(() => ModSettings.InfoEnhance, notes)));
        meta.Settings.Add(("spriteBars", Flag(() => ModSettings.BlockProgressBar, notes)));
        meta.Settings.Add(("typeBadge", Flag(() => ModSettings.TypeBadge, notes)));
        meta.Settings.Add(("typeBadgeMuted", Flag(() => ModSettings.TypeBadgeMuted, notes)));
        meta.Settings.Add(("contentsGrid", Flag(() => ModSettings.ContentsGrid, notes)));
        meta.Settings.Add(("manufacturerInline", Flag(() => ModSettings.ManufacturerInline, notes)));
        meta.Settings.Add(("tooltipFormatting", Flag(() => ModSettings.TooltipFormatting, notes)));
        foreach (string note in notes) meta.Extra.Add(("settingsNote", note));
        if (_variant.Length > 0) meta.Extra.Add(("variant", _variant));
        try
        {
            var running = new List<string>();
            foreach (var mod in ModLoader.Mods)
                if (mod?.Manifest != null && mod.IsEnabled && mod.Instance != null && !_suspended.Contains(mod.Manifest.ID))
                    running.Add(mod.Manifest.ID + "@" + mod.Manifest.ModVersion);
            meta.Extra.Add(("mods", string.Join("; ", running)));
        }
        catch (Exception ex) { meta.Extra.Add(("modsError", ex.GetType().Name)); }
        if (_suspended.Count > 0) meta.Extra.Add(("suspended", string.Join("; ", _suspended)));
        if (_gene) meta.Extra.Add(("geneScanner", _geneWasOn ? "on" : "off+on"));
        foreach (var entry in _fontInfo) meta.Extra.Add(entry);

        var w = new JsonOut();
        w.BeginObject();
        TooltipSnapshot.WriteHeader(w, meta);
        w.Prop("items").BeginArray();
        foreach (string item in items) w.Raw(item);
        w.EndArray();
        w.EndObject();
        ModHelper.WriteModDataFileText(_modId, OutputFile, w.ToString());
        return System.IO.Path.Combine(ModHelper.GetModDataFolder(_modId), OutputFile);
    }

    private static void Write(string status, string details)
    {
        try { ModHelper.WriteModDataFileText(_modId, ReportFile, $"status={status}{Environment.NewLine}{details}"); }
        catch (Exception ex) { Log.Error("TooltipSnapshot.Write", ex); }
    }

    private static void Finish(bool ok, string details)
    {
        _armed = false;
        Write(ok ? "DONE" : "FAIL", details);
        Log.Msg($"[TooltipSnapshot] {(ok ? "DONE OK" : "DONE FAILED")}");
        Application.Quit(ok ? 0 : 1);
    }
}
#endif
