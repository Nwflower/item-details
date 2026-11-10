using System;
using System.Collections.Generic;
using UnityEngine.Localization.Settings;

namespace ItemDetails;

/// <summary>
/// 全部原版读取都走这里。每个字段独立 try/catch，某一项失效只损失该项，不影响其它模块。
/// 所有类型/成员均已对照 0.46D 的 Il2CppAssemblies\Assembly-CSharp.dll 核实。
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

    /// <summary>
    /// 物品显示名，跟随游戏当前语言；读不到语言或词条时退回 <see cref="ItemName"/>。
    ///
    /// <see cref="ItemName"/> 读的 <c>item.name</c> 是物品创建或读档时按当时语言写死的：
    /// 原版建物品名走 <c>LocHelper.GetLocalizedItem("item_&lt;identifier&gt;_name")</c>，
    /// 之后切语言只换 UI 词条，不重写已有物品的名字（<c>LocHelper.RelocalizeIfMissing</c>
    /// 只修缺译占位符，见反编译 0.46D 的读档路径）。于是中文档里拿到的物品，
    /// 在英文界面下永远显示中文名（实测：自学习机器的三个模块在英文 tooltip 里
    /// 显示「环保模组 / 超频模组 / 精炼模组」）。这里按 identifier 现查原版 Item 表
    /// 补上当前语言，取名规则与 <c>ItemDirectory</c> 建物品时完全一致。
    /// </summary>
    internal static string ItemDisplayName(GameItem? it)
    {
        if (it == null) return "";
        string live = LiveItemName(it);
        if (live.Length > 0) return live;
        string name = ItemName(it);
        return name.Length > 0 ? name : it.identifier ?? "?";
    }

    /// <summary>identifier → 当前语言显示名。按语言码缓存，换语言整表重取。</summary>
    private static readonly Dictionary<string, string> LiveNames = new(StringComparer.Ordinal);
    private static string _liveNamesCode = "";

    private static string LiveItemName(GameItem it)
    {
        string id;
        try { id = it.identifier ?? ""; } catch { return ""; }
        if (id.Length == 0) return "";

        string code = Loc.Code;
        if (!string.Equals(code, _liveNamesCode, StringComparison.Ordinal))
        {
            LiveNames.Clear();
            _liveNamesCode = code;
        }
        if (LiveNames.TryGetValue(id, out var cached)) return cached;

        string resolved = LookupItemName(id);
        LiveNames[id] = resolved;
        return resolved;
    }

    /// <summary>
    /// 现查原版 Item 表的 <c>item_&lt;identifier&gt;_name</c>。两种情况下返回空、
    /// 由调用方退回固化名：没有这个词条（第三方物品另有一套键），或词条带着
    /// 未填充占位符——打印机芯片一类物品的名字按创建时的数量格式化
    /// （「3D Printer Chip (x{0} Box Cutter)」），显示时拿不到那个数量。
    /// </summary>
    internal static string LookupItemName(string identifier)
    {
        string key = "item_" + identifier + "_name";
        try
        {
            var entry = LocalizationSettings.StringDatabase.GetTable("Item")?.GetEntry(key);
            string value = entry?.Value ?? "";
            if (value.Length == 0 || value.Contains("{0}", StringComparison.Ordinal)) return "";
            return value;
        }
        catch { return ""; }
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
        try { return tag!.valueInt; } catch { return null; }
    }

    /// <summary>读标签字符串；标签不存在返回 null（与"存在但为空串"区分）。</summary>
    internal static string? TagString(GameItem? it, string key)
    {
        if (!TryPeekTag(it, key, out var tag)) return null;
        try { return tag!.valueString; } catch { return null; }
    }

    /// <summary>读标签 double（化学品纯度等按 double 存储的字段）。</summary>
    internal static double? TagDouble(GameItem? it, string key)
    {
        if (!TryPeekTag(it, key, out var tag)) return null;
        try { return tag!.valueDouble; } catch { return null; }
    }

    /// <summary>读标签是否启用；标签不存在返回 false。</summary>
    internal static bool TagEnabled(GameItem? it, string key)
    {
        if (!TryPeekTag(it, key, out var tag)) return false;
        try { return tag!.valueEnabled; } catch { return false; }
    }

    /// <summary>读标签的布尔值（如 <c>LOCK_STATUS</c>）；标签不存在返回 false。</summary>
    internal static bool TagBool(GameItem? it, string key)
    {
        if (!TryPeekTag(it, key, out var tag)) return false;
        try { return tag!.valueBool; } catch { return false; }
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

    // ---------- 果实品质（BERRY_*） ----------

    /// <summary>
    /// 带 <c>BERRY_TAG</c> 的物品按 <c>BERRY_CONDITION_INT</c> 分档。
    /// 这个标签由 <c>WineHelper.InitBerry</c> 授予，原版只有 wine_berry 走这条路，
    /// 但它并不是荧光莓专属：第三方果实只要调用同一个函数也会带上（实测酿酒拓展的
    /// 18 种果实）。所以这里只回答"这件物品的品质档位"，是不是荧光莓由调用方按名字显示。
    /// </summary>
    internal static BerryQuality ClassifyBerry(GameItem? it)
    {
        if (!IsTag(it, "BERRY_TAG")) return BerryQuality.NotBerry;
        int? cond = TagInt(it, "BERRY_CONDITION_INT");
        int? variant = TagInt(it, "BERRY_VISUAL_VARIANT_INT");
        return BerryQualityClassifier.Classify(cond ?? 0, variant ?? 0);
    }

    /// <summary>
    /// 这件果实对果酿评分的贡献：<c>WineHelper.StartFermentation</c> 把每个
    /// <c>WINE_INGREDIENT_TAG</c> 原料的 <c>BERRY_CONDITION_INT</c> 直接累加，
    /// 负值先 +1（发霉 −2 记 −1，未熟 −1 记 0）。第三方果实也走同一条公式。
    /// </summary>
    internal static int BerryWineScore(GameItem? it)
    {
        int cond = TagInt(it, "BERRY_CONDITION_INT") ?? 0;
        return cond < 0 ? cond + 1 : cond;
    }
}
