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
    private const float CacheSeconds = 2f;
    private const int MaxEntries = 16;

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

    private static void Build(GameItem machine, Dictionary<char, string> notes)
    {
        string id = "";
        try { id = machine.identifier ?? ""; } catch { }
        if (!Profiles.TryGetValue(id, out var profile)) return;

        var power = PowerModel(machine, profile);

        // 效率：只算效率自己省下的电。
        int? effPower = power == null ? null : Delta(machine, PowerTag, power, DrawnTag);
        if (effPower is int ep)
            notes[Efficiency] = Loc.T("module.power", "{0} power/day", Signed(ep));

        var perf = new List<string>();
        if (power != null)
        {
            int? speed = Delta(machine, PerfTag, power, SpeedTag);
            if (speed is int sp && sp != 0)
            {
                string text = Loc.T("module.speed", "{0} speed", Plus(sp));
                int faster = DaysFaster(machine, power);
                if (faster > 0) text += Loc.T("module.days_faster", ", about {0} day(s) faster", faster);
                perf.Add(text);
            }
            // 交易机、催陈器的性能也会降低耗电
            int? perfPower = Delta(machine, PerfTag, power, DrawnTag);
            if (perfPower is int pp && pp != 0)
                perf.Add(Loc.T("module.power", "{0} power/day", Signed(pp)));
        }

        if (profile.Performance != null
            && IntDelta(machine, PerfTag, profile.Performance) is int delta && delta != 0)
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
        else if (profile.Quality != null
                 && IntDelta(machine, QualityTag, profile.Quality) is int qualityDelta)
        {
            quality = Loc.T(profile.QualityKey, profile.QualityEn, Plus(qualityDelta));
            if (profile.QualityNextStep)
                quality = WithNextStep(quality, machine, QualityTag, profile.Quality);
        }

        if (perf.Count > 0) notes[Performance] = string.Join(Loc.T("list.sep", ", "), perf);
        if (!string.IsNullOrEmpty(quality)) notes[Quality] = quality!;
    }

    // ---------------- 耗电 / 速度模型 ----------------

    /// <summary>
    /// 这台机器把加成换算成耗电与速度用的游戏函数。探针在当前加成下必须复现机器上的实际值，
    /// 否则说明有别的东西改过（涡轮增压、第三方公式），返回 null。
    /// </summary>
    private static Action<GameItem>? PowerModel(GameItem machine, MachineEffectProfile profile)
    {
        Action<GameItem> model;
        bool system = GameRead.IsTag(machine, "SYSTEM_TAG");
        if (profile.UseDealMakerPower)
            model = MachineDealMaker.UpdatePowerUse;
        else if (system)
            model = ModuleHelper.ApplyModifiedPower;
        else
            model = p => ModuleHelper.ApplyBasicModuleEffect(null!, null!, p);

        int? actualPower = GameRead.TagInt(machine, DrawnTag);
        if (actualPower == null) return null;
        var probe = Probe(machine);
        if (probe == null) return null;
        try { model(probe); }
        catch { return null; }
        if (GameRead.TagInt(probe, DrawnTag) != actualPower) return null;

        // 有加工速度的机器，速度也要能复现；系统类模型不算速度，跳过
        int baseSpeed = GameRead.TagInt(machine, "BASE_PROCESSING_SPEED_TAG") ?? 0;
        if (baseSpeed > 0 && !system
            && GameRead.TagInt(probe, SpeedTag) != GameRead.TagInt(machine, SpeedTag))
            return null;
        return model;
    }

    /// <summary>正在加工的进度型机器：去掉性能后要多花几天。</summary>
    private static int DaysFaster(GameItem machine, Action<GameItem> power)
    {
        if (GameRead.TagString(machine, "MACHINE_STATE_TAG") != "STATE_WORKING") return 0;
        int cur = GameRead.TagInt(machine, "MACHINE_PROGRESS_CURRENT_TAG") ?? 0;
        int target = GameRead.TagInt(machine, "MACHINE_PROGRESS_TARGET_TAG") ?? 0;
        int speed = GameRead.TagInt(machine, SpeedTag) ?? 0;
        if (target <= cur || speed <= 0) return 0;

        var probe = Probe(machine, (PerfTag, 0));
        if (probe == null) return 0;
        try { power(probe); } catch { return 0; }
        int speed0 = GameRead.TagInt(probe, SpeedTag) ?? 0;
        if (speed0 <= 0) return 0;

        int left = target - cur;
        return Ceil(left, speed0) - Ceil(left, speed);
    }

    private static int Ceil(int a, int b) => (a + b - 1) / b;

    // ---------------- 差分 ----------------

    /// <summary>当前加成与「该项清零」时，模型写出的某个标签值之差。</summary>
    private static int? Delta(GameItem machine, string statTag, Action<GameItem> model, string outTag)
    {
        if ((GameRead.TagInt(machine, statTag) ?? 0) == 0) return null;
        var withStat = Probe(machine);
        if (withStat == null) return null;
        try { model(withStat); } catch { return null; }
        int? now = GameRead.TagInt(withStat, outTag);

        var without = Probe(machine, (statTag, 0));
        if (without == null) return null;
        try { model(without); } catch { return null; }
        int? zero = GameRead.TagInt(without, outTag);

        if (now == null || zero == null) return null;
        return now.Value - zero.Value;
    }

    /// <summary>当前加成与「该项清零」时，游戏函数返回值之差；该项为 0 时不写。</summary>
    private static int? IntDelta(GameItem machine, string statTag, Func<GameItem, int> f)
    {
        if ((GameRead.TagInt(machine, statTag) ?? 0) == 0) return null;
        var withStat = Probe(machine);
        if (withStat == null) return null;
        int now;
        try { now = f(withStat); } catch { return null; }

        var without = Probe(machine, (statTag, 0));
        if (without == null) return null;
        try { return now - f(without); } catch { return null; }
    }

    /// <summary>分档结果：当前档位下，再加多少百分点会进下一档；已到顶或找不到返回 -1。</summary>
    private static int NextStep<T>(GameItem machine, string statTag, Func<GameItem, T> f)
    {
        int cur = GameRead.TagInt(machine, statTag) ?? 0;
        T Eval(int bonus)
        {
            var p = Probe(machine, (statTag, bonus));
            if (p == null) throw new InvalidOperationException("probe");
            return f(p);
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
