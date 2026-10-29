using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 价值构成适配层：从 GameItem 读标签，调用纯公式 <c>Pure/ValueFormula.Pure.cs</c>，
/// 并做「拆解合计必须等于游戏真实值」的自校验。
///
/// 仅适用于机械手(HAND)、肋明治(RIBWICH)、等离子(PLASMA) 三类；
/// 这三类没有真伪概念，只有价值构成。
///
/// 公式常量来自 0.46D 反编译结果（AppHandHelper / RibwichHelper / PlasmaHelper）。
/// </summary>
internal static partial class ValueBreakdown
{
    internal static Kind DetectKind(GameItem? item)
    {
        if (GameRead.IsTag(item, "HAND_TAG")) return Kind.Hand;
        if (GameRead.IsTag(item, "RIBWICH_TAG")) return Kind.Ribwich;
        if (GameRead.IsTag(item, "PLASMA_CELL_TAG")) return Kind.Plasma;
        return Kind.None;
    }

    internal static Result? Analyze(GameItem? item)
    {
        if (item == null) return null;
        try
        {
            int native = GameRead.TrueValue(item) ?? 0;
            Kind k = DetectKind(item);

            Func<string, string?> str = key => StrTag(item, key);
            Func<string, int?> integer = key => GameRead.TagInt(item, key);

            Result r = k switch
            {
                Kind.Hand => ComputeHand(str),
                Kind.Plasma => ComputePlasma(str),
                Kind.Ribwich => ComputeRibwich(integer, native),
                _ => new Result { Kind = Kind.None, Valid = false },
            };
            if (!r.Valid) return null;

            // 与游戏真实值核对，不一致就不显示（宁可不说，也不说错）
            if (r.Total != native)
            {
                Log.Warn("ValueBreakdown.Mismatch",
                    $"kind={r.Kind} 计算={r.Total} 游戏={native}（公式可能随版本变化，本次不显示）");
                return null;
            }
            return r;
        }
        catch (Exception ex)
        {
            Log.Warn("ValueBreakdown", ex);
            return null;
        }
    }

    private static string? StrTag(GameItem item, string key) => GameRead.TagString(item, key);
}
