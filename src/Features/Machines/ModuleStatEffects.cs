using System;
using System.Collections.Generic;
using System.Globalization;

namespace ItemDetails;

/// <summary>
/// 机器「模组效果」三项加成的实际影响：在探针上用游戏自己的函数各算一次
/// 「当前加成」与「这一项清零」，差值就是这一项的贡献。
///
/// 为什么用差分而不是照抄公式：
///   · 性能、效率、质量在每台机器上含义不同（集水器是出水量、投影仪是吸引力、净水器是残留下限……），
///     数值全交给游戏函数算，其它模组用 Harmony 改了这些函数，显示自动跟上；
///   · 两项都影响耗电的机器（交易机的性能也省电、催陈器的性能抵扣耗电）能拆开各算各的；
///   · 分档的质量能向上试探出「再加多少升一档」。
///
/// 耗电和加工速度先做模型校验：探针按原版公式算出的值必须等于机器上的实际值，
/// 对不上（涡轮增压、第三方改了耗电等）就不给这两项，宁可少写也不写错。
///
/// 原版机器在 Profiles 表中登记「调哪个游戏函数、写成什么单位」，未登记机器不做推算。
/// 第三方机器通过 ItemDetailsApi 显式注入自己的注释。
/// 蒸发器、熔炉的公式写死在每晚回调里，没有可调用的函数，不做质量注释。
/// </summary>
internal static class ModuleStatEffects
{
    internal const char Performance = 'p';
    internal const char Efficiency = 'e';
    internal const char Quality = 'q';

    private const string PerfTag = "TOTAL_PERCENTAGE_PERFORMANCE_BONUS_INT";
    private const string PowerTag = "TOTAL_PERCENTAGE_POWER_BONUS_INT";
    private const string QualityTag = "TOTAL_PERCENTAGE_QUALITY_BONUS_INT";
    private const string DrawnTag = "machinery_drawn_power";
    private const string SpeedTag = "CURRENT_PROCESSING_SPEED_TAG";

    /// <summary>向上试探下一档的最大加成增量（百分点）。</summary>
    private const int StepSearchLimit = 1000;
    /// <summary>
    /// 缓存键已覆盖全部会变的输入（三项加成、耗电、速度、进度、状态、语言），值不变结果就不变；
    /// TTL 只兜底键外的变化，不能短到悬停时周期性重算。
    /// </summary>
    private const float CacheSeconds = 30f;
    private const int MaxEntries = 32;

    private static GameItem? _probe;

    private sealed class Entry
    {
        internal GameItem Machine = null!;
        internal string Key = "";
        internal float At;
        internal readonly Dictionary<char, string> Notes = new();
    }

    private static readonly List<Entry> Entries = new();

    internal static void Stop()
    {
        Entries.Clear();
        var probe = _probe;
        _probe = null;
        if (probe != null) { try { probe.Destroy(); } catch { } }
    }

    /// <summary>某一项加成的注释（不含括号）；没有可靠结论返回空串。</summary>
    internal static string Note(char stat, GameItem machine)
    {
        try
        {
            // 显式第三方提供方优先且不走原版缓存；提供方完全拥有这个 ID 的注释语义。
            if (ThirdPartyProviders.TryMachineNote(machine, stat, out string injected)) return injected;

            string key = CacheKey(machine);
            float now = UnityEngine.Time.unscaledTime;
            var entry = Entries.Find(e => ReferenceEquals(e.Machine, machine));
            if (entry == null || entry.Key != key || now - entry.At > CacheSeconds)
            {
                if (entry == null)
                {
                    entry = new Entry { Machine = machine };
                    Entries.Add(entry);
                    while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
                }
                entry.Key = key;
                entry.At = now;
                entry.Notes.Clear();
                Build(machine, entry.Notes);
            }
            return entry.Notes.TryGetValue(stat, out var note) ? note : "";
        }
        catch (Exception ex)
        {
            Log.Warn("ModuleStatEffects", ex);
            return "";
        }
    }

    private static string CacheKey(GameItem machine)
        => string.Join("|", Loc.Code,
            GameRead.TagInt(machine, PerfTag), GameRead.TagInt(machine, PowerTag),
            GameRead.TagInt(machine, QualityTag), GameRead.TagInt(machine, DrawnTag),
            GameRead.TagInt(machine, SpeedTag), GameRead.TagInt(machine, "MACHINE_PROGRESS_CURRENT_TAG"),
            GameRead.TagString(machine, "MACHINE_STATE_TAG"));

    // ---------------- 原版机器能力表 ----------------

