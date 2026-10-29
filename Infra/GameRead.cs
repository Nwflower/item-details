using System;
using System.Collections.Generic;

namespace ItemDetails;

/// <summary>
/// 集中常用游戏读取：标签先查字典，缺键返回空结果；游戏估价等调用失败时记录并返回空结果。
/// 标签与估价接口已对照 Mono 版 0.46D 的 Managed/Assembly-CSharp.dll 核实。
/// </summary>
internal static class GameRead
{
    // ---------- 基础 ----------

    internal static string SafeStr(object? v)
    {
        if (v == null) return "";
        try { return v.ToString() ?? ""; } catch { return ""; }
    }

    internal static string ItemName(GameItem? it)
    {
        try { return it == null ? "" : SafeStr(it.name); } catch { return "?"; }
    }

    internal static int UniqueId(GameItem? it)
    {
        try { return it == null ? -1 : it.uniqueId; } catch { return -1; }
    }

    internal static bool IsTag(GameItem? it, string key)
    {
        try { return it != null && it.IsTag(key); } catch { return false; }
    }

    /// <summary>
    /// 只读地取标签引用：<b>不</b>经 GetTagReadonly。
    /// 原版 GameItem.GetTagReadonly 会调用 TagSystem.GetTag，而 GetTag 在缺键时会
    /// InitTagString 往真实物品的 dict 里插入一个 disabled TagState——
    /// 于是"读取"会改动物品状态（旧存档/第三方物品缺键时尤其明显）。
    /// 这里直接查字典，缺键返回 false。
    /// </summary>
    internal static bool TryPeekTag(GameItem? it, string key, out TagState? tag)
    {
        tag = null;
        if (it == null || string.IsNullOrEmpty(key)) return false;
        try
        {
            if (!it.modifiedState.dict.TryGetValue(key, out var found) || found == null) return false;
            tag = found;
            return true;
        }
        catch { return false; }
    }

    /// <summary>读标签的整数值。返回 null 表示标签不存在或读不到。
    /// 原版用特殊值标记"缺失/未设置"，例如 -1 常表示缺失，交由调用方按语义判断。</summary>
    internal static int? TagInt(GameItem? it, string key)
    {
        if (!TryPeekTag(it, key, out var tag)) return null;
        return tag!.valueInt;
    }

    /// <summary>读标签字符串；标签不存在返回 null（与"存在但为空串"区分）。</summary>
    internal static string? TagString(GameItem? it, string key)
    {
        if (!TryPeekTag(it, key, out var tag)) return null;
        return tag!.valueString;
    }

    /// <summary>读标签 double（化学品纯度等按 double 存储的字段）。</summary>
    internal static double? TagDouble(GameItem? it, string key)
    {
        if (!TryPeekTag(it, key, out var tag)) return null;
        return tag!.valueDouble;
    }

    /// <summary>读标签是否启用；标签不存在返回 false。</summary>
    internal static bool TagEnabled(GameItem? it, string key)
    {
        if (!TryPeekTag(it, key, out var tag)) return false;
        return tag!.valueEnabled;
    }

    // ---------- 估价 ----------

    /// <summary>原版真实基础价值。仅对可鉴定物品（APPRAISAL_ITEM）有意义；普通物品通常返回 0。</summary>
    internal static int? TrueValue(GameItem? it)
    {
        if (it == null) return null;
        try { return AppraisalHelper.GetCorrectBaseValue(it); }
        catch (Exception ex) { Log.Warn("TrueValue", ex); return null; }
    }

    /// <summary>实际占用格数（只数实心格，不用外接矩形的宽×高）。</summary>
    /// <summary>
    /// 深拷贝标签系统：TagSystem.Clone() 只复制字典，而 TagState 是引用类型，
    /// 浅拷贝会让"探针"与真实物品共用同一批 TagState —— 任何 ModifyTag
    /// 实际改的都是真实物品（曾导致悬停即净化水源、消融改写鉴定品属性）。
    /// </summary>
    internal static TagSystem? DeepCloneTags(TagSystem? src)
    {
        // 不再"给个空标签集"兜底：调用方应当跳过，而不是拿空数据算出假结果
        if (src == null) return null;
        var dst = new TagSystem();
        try
        {
            foreach (var kv in src.dict)
            {
                if (kv.Value == null) continue;
                dst.dict[kv.Key] = kv.Value.Clone();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("GameRead.DeepCloneTags", ex);
            return null;
        }
        return dst;
    }
    internal static int OccupiedCells(GridShape? shape)
    {
        if (shape == null) return 0;
        try
        {
            int cells = 0;
            int w = shape.width;
            int h = shape.height;
            if (w <= 0 || h <= 0) return 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (shape.GetLocal(x, y) != 0) cells++;
                }
            }
            return cells;
        }
        catch
        {
            // 算不出实心格数就是算不出：不退回外接矩形（那会把非矩形物品的格数算大，
            // 让压缩率/性价比一起错）。调用方拿到 0 会跳过该行。
            return 0;
        }
    }

    /// <summary>
    /// 容器内部网格的**可用格数**。注意编码与物品外形相反：游戏用
    /// <c>new GridShapeBuilder(w, h).SetDataFill(0).SetDataOutside(1)</c> 建内部网格，
    /// 即 0 = 可放置格、1 = 墙或外侧；字符串形式（<c>SetData(shape, width)</c>）也沿用同一约定。
    /// 若照物品外形的 "!= 0 即实心" 来数，矩形容器会恒为 0，压缩率整行消失。
    /// </summary>
    internal static int UsableCells(GridShape? shape)
    {
        if (shape == null) return 0;
        try
        {
            int w = shape.width;
            int h = shape.height;
            if (w <= 0 || h <= 0) return 0;

            int cells = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (shape.GetLocal(x, y) == 0) cells++;
                }
            }
            return cells;
        }
        catch (Exception ex)
        {
            Log.Warn("GameRead.UsableCells", ex);
            return 0;
        }
    }

    // ---------- 荧光莓 ----------

    internal static BerryQuality ClassifyBerry(GameItem? it)
    {
        if (!IsTag(it, "BERRY_TAG")) return BerryQuality.NotBerry;
        int? cond = TagInt(it, "BERRY_CONDITION_INT");
        int? variant = TagInt(it, "BERRY_VISUAL_VARIANT_INT");
        return BerryQualityClassifier.Classify(cond ?? 0, variant ?? 0);
    }
}
