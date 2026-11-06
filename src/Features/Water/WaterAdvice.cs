using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 水质提升预估：对玩家实际拥有的每种净化手段，在独立探针上跑一次游戏自己的处理函数，
/// 比较前后纯度，得到「用这种手段能把水提到多少」。
///
/// 安全边界：
///   - 容器探针 = DirectoryMaster.Create + 逐条 TagState.Clone（见 GameRead.DeepCloneTags）；
///   - 滤芯/化学剂同样深拷贝，保留真实耐久与全部标签，绝不用「满耐久默认品」代替；
///   - 机器同样深拷贝：PurifyContainer 内部会用 GetTagReadonly 读加成标签，
///     缺键时 TagSystem.GetTag 会插入 0，把真实机器的品质加成钉死（净水器只能到 99.0%）；
///   - 探针仅在当前场景复用，场景切换或停用时销毁；创建/调用失败一律不产出建议。
///
/// 效果口径全部复用原版函数，不自己写公式：
///   机器     MachinePurifier.PurifyContainer
///   便携     WaterHelper.PurifyToBaseWater
///   化学剂   ChemicalHelper.DissolveInto + ChemicalHelper.HandleTemperatureReaction
///            （净水片是「含 15ml 氯的可溶化学物品」；第三方可溶物先在标准试验水上溶一次，
///            纯度上升才算净化剂，见 <see cref="RunTrials"/>）
///   滤芯     WaterHelper.HandleFilter（逐液体，被去除量转成水；uv_filter 由其内部处理）
///
/// 这条路径比其它模块贵得多，所以按「液体组成 + 容器类型 + 净化前纯度 + 净化手段状态」缓存。
/// 不按容器实例缓存：同组成、同滤芯/设备状态的水可以共享结果；已安装滤芯立即改变 key，
/// 背包设备变化最迟经 PurifySources 的 2 秒报告刷新后改变 key，不再串用另一瓶水的建议。
/// 100 槽只是容量上限，条目仍受 30 秒 TTL 约束。
///
/// 探针壳按 identifier 池化复用。实测单次 66ms 中 DirectoryMaster.Create 占 55ms，
/// DeepCloneTags 仅 0.07ms；池化后新组成约 10ms。化学剂会被 DissolveInto 末尾 Destroy，
/// 由 <see cref="ProbeDestroyGuard"/> 只对池化壳挡住这次销毁，同样复用；守卫不可用时退回逐次克隆。
/// DissolveInto 只读化学剂的液体标签，所以所有化学剂共用一个空白壳，试验十几种可溶物也不必逐个 Create。
/// </summary>
internal static class WaterAdvice
{
    private const float CacheSeconds = 30f;
    // 容量只是上限：条目仍受 30 秒 TTL 约束。100 槽足以覆盖正常的水容器对比，
    // 线性扫描在工具提示频率下可忽略，真正防止卡顿的是不再让 A/B 互相淘汰。
    private const int MaxEntries = 100;
    // 第三方可溶物的标准试验：悬停时每次最多做几种，其余留到下次；预热阶段放宽。
    private const int TrialsPerHover = 4;
    private const int TrialsOnPrewarm = 64;
    // 持续悬停同一容器时，成分若每秒都在变（加热、沸腾、反应），至多每 5 秒重算一次，
    // 其间沿用这件容器上次的结果。只在净化手段没变时沿用：滤芯磨损、设备增减照常立即重算；
    // 鼠标离开超过 0.5 秒（拖动、换物品）也不再沿用。
    private const float RecomputeSeconds = 5f;
    private const float HoverGapSeconds = 0.5f;

    /// <summary>某种手段的模拟结果。</summary>
    internal sealed class Method
    {
        internal string Name = "";
        internal double GainPct;      // 纯度提升（百分点）
    }

    private sealed class CacheEntry
    {
        internal long Signature;
        internal int Purity;
        internal string FilterName = "";
        internal string Locale = "";
        internal float At;
        internal List<Method> Methods = new();
    }

    private static readonly List<CacheEntry> Cache = new();

    /// <summary>最近一次真正模拟过的容器；只服务于持续悬停的那一件。</summary>
    private sealed class Recent
    {
        internal GameItem Item = null!;
        internal long Means;
        internal bool MeansValid;
        internal string Locale = "";
        internal float ComputedAt;
        internal float SeenAt;
        internal List<Method> Methods = new();
    }

    private static Recent? _recent;

    // 探针壳按 identifier 复用。DirectoryMaster.Create 实测约 4–5ms/次；
    // 标签深拷贝只有 0.1ms，因此复用壳、重灌标签才是正确优化。
    private static readonly Dictionary<string, GameItem> ProbeShells = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, GameItem> SourceShells = new(StringComparer.Ordinal);

    // 池化壳只在当前场景复用；每次 EmporiumEntry.Start 先销毁旧壳再预热，
    // 不跨场景保留已经被 Unity 销毁的 UI 节点。

    /// <summary>所有化学剂共用的空白溶解壳；每次溶解前重灌标签。</summary>
    private static GameItem? _agentShell;
    /// <summary>空白壳在溶解后被第三方销毁过；本场景内改为逐次克隆。</summary>
    private static bool _agentShellUnusable;