    private sealed class MachineEffectProfile
    {
        internal Func<GameItem, int>? Performance;
        internal string PerformanceKey = "";
        internal string PerformanceEn = "";
        internal int PerformanceDivisor = 1;
        internal Func<GameItem, int>? Quality;
        internal string QualityKey = "";
        internal string QualityEn = "";
        internal bool QualityNextStep;
        internal Func<GameItem, string>? QualityTier;
        internal Func<GameItem, string?>? CustomQuality;
        internal bool UseDealMakerPower;
    }

    /// <summary>
    /// 原版机器的唯一语义登记处。游戏数值或函数变化时改表，不为兼容而猜测未知机器。
    /// 空表项仍可使用原版共同的耗电/速度模型；熔炉、拆解机没有可调用的质量函数，故不登记质量。
    /// </summary>
    private static readonly Dictionary<string, MachineEffectProfile> Profiles = new(StringComparer.Ordinal)
    {
        ["furnace"] = new(),
        ["desequencer"] = new(),
        ["moisture_farm"] = new()
        {
            Performance = MachineMoistureFarm.GetOutputVolume,
            PerformanceKey = "module.output", PerformanceEn = "{0} ml/day", PerformanceDivisor = 1000,
            QualityTier = MachineMoistureFarm.GetOutputQuality,
        },
        ["mirage_projector"] = new()
        {
            Performance = MachineProjector.GetAttractivenessBoost,
            PerformanceKey = "module.attract", PerformanceEn = "{0}% attractiveness",
            Quality = MachineProjector.GetAttractivenessBoost,
            QualityKey = "module.attract", QualityEn = "{0}% attractiveness",
        },
        ["deal_maker"] = new()
        {
            Quality = MachineDealMaker.GetChanceIncrease,
            QualityKey = "module.bargain", QualityEn = "{0}% bargain chance",
            UseDealMakerPower = true,
        },
        ["security_alarm"] = new()
        {
            Performance = p => MachineAlarm.GetAlarmeStopRate(p),
            PerformanceKey = "module.theft", PerformanceEn = "{0}% theft prevention",
        },
        ["wine_rack"] = new()
        {
            Quality = MachineWineRack.GetAgingDays,
            QualityKey = "module.aging", QualityEn = "{0} day(s) of aging per day", QualityNextStep = true,
        },
        ["water_purifier"] = new() { CustomQuality = PurifierResidual },
        ["printer"] = new()
        {
            Quality = ModuleHelper.GetAjustedQuality,
            QualityKey = "module.print_quality", QualityEn = "{0} print quality",
        },
        ["plant_dna_manipulator"] = new()
        {
            Quality = ModuleHelper.GetAjustedQuality,
            QualityKey = "module.success", QualityEn = "{0}% success chance",
        },
        ["hydroponic"] = new()
        {
            Quality = ModuleHelper.GetAjustedQuality,
            QualityKey = "module.harvest", QualityEn = "{0}% harvest",
        },
    };

    // ---------------- 组装 ----------------

    /// <summary>
    /// 每种加成状态只建一次探针：当前加成 1 次，性能/效率/质量各清零 1 次。
    /// 同一状态下的只读函数与耗电模型共用这一次拷贝，不再每个差分各拷两次。
    /// </summary>
    private static void Build(GameItem machine, Dictionary<char, string> notes)
    {
        if (!Profiles.TryGetValue(machine.identifier ?? "", out var profile)) return;

        var power = PowerModel(machine, profile);
        var now = Take(machine, profile, power);
        if (now == null) return;
        if (power != null && !Reproduces(machine, now)) power = null;

        int perfBonus = GameRead.TagInt(machine, PerfTag) ?? 0;
        int powerBonus = GameRead.TagInt(machine, PowerTag) ?? 0;
        int qualityBonus = GameRead.TagInt(machine, QualityTag) ?? 0;

        // 效率：只算效率自己省下的电。
        if (power != null && powerBonus != 0
            && Diff(now.Drawn, Take(machine, profile, power, (PowerTag, 0))?.Drawn) is int ep)
            notes[Efficiency] = Loc.T("module.power", "{0} power/day", Signed(ep));

        var perf = new List<string>();
        var noPerf = perfBonus != 0 && (power != null || profile.Performance != null)
            ? Take(machine, profile, power, (PerfTag, 0))
            : null;
        if (power != null && noPerf != null)
        {
            if (Diff(now.Speed, noPerf.Speed) is int sp && sp != 0)
            {
                string text = Loc.T("module.speed", "{0} speed", Plus(sp));
                int faster = DaysFaster(machine, noPerf.Speed ?? 0);
                if (faster > 0) text += Loc.T("module.days_faster", ", about {0} day(s) faster", faster);
                perf.Add(text);
            }
            // 交易机、催陈器的性能也会降低耗电
            if (Diff(now.Drawn, noPerf.Drawn) is int pp && pp != 0)
                perf.Add(Loc.T("module.power", "{0} power/day", Signed(pp)));
        }

        if (profile.Performance != null && noPerf != null
            && Diff(now.Performance, noPerf.Performance) is int delta && delta != 0)
            perf.Add(Loc.T(profile.PerformanceKey, profile.PerformanceEn,
                Plus(delta / profile.PerformanceDivisor)));

        string? quality = null;
        if (profile.CustomQuality != null)
        {
            quality = profile.CustomQuality(machine);
        }
        else if (profile.QualityTier != null)
        {
            quality = NextTier(machine, QualityTag, profile.QualityTier);
        }
        else if (profile.Quality != null && qualityBonus != 0
                 && Diff(now.Quality, Take(machine, profile, null, (QualityTag, 0))?.Quality) is int qualityDelta)
        {
            quality = Loc.T(profile.QualityKey, profile.QualityEn, Plus(qualityDelta));
            if (profile.QualityNextStep)
                quality = WithNextStep(quality, machine, QualityTag, profile.Quality);
        }

        if (perf.Count > 0) notes[Performance] = string.Join(Loc.T("list.sep", ", "), perf);
        if (!string.IsNullOrEmpty(quality)) notes[Quality] = quality!;
    }

