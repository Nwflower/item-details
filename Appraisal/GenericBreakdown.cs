using System;
using System.Collections.Generic;
using System.Text;

namespace ItemDetails;

/// <summary>
/// 泛型价值分解：对**任何已注册进 AppraisalItemRegistry 的鉴定类型**给出分解，
/// 包括其它模组用 ModHelper.AddAppraisalItem 新加的类型。
///
/// 原理：不猜公式，而是黑盒消融测量——
///   1. 用 type.createRandom() 采样若干样本，统计每个标签的"常见值"作为基线
///   2. 用 DirectoryMaster.Create 造一个探针物品，把真实物品的标签克隆过去
///   3. 逐个标签替换成基线值，再看 type.getCorrectValue(探针) 的变化量
///   4. 变化量就是该标签的贡献，交由纯逻辑 ValueAblation 汇总
///
/// 已知三类（hand/ribwich/plasma_fuel）走精选路径，这里直接跳过。
/// 全程只操作探针，不碰玩家物品；任何一步失败都退化为不显示。
/// </summary>
internal static class GenericBreakdown
{
    private const int BaselineSamples = 24;
    private const int MaxLines = 8;
    private const int MaxTags = 24;
    private const int MaxCacheEntries = 256;
    private const int MaxBaselineTypes = 64;
    private const float FailureCacheSeconds = 5f;

    private static readonly HashSet<string> Curated = new(StringComparer.Ordinal)
    { "hand", "ribwich", "plasma_fuel" };

    private static readonly Dictionary<string, List<string>> Cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> FailureCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Dictionary<string, Record>> BaselineCache =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> BaselineFailures = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Record> EmptyBaseline = new(StringComparer.Ordinal);
    private static bool _registryLogged;

    /// <summary>
    /// 标签值的完整快照：TagState 有 string/int/float/long/double/bool 六个值槽
    /// 和启用位，只取 string/int 会把「disabled」「浮点差异」等语义压平——
    /// 既可能错误归因，也可能让不同物品共用同一份缓存分解。
    /// </summary>
    private readonly struct Record
    {
        internal readonly bool Enabled;
        internal readonly string Str;
        internal readonly int Int;
        internal readonly float Float;
        internal readonly long Long;
        internal readonly double Double;
        internal readonly bool Bool;
        internal readonly string Canonical;

        internal Record(TagState st)
        {
            Enabled = st.valueEnabled;
            Str = st.valueString ?? "";
            Int = st.valueInt;
            Float = st.valueFloat;
            Long = st.valueLong;
            Double = st.valueDouble;
            Bool = st.valueBool;

            var sb = new StringBuilder(72);
            sb.Append(Enabled ? '1' : '0').Append('|')
              .Append(Str.Length).Append(':').Append(Str).Append('|')
              .Append(Int).Append('|')
              .Append(BitConverter.SingleToInt32Bits(Float)).Append('|')
              .Append(Long).Append('|')
              .Append(BitConverter.DoubleToInt64Bits(Double)).Append('|')
              .Append(Bool ? '1' : '0');
            Canonical = sb.ToString();
        }

        /// <summary>从规范串里取回字符串槽（格式：enabled|len:str|int|float|long|double|bool）。</summary>
        internal static string StrOf(string canonical)
        {
            try
            {
                int bar = canonical.IndexOf('|');
                if (bar < 0) return canonical;
                int colon = canonical.IndexOf(':', bar + 1);
                if (colon < 0) return "";
                if (!int.TryParse(canonical.Substring(bar + 1, colon - bar - 1), out int len)) return "";
                if (len < 0 || colon + 1 + len > canonical.Length) return "";
                return canonical.Substring(colon + 1, len);
            }
            catch { return ""; }
        }
    }

    internal static List<string> Collect(GameItem? item, bool zh)
    {
        var lines = new List<string>();
        try
        {
            if (item == null) return lines;
            if (!GameRead.IsTag(item, "APPRAISAL_ITEM")) return lines;

            AppraisalItemType? type;
            try { type = AppraisalItemRegistry.Find(item); }
            catch (Exception ex) { Log.Warn("Generic.Find", ex); return lines; }
            if (type == null) return lines;
            if (type.id != null && Curated.Contains(type.id)) return lines;   // 精选路径负责
            if (type.getCorrectValue == null) return lines;

            LogRegistryOnce();

            // 显示文本随语言变化，缓存键必须带语言，不能复用上一次语言的字符串。
            string key = (zh ? "zh|" : "en|") + CacheKey(type, item);
            if (Cache.TryGetValue(key, out var cached))
            {
                FailureCache.Remove(key);
                return cached;
            }

            float now = UnityEngine.Time.unscaledTime;
            if (FailureCache.TryGetValue(key, out float failedAt))
            {
                if (now - failedAt <= FailureCacheSeconds) return lines;
                FailureCache.Remove(key);
            }

            var built = Build(type, item, type.getCorrectValue, zh, out bool reliable);
            if (!reliable)
            {
                // 24 次采样和逐标签消融可能反复失败；短暂负缓存避免同一物品
                // 每次 tooltip 重建都重跑，5 秒后仍允许恢复。
                if (FailureCache.Count >= MaxCacheEntries) FailureCache.Clear();
                FailureCache[key] = now;
                return lines;
            }

            FailureCache.Remove(key);
            if (Cache.Count >= MaxCacheEntries) Cache.Clear();
            Cache[key] = built;
            if (built.Count > 0) Log.MarkOk("GenericBreakdown");
            return built;
        }
        catch (Exception ex)
        {
            Log.Warn("GenericBreakdown", ex);
            return lines;
        }
    }