    // ---------------- 模拟入口：同步（测试、API）与悬停分片 ----------------
    //
    // 悬停时 tooltip 每帧重建，缓存未命中的那一帧若一次算完，实测 10–28 ms，超过一帧预算。
    // 悬停路径因此把模拟拆成一串小步骤，每帧最多用 <see cref="SliceBudgetMs"/> 毫秒，算完才写缓存：
    //   - 新成分的水：前几帧没有建议（返回「不可用」），算完后下一帧自然出现，不需要额外刷新。
    //   - 持续悬停同一件容器、成分在变：沿用上一次结果，后台重算完再替换。
    // 步骤边界都在「重灌探针壳 → 跑一次 → 收集结果」之间，每步自包含，任务随时可以丢弃，不留需要清理的状态。
    // 探针壳是共享的：任何同步入口（同步模拟、机器净化预测、预热、清理）开始前一律先丢弃进行中的任务。

    /// <summary>每帧分给水质模拟的毫秒数。</summary>
    private const double SliceBudgetMs = 1.5;
    /// <summary>任务超过这么久没人继续推进（鼠标离开了）就作废，不拿陈旧的输入接着算。</summary>
    private const float JobIdleSeconds = 1f;

    /// <summary>一次模拟读到的全部输入；用它判断「还是不是同一个问题」。</summary>
    private struct Inputs
    {
        internal GameItem Item;
        internal PurifySources.Report Owned;
        internal GameItem? InstalledFilter;
        internal int Purity;
        internal long Signature;
        internal bool SignatureValid;
        internal long Means;
        internal bool MeansValid;
        internal string FilterName;
        internal string Locale;
    }

    private sealed class Job
    {
        internal Inputs In;
        internal readonly List<Method> Result = new();
        internal bool Available;
        internal bool Cacheable;
        internal float LastPumpAt;
        internal IEnumerator<bool>? Steps;
#if ITEMDETAILS_DIAG
        internal double DiagMs;
        internal double DiagMaxSliceMs;
        internal int DiagSlices;
#endif
    }

    private static Job? _job;

    internal static List<Method> SimulateMethods(GameItem? item) => SimulateMethods(item, out _);

    /// <summary>同步算完才返回；测试与第三方调用用这个。</summary>
    internal static List<Method> SimulateMethods(GameItem? item, out bool available)
        => Simulate(item, sliced: false, out available);

    /// <summary>悬停路径：没算完时 <paramref name="available"/> 为 false，调用方按「暂无建议」处理，下一帧再来。</summary>
    internal static List<Method> SimulateMethodsOnHover(GameItem? item, out bool available)
        => Simulate(item, sliced: true, out available);

    private static List<Method> Simulate(GameItem? item, bool sliced, out bool available)
    {
        available = false;
        try
        {
            if (!sliced) _job = null;   // 同步算会重灌同一批探针壳，先丢弃进行中的分片任务
            if (!Prepare(item, out var inp)) return new List<Method>();
            float now = UnityEngine.Time.unscaledTime;

            var recent = _recent;
            bool sameItem = recent != null && ReferenceEquals(recent.Item, inp.Item);
            bool hovering = sameItem && now - recent!.SeenAt <= HoverGapSeconds;
            if (sameItem) recent!.SeenAt = now;

            if (TryCached(inp, now, out var cached))
            {
                available = true;
                return cached;
            }

            // 持续悬停：成分每秒都在变时，至多每 5 秒重算一次，其间沿用上次结果。
            if (hovering && inp.MeansValid && recent!.MeansValid && now - recent.ComputedAt < RecomputeSeconds
                && recent.Means == inp.Means && recent.Locale == inp.Locale)
            {
                available = true;
                return recent.Methods;
            }

            if (!sliced)
            {
                var run = NewJob(inp, now);
                Pump(run, now, long.MaxValue);
                available = run.Available;
                return run.Result;
            }

            var job = _job;
            if (job != null && (now - job.LastPumpAt > JobIdleSeconds || !SameQuestion(job.In, inp)))
                job = _job = null;
            job ??= _job = NewJob(inp, now);

            if (Pump(job, now, SliceBudgetTicks))
            {
                _job = null;
                available = job.Available;
                return job.Result;
            }

            // 还没算完：同一件容器上一次的结果先顶着，没有就暂无建议
            if (hovering && recent!.Locale == inp.Locale)
            {
                available = true;
                return recent.Methods;
            }
            return new List<Method>();
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.SimulateMethods", ex);
            _job = null;
            return new List<Method>();
        }
    }

    private static readonly long SliceBudgetTicks =
        (long)(SliceBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);

    /// <summary>读取这次模拟的输入；不适用（非水容器、酒瓶、读不到纯度）返回 false。</summary>
    private static bool Prepare(GameItem? item, out Inputs inp)
    {
        inp = default;
        if (item == null) return false;
        if (!GameRead.IsTag(item, "LIQUID_CONTAINER_TAG")) return false;
        // 酒瓶也是液体容器，但不能走水质净化链；空瓶/纯非水液体也不必扫描拥有物。
        if (GameRead.IsTag(item, "WINE_CONTAINER_TAG")) return false;

        int purity = SafePurity(item);
        if (purity < 0) return false;

        // 命中判断也必须知道净化手段：已安装滤芯立即取指纹，拥有设备走 2 秒扫描报告。
        var owned = PurifySources.Scan();
        GameItem? installedFilter = null;
        try { installedFilter = WaterHelper.GetFilterItem(item); } catch { }

        inp.Item = item;
        inp.Owned = owned;
        inp.InstalledFilter = installedFilter;
        inp.Purity = purity;
        inp.SignatureValid = TrySimulationSignature(item, owned, installedFilter, out inp.Signature);
        inp.MeansValid = TryMeansSignature(owned, installedFilter, out inp.Means);
        inp.FilterName = GameRead.ItemName(installedFilter);
        inp.Locale = Loc.Code;
        return true;
    }

