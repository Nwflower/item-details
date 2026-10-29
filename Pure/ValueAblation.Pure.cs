using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 消融测量：在不知道公式的前提下，用"改一个标签再问游戏值多少"的方式，
/// 反推每个标签对最终价值的贡献。
///
/// 纯逻辑，不引用 UnityEngine —— 采样与求值由调用方以委托注入，因此可离线测试。
/// </summary>
internal enum AblationMode
{
    /// <summary>顺序消融：每一段都基于"已消融前面的键"的配置，保证 Σ贡献 = 当前值 − 基线值。</summary>
    Marginal,

    /// <summary>单独消融：每个键独立回到基线，互不影响；组合加成会体现为残差。</summary>
    LeaveOneOut,
}

internal static class ValueAblation
{
    internal readonly struct Part
    {
        internal readonly string Key;
        internal readonly string Value;
        internal readonly int Delta;
        internal Part(string key, string value, int delta)
        { Key = key; Value = value; Delta = delta; }
    }

    internal sealed class Result
    {
        internal int Current;
        internal int Baseline;
        internal readonly List<Part> Parts = new();
        /// <summary>当前值 − 基线值 − Σ贡献。顺序消融下恒为 0；单独消融下体现组合加成。</summary>
        internal int Residual;
        /// <summary>是否所有参与计算的键都在基线里出现过。</summary>
        internal bool Complete = true;
        internal int TotalDelta => Current - Baseline;
    }

    /// <summary>
    /// 消融测量：反推每个标签对价值的贡献。
    ///
    /// current  —— 物品当前的标签键值
    /// baseline —— 每个键的"常见值"（缺键表示不知道该键的基线，会标记 Complete=false 并跳过）
    /// evaluate —— 给定一套标签配置，返回游戏算出的价值（实机里是 type.getCorrectValue(probe)）
    ///
    /// 只对传入的配置求值，绝不修改调用方的物品。
    /// 遍历顺序沿用 current 的枚举顺序（Dictionary 保留插入顺序），
    /// 因此 Marginal 模式下组合加成的归属取决于键的顺序——这是固有性质，不是 bug。
    /// </summary>
    internal static Result Decompose(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> baseline,
        Func<IReadOnlyDictionary<string, string>, int> evaluate,
        AblationMode mode = AblationMode.Marginal,
        int maxItems = 24)
    {
        var r = new Result();
        r.Current = evaluate(current);

        // 基线配置 = 当前配置，但把所有"基线里有的键"换成基线值
        var baselineCfg = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in current) baselineCfg[kv.Key] = kv.Value;
        foreach (var kv in current)
        {
            if (baseline.TryGetValue(kv.Key, out string? bv) && bv != null)
                baselineCfg[kv.Key] = bv;
        }
        r.Baseline = evaluate(baselineCfg);

        if (maxItems <= 0)
        {
            r.Complete = false;
            r.Residual = r.Current - r.Baseline;
            return r;
        }

        if (mode == AblationMode.Marginal)
        {
            var working = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in current) working[kv.Key] = kv.Value;

            int previous = r.Current;
            foreach (var kv in current)
            {
                if (r.Parts.Count >= maxItems) { r.Complete = false; break; }
                if (!baseline.TryGetValue(kv.Key, out string? bv) || bv == null)
                {
                    r.Complete = false;   // 不知道该键的常见值，无法消融
                    continue;
                }
                if (string.Equals(bv, kv.Value, StringComparison.Ordinal)) continue;

                working[kv.Key] = bv;
                int after = evaluate(working);
                r.Parts.Add(new Part(kv.Key, kv.Value, previous - after));
                previous = after;
            }
        }
        else
        {
            foreach (var kv in current)
            {
                if (r.Parts.Count >= maxItems) { r.Complete = false; break; }
                if (!baseline.TryGetValue(kv.Key, out string? bv) || bv == null)
                {
                    r.Complete = false;
                    continue;
                }
                if (string.Equals(bv, kv.Value, StringComparison.Ordinal)) continue;

                var probe = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in current) probe[pair.Key] = pair.Value;
                probe[kv.Key] = bv;

                int after = evaluate(probe);
                r.Parts.Add(new Part(kv.Key, kv.Value, r.Current - after));
            }
        }

        int sum = 0;
        foreach (var part in r.Parts) sum += part.Delta;
        r.Residual = r.Current - r.Baseline - sum;
        return r;
    }
}
