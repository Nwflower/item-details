#if ITEMDETAILS_SELFTEST
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ItemDetails;

/// <summary>
/// 无头自检的驱动部分：触发、自动读档、跑完退出。
///
/// 触发方式有两种，正常游玩都不受影响：
///   - 命令行 <c>-itemdetails-selftest</c>；
///   - 工具写入的一次性 <c>ModData/item_details/selftest.flag</c>（启动游戏不带参数，
///     文件内容必须是专用测试存档的槽位 ID）。命令行还需指定
///     <c>-itemdetails-selftest-slot ID</c>。
///
/// 关键是物品目录是 MonoBehaviour，主菜单阶段 <c>DirectoryMaster.Create</c> 必然返回 null，
/// 所以要等存档加载完成后再跑，并由 <see cref="AutoLoader"/> 读取明确指定的测试存档。
///
/// 用例与探针断言在 <c>SelfTest.Checks.cs</c>；本文件只管"什么时候跑、跑完怎么退出"。
/// </summary>
internal static partial class SelfTest
{
    private const string Flag = "-itemdetails-selftest";
    private const string TriggerFile = "selftest.flag";
    private static bool _runnerStarted;

    internal static void MaybeStart(string modId)
    {
        try
        {
            bool viaArgs = false;
            string[] args = Environment.GetCommandLineArgs();
            foreach (string a in args)
            {
                if (string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase))
                {
                    viaArgs = true;
                    break;
                }
            }

            // 也可以由工具放一个一次性标志文件触发：这样启动游戏完全不带参数，
            // Steam 不会弹「用自定义参数启动游戏」确认框，才能无人值守。
            bool viaFile = ModHelper.ModDataFileExists(modId, TriggerFile);
            if (!viaArgs && !viaFile) return;

            BeginRun(modId);
            string? trigger = viaFile ? ConsumeTriggerFile(modId) : null;
            if (!TryReadSlot(args, trigger, out _saveSlot, out string error))
            {
                Finish(false, error);
                return;
            }
            _runnerStarted = false;
            Log.Msg($"[SelfTest] armed by {(viaArgs ? "command line" : "trigger file")}");

            // 物品目录是 MonoBehaviour（Directory<T>），只有游戏场景加载后才注册，
            // 主菜单阶段 DirectoryMaster.Create 必然返回 null。
            // 因此等存档加载完成后再跑，而不是立刻轮询。
            ModHook.OnGameLoadedLate += Arm;
            Log.Msg("[SelfTest] armed, will run after a save is loaded");

            // 主菜单不会触发 OnGameLoadedLate，自动载入指定槽位。
            var go = new GameObject("ItemDetails_SelfTestAutoLoad");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<AutoLoader>();
        }
        catch (Exception ex)
        {
            Log.Error("SelfTest.MaybeStart", ex);
            if (Requested) Finish(false, "Startup failed: " + ex);
        }
    }

    /// <summary>读取专用测试槽位并删除一次性触发文件。</summary>
    private static string ConsumeTriggerFile(string modId)
    {
        string text = ModHelper.ReadModDataFileText(modId, TriggerFile) ?? "";
        ModHelper.DeleteModDataFile(modId, TriggerFile);
        return text;
    }

    /// <summary>
    /// 主菜单自动读取明确指定的测试槽位。
    ///
    /// 走过的两条弯路，别再踩：
    ///   - `MainMenuUIController.OnContinueClick()` 是**新游戏**（天赋/难度）流程，不读档；
    ///   - `SaveManager.HasSave()` 只反映内存里的 `saveJson`，主菜单恒为 null，不是"有没有存档"。
    /// 真正的读档入口是 LoadGameUI.LoadSlot：把 slot 写进 NewGameData，再走
    /// `StartNewGameClick()`（加载 InventoryScene，场景里再按 saveSlot 读 ES3）。
    /// </summary>
    private sealed class AutoLoader : MonoBehaviour
    {
        private float _elapsed;
        private float _nextTry;
        private int _tries;
        private bool _done;
        private bool _requested;
        private float _requestedAt;

        // 主菜单可能还没就绪、读档也需要时间，因此带重试而不是一次性放弃。
        private void Update()
        {
            if (_done || !Requested) return;

            _elapsed += Time.unscaledDeltaTime;
            if (_elapsed < 4f) return;

            try
            {
                // PlayerStore.Instance 的 getter 在主菜单会尝试构造实例并抛 NRE，
                // 不能直接把"读它"当作判据——必须单独包住，否则整个轮询会卡在异常上。
                bool loaded;
                try { loaded = PlayerStore.Instance != null; }
                catch { loaded = false; }

                if (loaded)      // 已经进游戏（可能自动续档）
                {
                    _done = true;
                    Arm();
                    return;
                }

                // 已发出读档请求：先给它 20 秒，超时再重试
                if (_requested && Time.unscaledTime - _requestedAt < 20f) return;
                if (_elapsed < _nextTry) return;
                _nextTry = _elapsed + 3f;

                if (_tries >= 3)
                {
                    _done = true;
                    Finish(false, "Automatic save loading failed.");
                    return;
                }

                var slots = SaveUIManager.GetOrderedSlotIds();
                if (slots == null || !slots.Contains(_saveSlot))
                {
                    _done = true;
                    Finish(false, $"Requested self-test save slot {_saveSlot} does not exist.");
                    return;
                }

                var menu = MainMenuUIController.Instance;
                var newGame = NewGameData.Instance;
                if (menu == null || newGame == null) return;   // 场景还没就绪，下次再试

                int slotId = _saveSlot;
                _tries++;
                _requested = true;
                _requestedAt = Time.unscaledTime;

                // 与 LoadGameUI.LoadSlot 相同的赋值；saveFile=null 表示用该 slot 的当前存档
                newGame.saveSlot = slotId;
                newGame.saveFile = null;
                menu.StartNewGameClick();

                Log.Msg($"[SelfTest] loading selected test save slot {slotId} (try {_tries})");
            }
            catch (Exception ex)
            {
                Log.Warn("SelfTest.AutoLoad", ex);
            }
        }
    }

    private static void Arm()
    {
        try
        {
            ModHook.OnGameLoadedLate -= Arm;
            if (!Requested || _runnerStarted) return;
            if (PlayerStore.Instance.saveSlotId != _saveSlot)
            {
                Finish(false, $"Loaded save slot {PlayerStore.Instance.saveSlotId} differs from requested test slot {_saveSlot}.");
                return;
            }
            _runnerStarted = true;
            var go = new GameObject("ItemDetails_SelfTest");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Runner>();
            Log.Msg("[SelfTest] save loaded, running checks");
        }
        catch (Exception ex)
        {
            Log.Error("SelfTest.Arm", ex);
            Finish(false, "Could not start the test runner: " + ex);
        }
    }

    private sealed class Runner : MonoBehaviour
    {
        private float _elapsed;
        private bool _done;

        private void Update()
        {
            if (_done) return;
            _elapsed += Time.deltaTime;

            // 只是探一下物品目录是否就绪：目录是 MonoBehaviour，存档加载后才注册，
            // 未就绪时 Create 返回 null；就绪后立刻销毁探针，避免每帧漏一个 GameItem。
            var probe = TryCreate("plasma_fuel");
            if (probe == null)
            {
                if (_elapsed > 40f)
                {
                    _done = true;
                    Finish(false, "Item directory never became ready.");
                }
                return;
            }
            try { probe.Destroy(); } catch { }

            _done = true;
            try
            {
                bool success;
                string report;
                using (new TestSettings()) success = RunAll(out report);
                Finish(success, report);
            }
            catch (Exception ex)
            {
                Finish(false, "Test execution failed: " + ex);
            }
        }
    }

    // ---------------- 用例 ----------------

    private sealed class Case
    {
        internal string Name = "";
        internal GameItem? Item;
        internal int? Expected;
    }

    private static bool RunAll(out string reportText)
    {
        var cases = new List<Case>();
        try
        {
            AddPlasmaCases(cases);
            AddHandCases(cases);
            AddRibwichCases(cases);
        }
        catch (Exception ex)
        {
            Log.Error("SelfTest.BuildCases", ex);
            reportText = "Case construction failed: " + ex;
            return false;
        }

        int pass = 0, fail = 0;
        var report = new StringBuilder();
        report.AppendLine("Item Details self-test");

        // 行级快照：直接跑我们的渲染器，把最终文本打进日志（不需要悬停、不看屏幕）
        DumpSnapshots();

        foreach (Case c in cases)
        {
            string line = Evaluate(c, out bool ok);
            if (ok)
            {
                pass++;
                Log.Msg("[SelfTest] PASS " + line);
            }
            else
            {
                fail++;
                Log.Msg("[SelfTest] FAIL " + line);
            }
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：探针克隆机制（泛型分解依赖它，离线测不到）
        {
            string line = CheckProbeMechanism(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：探针标签必须与真实物品隔离（曾因浅拷贝污染玩家存档里的水）
        {
            string line = CheckTagIsolation(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：泛型消融不得改写真实鉴定物品（同类浅拷贝风险）
        {
            string line = CheckGenericDoesNotMutate(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：泛型快照不得静默截断第 24 项之后的标签
        {
            string line = CheckGenericTagSnapshot(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：只读标签读取不得向真实物品插入缺失键（GetTagReadonly 的坑）
        {
            string line = CheckTagReadIsolation(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：机器净化模拟不得改动真实机器（99.0% 那个 bug 的回归护栏）
        {
            string line = CheckMachineSimIsolation(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：水质行不得给真实物品添加特性（GetWaterFeature 的读即写）
        {
            string line = CheckWaterInfoDoesNotAddFeature(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：酒瓶跳过净化模拟，纯度读取不插入缺失标签
        {
            string line = CheckWaterAdviceReadIsolation(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：相符的水经完整 tooltip 后不得被自动交易系统改判为错标签
        {
            string line = CheckWaterSaleStateIsolation(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：直接执行游戏真实售水结算，必须出水、入账且无误标声誉损失
        {
            string line = CheckWaterFountainTransaction(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：几瓶水交替访问时，多槽水质建议缓存不得互相淘汰
        {
            string line = CheckWaterAdviceCache(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：游戏原生双击回调应直接打开鉴定面板
        {
            string line = CheckDoubleClickInspect(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：机器电池槽里的电量应显示为 current/max
        {
            string line = CheckMachineBattery(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：上锁容器不能被“容器信息”路径展示，密封箱模块仍独立可见
        {
            string line = CheckLockedContainerGating(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        // 额外验证：当前语言的 Localization/*.csv 真的被读到（多语种改造的回归护栏）
        {
            string line = CheckLocalization(out bool ok);
            if (ok) pass++; else fail++;
            Log.Msg($"[SelfTest] {(ok ? "PASS" : "FAIL")} {line}");
            report.AppendLine((ok ? "PASS " : "FAIL ") + line);
        }

        reportText = $"pass={pass} fail={fail} total={pass + fail}{Environment.NewLine}{report}";
        return fail == 0;
    }

    /// <summary>
    /// 行级快照：对若干代表性物品直接跑一遍渲染器，把最终纯文本打进日志。
    /// 目的是"不悬停也能看到我们的输出长什么样"，为后续断言打基础。
    /// </summary>
    private static void DumpSnapshots()
    {
        string[] ids =
        {
            "plasma_fuel", "hand", "ribwich", "scratcher", "gamblers_coin",
            "trashcan", "drain", "faucet", "intel_sec",
        };

        var entries = new List<TooltipReport.Entry>();

        foreach (string id in ids)
        {
            var entry = new TooltipReport.Entry { Id = id };
            entries.Add(entry);

            GameItem? item = null;
            try { item = TryCreate(id); } catch { }
            if (item == null)
            {
                entry.Note = "创建失败";
                Log.Msg($"[Snap] {id}: <create failed>");
                continue;
            }

            try { entry.Name = GameRead.ItemName(item); } catch { }

            // 新造物品缺少初始化标签（鉴定品尤其如此），先按类型跑一次游戏初始化
            try
            {
                switch (id)
                {
                    case "plasma_fuel": PlasmaHelper.InitPlasmaItem(item); break;
                    case "hand": AppHandHelper.InitAppHandItem(item); break;
                    case "ribwich":
                        RibwichHelper.Init(item);                    // 先建基础标签（RIBWICH_TAG / APPRAISAL_ITEM）
                        RibwichHelper.InitCorrectNormal(item);       // 再补成合法未开封品
                        break;
                }
            }
            catch (Exception ex) { Log.Warn("SelfTest.SnapInit." + id, ex); }

            string? text = ModEntry.RenderTooltipText(item);
            if (string.IsNullOrEmpty(text))
            {
                entry.Note = "无输出（该物品可能缺少初始化标签，或本模组没有对应内容）";
                string diag = "";
                try { diag = $"native={GameRead.TrueValue(item)?.ToString() ?? "null"} kind={ValueBreakdown.DetectKind(item)}"; } catch { }
                Log.Msg($"[Snap] {id}: <no text> {diag}");
                continue;
            }

            foreach (string line in text.Replace("\r", "").Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) entry.Lines.Add(trimmed);
            }

            string flat = text.Replace("\r", "").Replace("\n", " ⏎ ").Trim();
            Log.Msg($"[Snap] {id}: {flat}");
        }

        try
        {
            string html = TooltipReport.BuildHtml(entries, "Item Details · tooltip 快照");
            string? path = TooltipReport.Write("item_details", "tooltip-report.html", html);
            Log.Msg($"[Snap] report: {path ?? "<write failed>"}");
        }
        catch (Exception ex)
        {
            Log.Warn("SelfTest.Report", ex);
        }
    }
}
#endif