    private static int? Diff(int? now, int? zero)
        => now == null || zero == null ? null : now.Value - zero.Value;

    // ---------------- 耗电 / 速度模型 ----------------

    /// <summary>这台机器把加成换算成耗电与速度用的游戏函数；能否采用由 <see cref="Reproduces"/> 校验。</summary>
    private static Action<GameItem>? PowerModel(GameItem machine, MachineEffectProfile profile)
    {
        if (GameRead.TagInt(machine, DrawnTag) == null) return null;
        if (profile.UseDealMakerPower) return MachineDealMaker.UpdatePowerUse;
        if (GameRead.IsTag(machine, "SYSTEM_TAG")) return ModuleHelper.ApplyModifiedPower;
        return p => ModuleHelper.ApplyBasicModuleEffect(null!, null!, p);
    }

    /// <summary>
    /// 探针在当前加成下必须复现机器上的实际值，否则说明有别的东西改过
    /// （涡轮增压、第三方公式），不给耗电和速度。
    /// </summary>
    private static bool Reproduces(GameItem machine, Sample now)
    {
        if (now.Drawn == null || now.Drawn != GameRead.TagInt(machine, DrawnTag)) return false;

        // 有加工速度的机器，速度也要能复现；系统类模型不算速度，跳过
        int baseSpeed = GameRead.TagInt(machine, "BASE_PROCESSING_SPEED_TAG") ?? 0;
        if (baseSpeed > 0 && !GameRead.IsTag(machine, "SYSTEM_TAG")
            && now.Speed != GameRead.TagInt(machine, SpeedTag))
            return false;
        return true;
    }

    /// <summary>正在加工的进度型机器：去掉性能后要多花几天。<paramref name="speed0"/> 是性能清零后的速度。</summary>
    private static int DaysFaster(GameItem machine, int speed0)
    {
        if (speed0 <= 0) return 0;
        if (GameRead.TagString(machine, "MACHINE_STATE_TAG") != "STATE_WORKING") return 0;
        int cur = GameRead.TagInt(machine, "MACHINE_PROGRESS_CURRENT_TAG") ?? 0;
        int target = GameRead.TagInt(machine, "MACHINE_PROGRESS_TARGET_TAG") ?? 0;
        int speed = GameRead.TagInt(machine, SpeedTag) ?? 0;
        if (target <= cur || speed <= 0) return 0;

        int left = target - cur;
        return Ceil(left, speed0) - Ceil(left, speed);
    }

    private static int Ceil(int a, int b) => (a + b - 1) / b;

    // ---------------- 采样 ----------------

    /// <summary>一种加成状态下的读数；读不到的项为 null。</summary>
    private sealed class Sample
    {
        internal int? Performance;
        internal int? Quality;
        internal int? Drawn;
        internal int? Speed;
    }