    private static bool SameQuestion(in Inputs a, in Inputs b)
        => ReferenceEquals(a.Item, b.Item) && a.Purity == b.Purity && a.Signature == b.Signature
           && a.SignatureValid == b.SignatureValid && a.Means == b.Means && a.MeansValid == b.MeansValid
           && a.FilterName == b.FilterName && a.Locale == b.Locale;

    /// <summary>按最近使用顺序从后往前找；过期项顺手清掉。</summary>
    private static bool TryCached(in Inputs inp, float now, out List<Method> methods)
    {
        methods = null!;
        for (int i = Cache.Count - 1; i >= 0; i--)
        {
            var entry = Cache[i];
            if (now - entry.At > CacheSeconds)
            {
                Cache.RemoveAt(i);
                continue;
            }
            if (!inp.SignatureValid || entry.Signature != inp.Signature || entry.Purity != inp.Purity) continue;
            if (entry.FilterName != inp.FilterName || entry.Locale != inp.Locale) continue;

            if (i != Cache.Count - 1)
            {
                Cache.RemoveAt(i);
                Cache.Add(entry);
            }
#if ITEMDETAILS_DIAG
            TrackCache(hit: true, now);
#endif
            methods = entry.Methods;
            return true;
        }
        return false;
    }

    private static Job NewJob(in Inputs inp, float now)
    {
#if ITEMDETAILS_DIAG
        TrackCache(hit: false, now);
        _diagCloneCreateMs = 0;
        _diagCloneTagsMs = 0;
        _diagFeatureMs = 0;
        _diagCloneCount = 0;
#endif
        return new Job { In = inp, LastPumpAt = now };
    }

    /// <summary>
    /// 推进任务直到用完预算或算完。返回 true 表示任务已结束（成功与否看 <see cref="Job.Available"/>）。
    /// 步骤里抛错不再继续这个任务：记一条日志，按「不可用」结束。
    /// </summary>
    private static bool Pump(Job job, float now, long budgetTicks)
    {
        job.LastPumpAt = now;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            job.Steps ??= Steps(job).GetEnumerator();
            bool finished = false;
            while (true)
            {
                if (!job.Steps.MoveNext()) { finished = true; break; }
                if (System.Diagnostics.Stopwatch.GetTimestamp() - start >= budgetTicks) break;
            }
#if ITEMDETAILS_DIAG
            double sliceMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0
                             / System.Diagnostics.Stopwatch.Frequency;
            job.DiagMs += sliceMs;
            job.DiagSlices++;
            if (sliceMs > job.DiagMaxSliceMs) job.DiagMaxSliceMs = sliceMs;
#endif
            if (!finished) return false;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.Pump", ex);
            job.Available = false;
            return true;
        }

