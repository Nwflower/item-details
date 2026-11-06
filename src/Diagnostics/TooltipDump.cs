#if ITEMDETAILS_SELFTEST
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 一次性排版探针：ModData/item_details/tooltipdump.flag（内容为存档槽位 ID）。
/// 读档后不修改、不存档，把存档里液体容器的正文（走与本模组相同的渲染器）写进
/// tooltipdump.txt 后退出。用来在不截图的情况下检查水质行的列对齐与折行。
/// 只在 SelfTest 构建里编译。
/// </summary>
internal static class TooltipDump
{
    private const string TriggerFile = "tooltipdump.flag";
    private const string ReportFile = "tooltipdump.txt";

    private static string _modId = "item_details";
    private static int _slot = -1;
    private static bool _armed;
    private static readonly StringBuilder Report = new();

    internal static void MaybeStart(string modId)
    {
        try
        {
            if (!ModHelper.ModDataFileExists(modId, TriggerFile)) return;
            _modId = modId;
            string text = (ModHelper.ReadModDataFileText(modId, TriggerFile) ?? "").Trim();
            ModHelper.DeleteModDataFile(modId, TriggerFile);
            if (!int.TryParse(text, out _slot) || _slot < 0) { Finish(false, "flag 不是有效槽位：" + text); return; }
            _armed = true;
            Write("RUNNING", "等待读取存档槽位 " + _slot);
            ModHook.OnGameLoadedLate += Arm;
            var go = new GameObject("ItemDetails_TooltipDumpAutoLoad");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<AutoLoader>();
        }
        catch (Exception ex)
        {
            Log.Error("TooltipDump.MaybeStart", ex);
            if (_armed) Finish(false, "启动失败：" + ex);
        }
    }

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
                try { loaded = PlayerStore.Instance != null; } catch { loaded = false; }
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
            catch (Exception ex) { Log.Warn("TooltipDump.AutoLoad", ex); }
        }
    }

    private static bool _started;

    private static void Arm()
    {
        try
        {
            ModHook.OnGameLoadedLate -= Arm;
            if (!_armed || _started) return;
            if (PlayerStore.Instance.saveSlotId != _slot) { Finish(false, $"读到的槽位是 {PlayerStore.Instance.saveSlotId}"); return; }
            _started = true;
            var go = new GameObject("ItemDetails_TooltipDump");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Runner>();
        }
        catch (Exception ex) { Log.Error("TooltipDump.Arm", ex); Finish(false, "无法启动：" + ex); }
    }

    private sealed class Runner : MonoBehaviour
    {
        private float _elapsed;
        private float _readyAt = -1f;
        private bool _done;

        private void Update()
        {
            if (_done) return;
            _elapsed += Time.deltaTime;
            if (_readyAt < 0f)
            {
                GameItem? probe = null;
                try { probe = DirectoryMaster.Create<GameItem>("plasma_fuel"); } catch { }
                if (probe == null) { if (_elapsed > 40f) { _done = true; Finish(false, "物品目录一直没就绪"); } return; }
                try { probe.Destroy(); } catch { }
                _readyAt = _elapsed;
                return;
            }
            if (_elapsed - _readyAt < 2f) return;
            _done = true;
            try
            {
                Report.AppendLine($"slot={_slot}，LiquidGate={LiquidGate.Enabled}，InfoEnhance={ModSettings.InfoEnhance}");
                foreach (var item in Liquids())
                {
                    Report.AppendLine();
                    Report.AppendLine($"=== {item.identifier}（{GameRead.ItemDisplayName(item)}）容量={Capacity(item)} ===");
                    string? text = ModEntry.RenderTooltipText(item, 40);
                    Report.AppendLine(text ?? "（渲染失败）");
                }
                Report.AppendLine();
                Report.AppendLine("== 三列位置验证 ==");
                DumpColumnPositions();
                Report.AppendLine("== 字体度量 ==");
                DumpMetrics();
                Finish(true, Report.ToString());
            }
            catch (Exception ex) { Finish(false, Report + Environment.NewLine + "探针异常：" + ex); }
        }
    }

    private static IEnumerable<GameItem> Liquids()
    {
        var seen = new HashSet<string>();
        foreach (var root in new[] { EmporiumEntry.Instance.invElement, EmporiumEntry.Instance.backInvinvElement })
        {
            foreach (var item in root.childItems.ToList())
            {
                foreach (var candidate in Candidates(item))
                {
                    bool liquid;
                    try { liquid = candidate.IsTag("LIQUID_CONTAINER_TAG"); } catch { liquid = false; }
                    if (!liquid) continue;
                    if (!seen.Add(candidate.identifier)) continue;
                    yield return candidate;
                    if (seen.Count >= 8) yield break;
                }
            }
        }
    }

    private static IEnumerable<GameItem> Candidates(GameItem item)
    {
        yield return item;
        List<GameGridInventory>? grids = null;
        try { grids = StorageInfo.GridStorage(item); } catch { }
        if (grids == null) yield break;
        foreach (var grid in grids)
            foreach (var child in grid.childItems.ToList())
                yield return child;
    }

    private static string Capacity(GameItem item)
    {
        try
        {
            int cap = item.GetTagReadonly("LIQUID_CONTAINER_CAPACITY").GetInt();
            int total = 0;
            foreach (Liquid liquid in Liquid.Liquids) total += item.GetTagReadonly(liquid.currentPart).GetInt();
            return $"{total / 1000} / {cap / 1000} mL";
        }
        catch { return "?"; }
    }


    private static void DumpMetrics()
    {
        try
        {
            var any = UnityEngine.Object.FindObjectOfType<TMPro.TMP_Text>();
            TMPro.TMP_FontAsset? font = any != null ? any.font : TMPro.TMP_Settings.defaultFontAsset;
            Report.AppendLine($"字体：{(font != null ? font.name : "null")}，pointSize={(font != null ? font.faceInfo.pointSize.ToString("0.#") : "?")}，来源={(any != null ? "场景文本" : "默认")}");
            var go = new GameObject("MeasureTmp", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
            go.transform.SetParent(null, false);
            var tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
            tmp.font = font;
            tmp.fontSize = 16f;
            tmp.enableWordWrapping = false;
            foreach (string s in new[] { " ", "0", "水", "化学污染物", "物理污染物", "1962.0mL", "11.4mL", "2000/2000mL", "98.10%" })
                Report.AppendLine($"  宽[{s}] = {tmp.GetPreferredValues(s, 5000f, 5000f).x:0.00}px");
            UnityEngine.Object.Destroy(go);
        }
        catch (Exception ex) { Report.AppendLine("字体度量失败：" + ex.GetType().Name + " " + ex.Message); }
    }


    private static void DumpColumnPositions()
    {
        try
        {
            GameItem? sample = null;
            foreach (var it in Liquids()) { if (it.identifier == "large_bottled_water") { sample = it; break; } }
            if (sample == null) { Report.AppendLine("没有大瓶水样本"); return; }
            var r = WaterInfo.Analyze(sample);
            var rows = WaterColumns.Build(LiquidQueries.Snapshot(r, sample));

            TMPro.TMP_FontAsset? font = null;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TMPro.TMP_Text>())
                if (t.font != null && t.font.name.Contains("SC")) { font = t.font; break; }
            font ??= TMPro.TMP_Settings.defaultFontAsset;
            var canvasGo = new GameObject("MeasureCanvas", typeof(RectTransform), typeof(UnityEngine.Canvas));
            var canvas = canvasGo.GetComponent<UnityEngine.Canvas>();
            canvas.renderMode = UnityEngine.RenderMode.ScreenSpaceOverlay;
            var go = new GameObject("MeasureCols", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
            go.transform.SetParent(canvasGo.transform, false);
            var tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
            tmp.font = font;
            tmp.fontSize = 16f;
            tmp.enableWordWrapping = false;
            foreach (var line in rows)
            {
                tmp.text = line.Text;
                tmp.ForceMeshUpdate();
                var ti = tmp.textInfo;
                var parts = new List<string>();
                for (int i = 0; i < ti.characterCount && parts.Count < 14; i++)
                {
                    var ci = ti.characterInfo[i];
                    if (ci.isVisible) parts.Add($"{ci.character}@{ci.bottomLeft.x:0.0}");
                }
                Report.AppendLine(string.Join(" ", parts));
            }
            UnityEngine.Object.Destroy(canvasGo);
        }
        catch (Exception ex) { Report.AppendLine("列位置验证失败：" + ex.GetType().Name + " " + ex.Message); }
    }

    private static void Write(string status, string details)
    {
        try { ModHelper.WriteModDataFileText(_modId, ReportFile, $"status={status}{Environment.NewLine}{details}"); }
        catch (Exception ex) { Log.Error("TooltipDump.Write", ex); }
    }

    private static void Finish(bool ok, string details)
    {
        _armed = false;
        Write(ok ? "DONE" : "FAIL", details);
        Log.Msg($"[TooltipDump] {(ok ? "DONE" : "FAILED")}");
        Application.Quit(ok ? 0 : 1);
    }
}
#endif