    internal static void Stop()
    {
        Cache.Clear();
        FailureCache.Clear();
        BaselineCache.Clear();
        BaselineFailures.Clear();
        _registryLogged = false;
    }

    private static List<string> Build(AppraisalItemType type, GameItem item,
        Func<GameItem, int> getValue, bool zh, out bool reliable)
    {
        reliable = false;
        var lines = new List<string>();

        // 1) 基线采样：成功按类型缓存；失败短路 5 秒，之后允许重试。
        var baseline = GetBaseline(type);
        if (baseline.Count == 0)
        {
            Log.Warn("Generic.Baseline", "无法采样基线：" + type.id);
            return lines;
        }

        GameItem? probe = null;
        try
        {
            // 先读真实物品的值，后面用它验证探针确实复现了同一对象。
            int itemValue;
            try { itemValue = getValue(item); }
            catch (Exception ex) { Log.Warn("Generic.RealValue", ex); return lines; }
            if (itemValue == 0) return lines;

            // 2) 探针：与真实物品同标签
            string id = string.IsNullOrEmpty(type.itemId) ? item.identifier : type.itemId;
            probe = DirectoryMaster.Create<GameItem>(id);
            if (probe == null) { Log.Warn("Generic.Probe", "创建探针失败：" + id); return lines; }
            GameItem probeItem = probe;
            var clonedTags = GameRead.DeepCloneTags(item.modifiedState);
            if (clonedTags == null) { Log.Warn("Generic.Clone", "标签克隆失败"); return lines; }
            probeItem.modifiedState = clonedTags;
            probeItem.state = probeItem.modifiedState;

            // 3) 当前值与基线值的字符串视图，供纯逻辑决策
            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            var baseView = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in TagRecords(item))
            {
                current[kv.Key] = kv.Value.Canonical;
                if (baseline.TryGetValue(kv.Key, out var baseRec)) baseView[kv.Key] = baseRec.Canonical;
            }
            if (current.Count == 0) return lines;

            // 4) 求值：每次从真实物品重新克隆标签，再应用覆盖项，避免状态累积。
            // 任何异常或标签写入失败都把本轮标为不可靠，不能把 0 当作真实定价。
            bool evalFailed = false;
            int Evaluate(IReadOnlyDictionary<string, string> cfg)
            {
                if (evalFailed) return 0;
                try
                {
                    var clonedEval = GameRead.DeepCloneTags(item.modifiedState);
                    if (clonedEval == null) { evalFailed = true; return 0; }
                    probeItem.modifiedState = clonedEval;
                    probeItem.state = probeItem.modifiedState;

                    foreach (var kv in cfg)
                    {
                        if (!baseline.TryGetValue(kv.Key, out var baseRec)) continue;
                        if (!current.TryGetValue(kv.Key, out var curCanon)) continue;
                        if (baseRec.Canonical == curCanon) continue;    // 该键本来就在基线值上
                        if (kv.Value != baseRec.Canonical) continue;    // 只有"被替换为基线"的键才生效
                        if (!Apply(probeItem, kv.Key, baseRec))
                        {
                            evalFailed = true;
                            return 0;
                        }
                    }
                    return getValue(probeItem);
                }
                catch (Exception ex)
                {
                    Log.Warn("Generic.Evaluate", ex);
                    evalFailed = true;
                    return 0;
                }
            }

            var result = ValueAblation.Decompose(current, baseView, Evaluate, AblationMode.Marginal, MaxTags);

            // 探针复现值必须与真实物品一致，且所有标签都能归因，才允许显示。
            if (evalFailed || result.Current != itemValue || !result.Complete) return lines;
            reliable = true;

            // 5) 输出：已知标签用玩家可读名称；未知标签合并成一行“其他属性”，
            // 不把内部 TAG=value 直接暴露在 tooltip 里。
            var known = new List<(ValueAblation.Part Part, string Name)>();
            int otherDelta = 0;
            foreach (var p in result.Parts)
            {
                string? name = TagName(p.Key, p.Value, zh);
                if (name == null) otherDelta += p.Delta;
                else known.Add((p, name));
            }
            known.Sort((a, b) => Math.Abs(b.Part.Delta).CompareTo(Math.Abs(a.Part.Delta)));

            for (int i = 0; i < known.Count && i < MaxLines; i++)
            {
                var knownItem = known[i];
                string sign = knownItem.Part.Delta > 0 ? $"+{knownItem.Part.Delta}" : knownItem.Part.Delta.ToString();
                lines.Add($"· {knownItem.Name} {sign}");
            }
            if (known.Count > MaxLines)
                lines.Add(zh ? $"· …另有 {known.Count - MaxLines} 项" : $"· ... and {known.Count - MaxLines} more");
            if (otherDelta != 0)
            {
                string sign = otherDelta > 0 ? $"+{otherDelta}" : otherDelta.ToString();
                lines.Add(zh ? $"· 其他属性 {sign}" : $"· Other attributes {sign}");
            }

            if (result.Residual != 0)
            {
                string sign = result.Residual > 0 ? $"+{result.Residual}" : result.Residual.ToString();
                lines.Add(zh ? $"· 组合/其它 {sign}" : $"· Combined/other {sign}");
            }

            return lines;
        }
        catch (Exception ex)
        {
            Log.Warn("Generic.Build", ex);
            reliable = false;
            return lines;
        }
        finally
        {
            if (probe != null)
            {
                try { probe.Destroy(); } catch { }
            }
        }
    }

    private static Dictionary<string, Record> GetBaseline(AppraisalItemType type)
    {
        string key = (type.id ?? "") + "|" + (type.itemId ?? "") + "|" + (type.source ?? "");
        if (BaselineCache.TryGetValue(key, out var cached)) return cached;

        float now = UnityEngine.Time.unscaledTime;
        if (BaselineFailures.TryGetValue(key, out float failedAt))
        {
            if (now - failedAt <= FailureCacheSeconds) return EmptyBaseline;
            BaselineFailures.Remove(key);
        }

        var baseline = SampleBaseline(type);
        if (baseline.Count > 0)
        {
            BaselineFailures.Remove(key);
            if (BaselineCache.Count >= MaxBaselineTypes) BaselineCache.Clear();
            BaselineCache[key] = baseline;
        }
        else
        {
            if (BaselineFailures.Count >= MaxBaselineTypes) BaselineFailures.Clear();
            BaselineFailures[key] = now;
        }
        return baseline;
    }

    // ---------------- 基线采样 ----------------

    private static Dictionary<string, Record> SampleBaseline(AppraisalItemType type)
    {
        // createRandom 会消耗 UnityEngine.Random。基线采样属于只读分析，不应改变
        // 玩家后续的随机结果；采样结束后恢复原状态。第三方若使用 System.Random，
        // 其内部状态无法从这里保存，这一点仍属于第三方实现的边界。
        UnityEngine.Random.State rngState = UnityEngine.Random.state;
        try
        {
            return SampleBaselineCore(type);
        }
        finally
        {
            UnityEngine.Random.state = rngState;
        }
    }

    private static Dictionary<string, Record> SampleBaselineCore(AppraisalItemType type)
    {
        var counts = new Dictionary<string, Dictionary<string, (Record rec, int n)>>(StringComparer.Ordinal);
        for (int i = 0; i < BaselineSamples; i++)
        {
            GameItem? sample;
            try { sample = type.createRandom?.Invoke(); }
            catch { continue; }
            if (sample == null) continue;

            try
            {
                foreach (var kv in sample.modifiedState.dict)
                {
                    if (kv.Value == null) continue;
                    var rec = ToRecord(kv.Value);
                    if (!counts.TryGetValue(kv.Key, out var bucket))
                    {
                        bucket = new Dictionary<string, (Record, int)>(StringComparer.Ordinal);
                        counts[kv.Key] = bucket;
                    }
                    if (bucket.TryGetValue(rec.Canonical, out var existing))
                        bucket[rec.Canonical] = (existing.rec, existing.n + 1);
                    else
                        bucket[rec.Canonical] = (rec, 1);
                }
            }
            catch { }
            finally
            {
                try { sample.Destroy(); } catch { }
            }
        }

        var result = new Dictionary<string, Record>(StringComparer.Ordinal);
        foreach (var kv in counts)
        {
            Record best = default;
            int bestN = -1;
            foreach (var inner in kv.Value)
            {
                if (inner.Value.n > bestN) { bestN = inner.Value.n; best = inner.Value.rec; }
            }
            if (bestN > 0) result[kv.Key] = best;
        }
        return result;
    }

    // ---------------- 工具 ----------------

    private static Record ToRecord(TagState st) => new Record(st);

    private static bool Apply(GameItem probe, string tag, Record rec)
    {
        try
        {
            // 保留 enabled 与全部值槽：强制 Enable 会把「禁用标签」变成生效标签，
            // 让消融结果与真实物品定价不一致。
            probe.ModifyTag(tag, t =>
            {
                t.SetEnabled(rec.Enabled);
                t.SetString(rec.Str);
                t.SetInt(rec.Int);
                t.SetFloat(rec.Float);
                t.SetLong(rec.Long);
                t.SetDouble(rec.Double);
                t.SetBool(rec.Bool);
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 物品当前标签（键 → 规范化值），最多 MaxTags 项。
    /// Build 与 CacheKey 必须用同一份口径，否则缓存键与实际消融的标签集不一致。
    /// </summary>
    private static List<KeyValuePair<string, Record>> TagRecords(GameItem item)
    {
        var result = new List<KeyValuePair<string, Record>>();
        try
        {
            foreach (var kv in item.modifiedState.dict)
            {
                if (kv.Value == null) continue;
                if (result.Count >= MaxTags) break;
                result.Add(new KeyValuePair<string, Record>(kv.Key, ToRecord(kv.Value)));
            }
        }
        catch { }
        return result;
    }

    private static string CacheKey(AppraisalItemType type, GameItem item)
    {
        var sb = new StringBuilder();
        sb.Append(type.id).Append('|');

        var keys = new List<string>();
        foreach (var kv in TagRecords(item))
            keys.Add(kv.Key + "=" + kv.Value.Canonical);

        keys.Sort(StringComparer.Ordinal);
        foreach (string k in keys) sb.Append(k).Append(';');
        return sb.ToString();
    }

    /// <summary>标签显示名：已知的返回玩家可读名称；未知返回 null，由调用方合并。</summary>
    private static string? TagName(string tag, string canonical, bool zh)
    {
        string raw = Record.StrOf(canonical ?? "");

        string? friendly = (tag, raw) switch
        {
            ("HAND_MATERIAL", "GOLD_PLATED") => zh ? "镀金" : "Gold plating",
            ("HAND_MATERIAL", "CARBON_COMPOSITE") => zh ? "碳纤维" : "Carbon-fiber shell",
            ("HAND_INTERFACE", "NEURAL") => zh ? "神经接口" : "Neural jack",
            ("HAND_INTERFACE", "EMG_PLUS") => zh ? "能源标" : "EMG+ connector",
            ("HAND_CONDITION", "WEAR") => zh ? "磨损" : "Worn",
            ("HAND_CONDITION", "DAMAGE") => zh ? "受损" : "Damaged",
            ("HAND_ADDON_ARMORED", "ARMORED") => zh ? "装甲组件" : "Armor module",
            ("HAND_ADDON_BLADED", "BLADED") => zh ? "刀刃组件" : "Blade module",
            ("PLASMA_SEAL", "LL") => zh ? "王尔德载具" : "Wild's Rides",
            ("PLASMA_SEAL", "ZHONGHUA") => zh ? "中华船舶" : "ZhongHua",
            ("PLASMA_SEAL", "REPUBLIC") => zh ? "共和国海军" : "Republic Naval",
            ("PLASMA_SEAL", "IGNITION_LAB") => zh ? "点火实验室" : "Ignition Lab",
            ("PLASMA_COLOR", "HIGH_QUALITY") => zh ? "高级颜色" : "Premium finish",
            ("PLASMA_COLOR", "POOR1") or ("PLASMA_COLOR", "POOR2") => zh ? "低级颜色" : "Cheap finish",
            ("PLASMA_CASING_CONDITION_WEAR", "WEAR") => zh ? "轻度磨损" : "Slight wear",
            ("PLASMA_CASING_CONDITION_WEAR", "WEAR_HEAVY") => zh ? "重度磨损" : "Severe wear",
            ("PLASMA_CASING_CONDITION_CRACK", "CRACK") => zh ? "外壳破裂" : "Casing fracture",
            ("PLASMA_CASING_CONDITION_UNCOVERED", "UNCOVERED") => zh ? "螺丝外露" : "Loose screws",
            ("PLASMA_CASING_CONDITION_REINFORCED", "REINFORCED") => zh ? "加固处理" : "Reinforcement",
            _ => null,
        };

        return friendly;
    }

    private static void LogRegistryOnce()
    {
        if (_registryLogged) return;
        _registryLogged = true;
        try
        {
            var all = AppraisalItemRegistry.GetAll();
            int vanilla = 0, modded = 0;
            var ids = new StringBuilder();
            foreach (var t in all)
            {
                if (t == null) continue;
                bool isVanilla = string.IsNullOrEmpty(t.source) || t.source == "vanilla";
                if (isVanilla) vanilla++; else modded++;
                ids.Append(t.id).Append(' ');
            }
        }
        catch (Exception ex) { Log.Warn("Generic.Registry", ex); }
    }
}