        Finish(job, now);
        return true;
    }

    /// <summary>
    /// 任务算完：确认输入没变再写缓存。分片期间容器成分或净化手段变了，这份结果对不上现在的水，直接作废，
    /// 下一帧按新输入重新开始。缓存键不可信（<see cref="Job.Cacheable"/> 为假）时结果只用这一次、不进缓存。
    /// </summary>
    private static void Finish(Job job, float now)
    {
        if (!job.Available) return;

        var inp = job.In;
        if (job.Cacheable && (!Prepare(inp.Item, out var again) || !SameQuestion(inp, again)))
        {
            job.Available = false;
            return;
        }

        job.Result.RemoveAll(m => m.GainPct < 0.01);
        job.Result.Sort((a, b) => b.GainPct.CompareTo(a.GainPct));

#if ITEMDETAILS_DIAG
        Log.Msg($"[perf] 水质模拟 purity={inp.Purity} methods={job.Result.Count} clones={_diagCloneCount} " +
                $"total={job.DiagMs:0.00}ms 分片={job.DiagSlices} 最大单片={job.DiagMaxSliceMs:0.00}ms " +
                $"create={_diagCloneCreateMs:0.00}ms tags={_diagCloneTagsMs:0.00}ms feature={_diagFeatureMs:0.00}ms");
#endif
        if (job.Cacheable)
        {
            Cache.Add(new CacheEntry
            {
                Signature = inp.Signature,
                Purity = inp.Purity,
                FilterName = inp.FilterName,
                Locale = inp.Locale,
                At = now,
                Methods = job.Result,
            });
            if (Cache.Count > MaxEntries) Cache.RemoveAt(0);
        }

        // 持续悬停的沿用与分片期间的顶替都靠它；缓存键不可信时 MeansValid 为假，不会被当作「新鲜」
        _recent = new Recent
        {
            Item = inp.Item,
            Means = inp.Means,
            MeansValid = inp.MeansValid && job.Cacheable,
            Locale = inp.Locale,
            ComputedAt = now,
            SeenAt = now,
            Methods = job.Result,
        };
    }

    /// <summary>
    /// 模拟的全部步骤。每个 <c>yield return</c> 是一个可以停下来的边界；
    /// 中途 <c>yield break</c> 表示这次算不了（<see cref="Job.Available"/> 保持 false）。
    /// 迭代器里不能包 try/catch：异常交给 <see cref="Pump"/> 统一处理，各步骤内部本来就各自兜底。
    /// </summary>
    private static IEnumerable<bool> Steps(Job job)
    {
        var item = job.In.Item;
        var owned = job.In.Owned;
        var installedFilter = job.In.InstalledFilter;
        var result = job.Result;
        var usedNames = new HashSet<string>(StringComparer.Ordinal);

        // 容器探针壳与净化源壳都按 identifier 复用；每次使用前只重灌标签。
        // 首次创建壳很贵，单独占一步，不与后面的模拟叠在同一帧。
        GameItem? sharedProbe = GetShell(ProbeShells, item);
        if (sharedProbe == null) yield break;
        yield return true;

        // 0) 回调走 DissolveInto 但还没试验过的可溶物：先在标准试验水上判定，再并入本次模拟。
        //    判出新结论时报告已失效、签名不含新净化剂，这次结果不进缓存。
        var chemAgents = owned.ChemAgents;
        job.Cacheable = job.In.SignatureValid;
        if (owned.ChemCandidates.Count > 0)
        {
            var trials = new TrialOutcome();
            foreach (bool _ in TrialSteps(sharedProbe, item, owned.ChemCandidates, TrialsPerHover, trials))
                yield return true;
            if (trials.Changed) job.Cacheable = false;
            if (trials.Positives.Count > 0)
                chemAgents = owned.ChemAgents.Concat(trials.Positives).Take(PurifySources.MaxChemAgents).ToList();
        }

        // 1) 机器：黑盒试 PurifyContainer。机器必须走深拷贝——函数内部用
        //    GetTagReadonly 读品质/性能加成，而 TagSystem.GetTag 对缺键会插入 0，
        //    那会把真实机器的加成钉死，净水器从此只能净化到 99.0%（GetPurityMult(0)=1.0）。
        foreach (var machine in owned.Machines)
        {
            GameItem m = machine;
            if (!ResetProbe(sharedProbe, item)) yield break;
            var shell = GetShell(SourceShells, m);
            if (shell == null || !ResetSource(shell, m)) continue;
            RunOnProbe(sharedProbe, m, usedNames, result,
                (probe, _) => ApplyMachineShell(probe, shell));
            yield return true;
        }

        // 2) 便携净水器
        if (owned.Portable != null)
        {
            if (!ResetProbe(sharedProbe, item)) yield break;
            RunOnProbe(sharedProbe, owned.Portable, usedNames, result,
                (probe, _) => WaterHelper.PurifyToBaseWater(probe));
            yield return true;
        }

        // 3) 化学剂：溶入探针后跑游戏自己的反应链（只模拟登记过或试验通过的净化剂）
        foreach (var agent in chemAgents)
        {
            GameItem a = agent;
            if (!ResetProbe(sharedProbe, item)) yield break;
            RunOnProbe(sharedProbe, a, usedNames, result, (probe, _) => Dissolve(a, probe));
            yield return true;
        }

        // 4) 可用的罐内滤芯优先；耗尽时仍列出玩家拥有的替换滤芯。
        if (installedFilter != null && PurifySources.IsUsableFilter(installedFilter))
        {
            var shell = GetShell(SourceShells, installedFilter);
            if (shell == null || !ResetSource(shell, installedFilter)) yield break;
            if (!ResetProbe(sharedProbe, item)) yield break;
            RunFilter(sharedProbe, installedFilter, shell, usedNames, result);
            yield return true;
        }
        else
        {
            foreach (var f in owned.Filters)
            {
                var shell = GetShell(SourceShells, f);
                if (shell == null || !ResetSource(shell, f)) continue;
                if (!ResetProbe(sharedProbe, item)) yield break;
                RunFilter(sharedProbe, f, shell, usedNames, result);
                yield return true;
            }
        }

        job.Available = true;
    }

    /// <summary>
    /// 水质组成指纹：容器类型 + 每种液体的 identifier/parts。
    /// 这只是完整缓存签名的一部分；TrySimulationSignature 还会加入实际净化手段状态。
    /// </summary>
    private static long CompositionSignature(GameItem item)
    {
        const long offset = unchecked((long)14695981039346656037UL);
        const long prime = 1099511628211L;
        unchecked
        {
            long h = offset;

            try
            {
                string id = item.identifier ?? "";
                for (int i = 0; i < id.Length; i++) h = (h ^ id[i]) * prime;
            }
            catch { }

            foreach (var liquid in Liquid.Liquids)
            {
                if (liquid == null) continue;
                string part = liquid.currentPart ?? "";
                int parts = GameRead.TagInt(item, part) ?? 0;
                if (parts <= 0) continue;

                for (int i = 0; i < part.Length; i++) h = (h ^ part[i]) * prime;
                h = (h ^ parts) * prime;
            }
            return h;
        }
    }

    /// <summary>把实际参与本次模拟的设备状态并入液体签名；指纹读取失败时禁用本次缓存。</summary>
    private static bool TrySimulationSignature(GameItem item, PurifySources.Report owned,
        GameItem? installedFilter, out long signature)
    {
        signature = CompositionSignature(item);
        if (!TryMeansSignature(owned, installedFilter, out long means)) return false;
        Mix(ref signature, means);
        return true;
    }

    /// <summary>净化手段部分：拥有的设备与罐内滤芯状态，不含液体组成。</summary>
    private static bool TryMeansSignature(PurifySources.Report owned, GameItem? installedFilter, out long means)
    {
        means = 0;
        if (!owned.SignaturesValid) return false;

        Mix(ref means, owned.CommonSignature);
        return TryFilterSignature(owned, installedFilter, ref means);
    }

    /// <summary>罐内滤芯始终参与签名；不可用时还要包含替换候选的状态。</summary>
    private static bool TryFilterSignature(PurifySources.Report owned,
        GameItem? installedFilter, ref long signature)
    {
        if (installedFilter != null)
        {
            if (!ItemValueFingerprint.TryRead(installedFilter, out long filter)) return false;
            Mix(ref signature, filter);
        }
        if (!PurifySources.IsUsableFilter(installedFilter))
            Mix(ref signature, owned.FiltersSignature);
        return true;
    }

    private static void Mix(ref long hash, long value)
    {
        unchecked { hash = (hash ^ value) * 1099511628211L; }
    }

    /// <summary>按 identifier 取一个可复用壳；首次才 Create。</summary>
    private static GameItem? GetShell(Dictionary<string, GameItem> pool, GameItem source)
    {
        try
        {
            string id = source.identifier ?? "";
            if (id.Length == 0) return null;
            if (pool.TryGetValue(id, out var existing)) return existing;

            var shell = CloneItem(source);
            if (shell == null) return null;
            pool[id] = shell;
            return shell;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.GetShell", ex);
            return null;
        }
    }

    /// <summary>复用净化源壳：只重灌标签/单件价值，不重新 Create。</summary>
    private static bool ResetSource(GameItem shell, GameItem source)
    {
        try
        {
            var cloned = GameRead.DeepCloneTags(source.modifiedState);
            if (cloned == null) return false;

            shell.modifiedState = cloned;
            shell.state = shell.modifiedState;
            shell.unitValue = source.unitValue;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.ResetSource", ex);
            return false;
        }
    }

    private static void ClearShells()
    {
        _job = null;
        foreach (var shell in ProbeShells.Values)
            try { shell.Destroy(); } catch { }
        foreach (var shell in SourceShells.Values)
            try { shell.Destroy(); } catch { }
        if (_agentShell != null) { try { _agentShell.Destroy(); } catch { } }
        ProbeShells.Clear();
        SourceShells.Clear();
        _agentShell = null;
        _agentShellUnusable = false;
    }

    /// <summary>
    /// 预热：只创建当前场景缺失的探针/源壳，不跑净化模拟。
    /// 在存档加载后由 <see cref="WaterWarmupPatch"/> 调用，把工厂创建成本
    /// 挪到加载阶段，避免第一次悬停水质容器时付 60ms 级卡顿。
    /// </summary>
    internal static bool PrewarmOwned()
    {
        _job = null;   // 预热会用探针壳做标准试验
        try
        {
            // 不按显示开关（LiquidGate）短路：玩家可能进入游戏后才切换档位，
            // 先建壳可以让那次开关也直接命中，不必等下一次悬停才付 Create。
            var items = EmporiumEntry.Instance?.GetAllOwnedItems();
            if (items == null || items.Count == 0) return false;

            int probesBefore = ProbeShells.Count;
            int sourcesBefore = SourceShells.Count;
            GameItem? trialContainer = null;
            GameItem? trialProbe = null;

            foreach (var item in items)
            {
                if (item == null) continue;
                if (!GameRead.IsTag(item, "LIQUID_CONTAINER_TAG")) continue;
                if (GameRead.IsTag(item, "WINE_CONTAINER_TAG")) continue;
                if (SafePurity(item) < 0) continue;
                var probe = GetShell(ProbeShells, item);
                if (probe != null && trialProbe == null)
                {
                    trialContainer = item;
                    trialProbe = probe;
                }
            }

            var owned = PurifySources.Scan();
            foreach (var machine in owned.Machines) GetShell(SourceShells, machine);
            foreach (var filter in owned.Filters) GetShell(SourceShells, filter);
            if (owned.ChemAgents.Count > 0 || owned.ChemCandidates.Count > 0) GetAgentShell();

            // 第三方可溶物的标准试验也挪到加载阶段；没有水容器时等第一次悬停再做。
            int candidates = owned.ChemCandidates.Count;
            int purifiers = 0;
            if (trialProbe != null && trialContainer != null && candidates > 0)
                purifiers = RunTrials(trialProbe, trialContainer, owned.ChemCandidates, TrialsOnPrewarm, out _).Count;

            Log.Msg($"WaterAdvice prewarm: probes +{ProbeShells.Count - probesBefore} " +
                    $"sources +{SourceShells.Count - sourcesBefore} chem trials {candidates} -> {purifiers}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.Prewarm", ex);
            return true;   // 失败也不要每 0.5 秒重试刷屏
        }
    }

    internal static void Stop()
    {
        _job = null;
        Cache.Clear();
        _recent = null;
        ClearShells();
#if ITEMDETAILS_DIAG
        _diagHits = 0;
        _diagMisses = 0;
        _diagAt = 0f;
#endif
        PurifySources.Reset();
    }