    /// <summary>
    /// 按 overrides 建一次探针：先调只读的性能/质量函数，再跑耗电模型（会改探针）读出耗电和速度。
    /// 顺序不能反，否则只读函数会读到模型写过的标签。
    /// </summary>
    private static Sample? Take(GameItem machine, MachineEffectProfile profile, Action<GameItem>? model,
                                params (string Tag, int Value)[] overrides)
    {
        var probe = Probe(machine, overrides);
        if (probe == null) return null;

        var s = new Sample();
        if (profile.Performance != null) { try { s.Performance = profile.Performance(probe); } catch { } }
        if (profile.Quality != null) { try { s.Quality = profile.Quality(probe); } catch { } }
        if (model != null)
        {
            try
            {
                model(probe);
                s.Drawn = GameRead.TagInt(probe, DrawnTag);
                s.Speed = GameRead.TagInt(probe, SpeedTag);
            }
            catch { }
        }
        return s;
    }

    /// <summary>分档结果：当前档位下，再加多少百分点会进下一档；已到顶或找不到返回 -1。</summary>
    private static int NextStep<T>(GameItem machine, string statTag, Func<GameItem, T> f)
    {
        int cur = GameRead.TagInt(machine, statTag) ?? 0;
        // f 只读：整个二分共用一次拷贝，每步只改这一项加成
        var probe = Probe(machine);
        if (probe == null || !probe.modifiedState.dict.TryGetValue(statTag, out var slot) || slot == null)
            throw new InvalidOperationException("probe");
        T Eval(int bonus)
        {
            slot.SetInt(bonus);
            return f(probe);
        }

        var now = Eval(cur);
        if (EqualityComparer<T>.Default.Equals(Eval(cur + StepSearchLimit), now)) return -1;

        // 分档函数单调：找最小的 d 使结果变化
        int lo = 1, hi = StepSearchLimit;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (EqualityComparer<T>.Default.Equals(Eval(cur + mid), now)) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static string? NextTier(GameItem machine, string statTag, Func<GameItem, string> f)
    {
        try
        {
            int step = NextStep(machine, statTag, f);
            return step > 0
                ? Loc.T("module.next_tier", "+{0}% for next tier", step)
                : Loc.T("module.top_tier", "top tier");
        }
        catch { return null; }
    }

    private static string WithNextStep(string text, GameItem machine, string statTag, Func<GameItem, int> f)
    {
        try
        {
            int step = NextStep(machine, statTag, f);
            if (step > 0) text += Loc.T("list.sep", ", ") + Loc.T("module.next_tier", "+{0}% for next tier", step);
        }
        catch { }
        return text;
    }

    private static string? PurifierResidual(GameItem machine)
    {
        try
        {
            int q = GameRead.TagInt(machine, QualityTag) ?? 0;
            double now = MachinePurifier.GetPurityMult(q);
            string text = Loc.T("module.residual", "residual ×{0}", now.ToString("0.##", CultureInfo.InvariantCulture));

            // 纯函数，直接按质量值试探，不需要探针
            if (MachinePurifier.GetPurityMult(q + StepSearchLimit) == now) return text;
            int lo = 1, hi = StepSearchLimit;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (MachinePurifier.GetPurityMult(q + mid) == now) lo = mid + 1;
                else hi = mid;
            }
            return text + Loc.T("list.sep", ", ") + Loc.T("module.next_tier", "+{0}% for next tier", lo);
        }
        catch { return null; }
    }

    // ---------------- 探针 ----------------

    /// <summary>
    /// 复用同一个空物品壳，每次重新深拷贝机器的标签再改写指定加成。
    /// 游戏函数在探针上调用 ModifyTag 只改探针，不碰真实机器。
    /// </summary>
    private static GameItem? Probe(GameItem machine, params (string Tag, int Value)[] overrides)
    {
        try
        {
            var tags = GameRead.DeepCloneTags(machine.modifiedState);
            if (tags == null) return null;
            foreach (var (tag, value) in overrides)
            {
                if (!tags.dict.TryGetValue(tag, out var state) || state == null) return null;
                state.SetInt(value);
            }

            _probe ??= ItemDirectory.CreateEmptyItem("itemdetails_stat_probe");
            _probe.modifiedState = tags;
            _probe.state = tags;
            return _probe;
        }
        catch (Exception ex)
        {
            Log.Warn("ModuleStatEffects.Probe", ex);
            return null;
        }
    }

    /// <summary>耗电：零变化也写成负数（-0），和玩家「效率+16% 应该减耗」的直觉一致。</summary>
    private static string Signed(int delta) => delta <= 0 ? "-" + (-delta) : "+" + delta;

    /// <summary>其它收益：零写成 +0。</summary>
    private static string Plus(int delta) => delta >= 0 ? "+" + delta : delta.ToString();
}