#if ITEMDETAILS_DIAG
    private static int _diagHits;
    private static int _diagMisses;
    private static float _diagAt;
    private static double _diagCloneCreateMs;
    private static double _diagCloneTagsMs;
    private static double _diagFeatureMs;
    private static int _diagCloneCount;

    /// <summary>
    /// 每 2 秒汇总一次缓存命中/重算。重算次数才是水质建议的真实成本来源：
    /// 在几瓶水之间来回划时，单槽缓存会 1:1 交替 miss。
    /// </summary>
    private static void TrackCache(bool hit, float now)
    {
        if (hit) _diagHits++;
        else _diagMisses++;
        if (_diagAt <= 0f) _diagAt = now + 2f;
        if (now < _diagAt) return;

        Log.Msg($"[perf] 水质建议缓存（2秒）: 命中={_diagHits} 重算={_diagMisses} 槽位={Cache.Count}");
        _diagHits = 0;
        _diagMisses = 0;
        _diagAt = now + 2f;
    }
#endif

    private delegate void PurifyAction(GameItem probe, GameItem source);

    /// <summary>
    /// 在探针容器上跑一次机器净化。机器同样深拷贝：<c>MachinePurifier.PurifyContainer</c>
    /// 会通过 <c>GetTagReadonly</c> 读 <c>TOTAL_PERCENTAGE_*_BONUS_INT</c>，而
    /// <c>TagSystem.GetTag</c> 缺键即插入 0 —— 直接传真实机器会把它的加成永久钉成 0，
    /// 表现为净水器只能净化到 99.0%。
    /// </summary>
    /// <summary>安全入口：直接传真实机器时先克隆，绝不改真实物品。</summary>
    internal static void ApplyMachine(GameItem probe, GameItem machine)
    {
        GameItem? clone = null;
        try
        {
            clone = CloneItem(machine);
            if (clone == null) return;
            MachinePurifier.PurifyContainer(clone, probe);
        }
        catch (Exception ex) { Log.Warn("WaterAdvice.ApplyMachine", ex); }
        finally
        {
            if (clone != null) { try { clone.Destroy(); } catch { } }
        }
    }

    /// <summary>
    /// 预测某台机器今晚能从指定容器中净化的量与净化后的纯度。
    /// 复用既有池化探针，每次重灌真实机器与容器的深拷贝标签，
    /// 防止原版函数插入缺省标签或改写液体时修改玩家的物品。
    /// 失败返回 (-1, 0)；池中的副本随场景切换/Mod 停用销毁。
    /// </summary>
    internal static (int RemovedParts, int PurityAfter) SimulateMachineRemoval(GameItem machine, GameItem container)
    {
        _job = null;   // 共用探针壳：进行中的分片任务让路
        try
        {
            var waterProbe = GetShell(ProbeShells, container);
            var machineShell = GetShell(SourceShells, machine);
            if (waterProbe == null || machineShell == null
                || !ResetProbe(waterProbe, container) || !ResetSource(machineShell, machine))
                return (-1, 0);

            int removed = MachinePurifier.PurifyContainer(machineShell, waterProbe);
            int purityAfter = SafePurity(waterProbe);
            return purityAfter >= 0 ? (removed, purityAfter) : (-1, 0);
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.SimulateMachineRemoval", ex);
            return (-1, 0);
        }
    }

    /// <summary>池化路径：调用方已经拿到隔离壳并重置过标签。</summary>
    private static void ApplyMachineShell(GameItem probe, GameItem machineShell)
    {
        try { MachinePurifier.PurifyContainer(machineShell, probe); }
        catch (Exception ex) { Log.Warn("WaterAdvice.ApplyMachineShell", ex); }
    }

    /// <summary>
    /// 化学剂溶入探针后跑游戏自己的反应链。
    /// DissolveInto 末尾会 Destroy 化学剂：有 <see cref="ProbeDestroyGuard"/> 时用共享空白壳并挡住这次销毁；
    /// 没有补丁或壳被第三方销毁过，退回每次克隆一份、用完清理。
    /// </summary>
    private static void Dissolve(GameItem agent, GameItem probe)
    {
        var shell = GetAgentShell();
        if (shell != null && ResetSource(shell, agent))
        {
            bool intact = ProbeDestroyGuard.Run(shell, () => ChemicalHelper.DissolveInto(shell, probe));
            if (!intact)
            {
                // 第三方在 Destroy 前缀里自己处理掉了这个壳：本场景不再池化
                _agentShell = null;
                _agentShellUnusable = true;
                Log.Warn("WaterAdvice.ChemShell", "化学剂空白壳在溶解后不可用，改为逐次克隆");
            }
            ChemicalHelper.HandleTemperatureReaction(probe);
            return;
        }

        var dissolved = CloneItem(agent);
        if (dissolved == null) return;
        try
        {
            ChemicalHelper.DissolveInto(dissolved, probe);
            ChemicalHelper.HandleTemperatureReaction(probe);
        }
        finally { try { dissolved.Destroy(); } catch { } }
    }

    /// <summary>
    /// 化学剂共用的空白壳：DissolveInto 只读化学剂的液体标签，空白物品重灌标签后即可代替任何化学剂。
    /// 守卫不可用或壳已被第三方销毁时返回 null，由调用方逐次克隆。
    /// </summary>
    private static GameItem? GetAgentShell()
    {
        if (!ProbeDestroyGuard.Active || _agentShellUnusable) return null;
        if (_agentShell != null && !ProbeDestroyGuard.IsDestroyed(_agentShell)) return _agentShell;
        try
        {
#if ITEMDETAILS_DIAG
            var diagCreate = System.Diagnostics.Stopwatch.StartNew();
#endif
            _agentShell = ItemDirectory.CreateEmptyItem();
#if ITEMDETAILS_DIAG
            diagCreate.Stop();
            _diagCloneCreateMs += diagCreate.Elapsed.TotalMilliseconds;
#endif
            return _agentShell;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.AgentShell", ex);
            _agentShell = null;
            _agentShellUnusable = true;
            return null;
        }
    }

    /// <summary>
    /// 对还没下结论的可溶物各做一次标准试验，结论写回 <see cref="PurifySources"/>，返回试验通过的物品。
    /// 回调走 DissolveInto 只说明它能溶；营养片、粪便、药物也都能溶，纯度上升才算净化剂。
    /// 试验跑不成（探针重置失败）不下结论，下次再试。<paramref name="changed"/> 表示本次判出了新结论。
    /// </summary>
    private static List<GameItem> RunTrials(GameItem probe, GameItem container, List<GameItem> candidates,
        int budget, out bool changed)
    {
        var outcome = new TrialOutcome();
        foreach (bool _ in TrialSteps(probe, container, candidates, budget, outcome)) { }
        changed = outcome.Changed;
        return outcome.Positives;
    }

    private sealed class TrialOutcome
    {
        internal readonly List<GameItem> Positives = new();
        internal bool Changed;
    }

    /// <summary>逐个试验的迭代器：每试完一个让出一次，悬停路径据此分片；同步调用方直接跑完。</summary>
    private static IEnumerable<bool> TrialSteps(GameItem probe, GameItem container, List<GameItem> candidates,
        int budget, TrialOutcome outcome)
    {
        foreach (var candidate in candidates)
        {
            string id = candidate.identifier ?? "";
            if (id.Length == 0 || PurifySources.IsJudged(id)) continue;
            if (budget-- <= 0) break;

            bool? purifies = TrialPurifies(probe, container, candidate);
            if (purifies != null)
            {
                PurifySources.JudgeChem(id, purifies.Value);
                outcome.Changed = true;
                if (purifies.Value) outcome.Positives.Add(candidate);
            }
            yield return true;
        }
    }

    /// <summary>标准试验：探针换成固定的试验水，溶入一份化学剂，纯度上升即为净化剂。</summary>
    private static bool? TrialPurifies(GameItem probe, GameItem container, GameItem agent)
    {
        if (!ResetProbe(probe, container) || !FillTrialWater(probe)) return null;
        int before = SafePurity(probe);
        if (before < 0) return null;
        try { Dissolve(agent, probe); }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.Trial." + agent.identifier, ex);
            return false;
        }
        return SafePurity(probe) > before;
    }

    /// <summary>
    /// 标准试验水：容器容量的一半（最多 1L）、纯度 60%，杂质按原版沟渠水的分布配。
    /// 不用 WaterHelper.AddWater：它会掷随机数，且按容器剩余容量灌满。
    /// 氯先分解化学污染物、再杀微生物，这份水两样都有，一片净水片（15ml 氯）就能明显提纯。
    /// </summary>
    private static bool FillTrialWater(GameItem probe)
    {
        try
        {
            int capacity = GameRead.TagInt(probe, "LIQUID_CONTAINER_CAPACITY") ?? 0;
            int total = Math.Min(capacity / 2, 1_000_000);
            if (total <= 0) return false;

            foreach (var liquid in Liquid.Liquids)
            {
                if (liquid == null) continue;
                string part = liquid.currentPart;
                if ((GameRead.TagInt(probe, part) ?? 0) != 0)
                    probe.ModifyTag(part, st => st.SetInt(0), skipValidate: true);
            }

            int dirt = total * 4 / 10;
            int added = 0;
            foreach (var share in WaterPremadeHelper.gutterflowContaminantDistribution)
            {
                int amount = (int)(dirt * share.Value);
                if (amount <= 0) continue;
                probe.ModifyTag(share.Key, st => st.Enable().SetInt(amount), skipValidate: true);
                added += amount;
            }
            if (added <= 0) return false;
            probe.ModifyTag(Liquid.GetCurrentPartFromId("water"), st => st.Enable().SetInt(total - dirt), skipValidate: true);

            try { probe.itemFeatures.RemoveAll(f => f != null && f.category == "CATEGORY_WATER_PURITY"); }
            catch { }
            WaterFeatureHelper.InitWaterFeature(probe);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.TrialWater", ex);
            return false;
        }
    }

    /// <summary>在已重置的共享探针上跑一次手段，比较前后纯度。</summary>
    private static void RunOnProbe(GameItem probe, GameItem source,
        HashSet<string> usedNames, List<Method> result, PurifyAction action)
    {
        try
        {
            int before = SafePurity(probe);
            if (before < 0) return;

            action(probe, source);

            AddMethod(result, usedNames, GameRead.ItemDisplayName(source), before, SafePurity(probe));
        }
        catch (Exception ex) { Log.Warn("WaterAdvice.Simulate", ex); }
    }

    /// <summary>滤芯：共享容器探针 + 滤芯深拷贝，逐液体走原版 HandleFilter。</summary>
    private static void RunFilter(GameItem probe, GameItem filterSource, GameItem filterShell,
        HashSet<string> usedNames, List<Method> result)
    {
        try
        {
            int before = SafePurity(probe);
            if (before < 0) return;

            string waterTag = Liquid.GetCurrentPartFromId("water");
            foreach (var liquid in Liquid.Liquids)
            {
                if (liquid == null) continue;
                string part = liquid.currentPart;
                int amount = GameRead.TagInt(probe, part) ?? 0;
                if (amount <= 0) continue;

                int remaining = WaterHelper.HandleFilter(liquid.identifier, amount, probe, filterShell);
                int removed = amount - remaining;
                if (removed <= 0) continue;

                probe.ModifyTag(part, st => st.Enable().SetInt(remaining));
                int waterNow = GameRead.TagInt(probe, waterTag) ?? 0;
                probe.ModifyTag(waterTag, st => st.Enable().SetInt(waterNow + removed));
            }

            AddMethod(result, usedNames, GameRead.ItemDisplayName(filterSource), before, SafePurity(probe));
        }
        catch (Exception ex) { Log.Warn("WaterAdvice.Filter", ex); }
    }

    private static void AddMethod(List<Method> list, HashSet<string> usedNames,
        string name, int before, int after)
    {
        if (before < 0 || after <= before) return;

        // 名称取不到就不列：不编造占位名
        if (string.IsNullOrEmpty(name)) return;

        // 同一效果不重复列（例如容器里装的就是 UV 滤芯，可能同时被滤芯与化学通道覆盖）
        if (!usedNames.Add(name)) return;

        list.Add(new Method { Name = name, GainPct = (after - before) / 100.0 });
    }

    /// <summary>
    /// 复用探针壳：重灌容器的标签/单件价值，移除上一轮的水质特性并重新初始化。
    /// 只重建 TagState 和水质特性，不重新 DirectoryMaster.Create。
    /// </summary>
    private static bool ResetProbe(GameItem probe, GameItem item)
    {
        try
        {
            var cloned = GameRead.DeepCloneTags(item.modifiedState);
            if (cloned == null) return false;

            probe.modifiedState = cloned;
            probe.state = probe.modifiedState;
            probe.unitValue = item.unitValue;

            try
            {
                probe.itemFeatures.RemoveAll(f => f != null && f.category == "CATEGORY_WATER_PURITY");
            }
            catch { }

#if ITEMDETAILS_DIAG
            var diagFeature = System.Diagnostics.Stopwatch.StartNew();
#endif
            try { WaterFeatureHelper.InitWaterFeature(probe); }
            catch (Exception ex) { Log.Warn("WaterAdvice.InitFeature", ex); }
#if ITEMDETAILS_DIAG
            diagFeature.Stop();
            _diagFeatureMs += diagFeature.Elapsed.TotalMilliseconds;
#endif
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.ResetProbe", ex);
            return false;
        }
    }

    /// <summary>按 identifier 造副本并深拷贝全部标签/单件价值；失败返回 null。</summary>
    private static GameItem? CloneItem(GameItem source)
    {
        GameItem? copy = null;
        try
        {
            string id = source.identifier;
            if (string.IsNullOrEmpty(id)) return null;

#if ITEMDETAILS_DIAG
            var diagCreate = System.Diagnostics.Stopwatch.StartNew();
#endif
            copy = DirectoryMaster.Create<GameItem>(id);
#if ITEMDETAILS_DIAG
            diagCreate.Stop();
            _diagCloneCreateMs += diagCreate.Elapsed.TotalMilliseconds;
#endif
            if (copy == null) return null;

#if ITEMDETAILS_DIAG
            var diagTags = System.Diagnostics.Stopwatch.StartNew();
#endif
            var cloned = GameRead.DeepCloneTags(source.modifiedState);
#if ITEMDETAILS_DIAG
            diagTags.Stop();
            _diagCloneTagsMs += diagTags.Elapsed.TotalMilliseconds;
#endif
            if (cloned == null) { Log.Warn("WaterAdvice.Clone", "标签克隆失败，跳过"); copy.Destroy(); return null; }

            copy.modifiedState = cloned;
            copy.state = copy.modifiedState;
            copy.unitValue = source.unitValue;
#if ITEMDETAILS_DIAG
            _diagCloneCount++;
#endif
            return copy;
        }
        catch (Exception ex)
        {
            Log.Warn("WaterAdvice.CloneItem", ex);
            if (copy != null) { try { copy.Destroy(); } catch { } }
            return null;
        }
    }

    internal static int SafePurity(GameItem item)
    {
        try
        {
            // 不调 WaterHelper.GetWaterPurity：它用 GetTagReadonly 遍历所有液体，
            // 会给真实物品插入缺失标签。这里与 WaterInfo 一样只 peek 已有键。
            long water = 0;
            long total = 0;
            foreach (var liquid in Liquid.Liquids)
            {
                if (liquid == null) continue;
                int parts = GameRead.TagInt(item, liquid.currentPart) ?? 0;
                if (parts <= 0) continue;
                total += parts;
                if (liquid.identifier == "water") water += parts;
            }
            if (water <= 0 || total <= 0) return -1;
            return (int)(water * WaterInfo.PurityFull / total);
        }
        catch { return -1; }
    }
}

/// <summary>
/// 在 EmporiumEntry 完成场景初始化后重建并预热水质探针（只建壳，不模拟）。
/// 池化壳不跨场景复用，避免持有已被 Unity 销毁的 UI 节点。
/// 用 Start postfix 而不是自建 MonoBehaviour：模组创建的对象在主菜单/场景切换中
/// 不保证拿到 Update，而 EmporiumEntry.Start 是存档场景确定会走到的时机。
/// </summary>
internal static class WaterWarmupPatch
{

    internal static void Install(PatchScope scope)
    {
        try
        {
            var start = AccessTools.Method(typeof(EmporiumEntry), "Start", Type.EmptyTypes);
            if (start == null)
            {
                Log.Warn("WaterWarmup.Apply", "未找到 EmporiumEntry.Start，跳过预热");
                return;
            }
            scope.Patch(start,
                postfix: new HarmonyMethod(typeof(WaterWarmupPatch), nameof(AfterStart)));
            Log.Msg("WaterWarmup: patched EmporiumEntry.Start");
        }
        catch (Exception ex)
        {
            Log.Error("WaterWarmup.Apply", ex);
        }
    }

    private static void AfterStart()
    {
        // 场景里的 UI 节点已经更换：旧壳不能继续池化，先清理再按当前拥有物预热。
        try { WaterAdvice.Stop(); }
        catch (Exception ex) { Log.Warn("WaterWarmup.Reset", ex); }
        // 预热只为随后真的要画的水质表服务：液体组分分析关掉、或选了「有扫描器」而手上没有，
        // 就没必要建探针。拥有关系走 ScannerInventory 的 TTL 缓存，这里不会每次遍历容器。
        if (!LiquidGate.Enabled) return;
        try { WaterAdvice.PrewarmOwned(); }
        catch (Exception ex) { Log.Warn("WaterWarmup.AfterStart", ex); }
    }
}
