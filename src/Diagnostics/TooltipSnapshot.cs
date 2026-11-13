#if ITEMDETAILS_SELFTEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace ItemDetails;

/// <summary>
/// tooltip 可视化快照的序列化器：把一次 <c>RichTextBuilder.Build</c> 实际消费的折行后片段
/// （以及 Build 产出的各图层原始富文本）写成 JSON。格式说明见 <c>tools/TOOLTIP_SNAPSHOT.md</c>。
///
/// 片段来源有两条，序列化只认"Build 依次交给 <c>TextSlice.GetString</c> 的片段列表"这一份：
///   - 游戏内：<see cref="TooltipSnapshotRecorder"/> 用 Harmony 后缀接 GetString；
///   - 离线夹具：测试把游戏程序集的副本静态改写，在 GetString 开头调用
///     <c>RichTextBuilder.ItemDetailsSnapshotHook</c>（见 <see cref="OfflineItem"/>）。
/// 两条路径之后走同一段代码，所以离线测试覆盖的就是游戏内用的序列化。
///
/// 只在 SelfTest 构建里编译，Release / Diag 不含这个类型。
/// </summary>
internal static class TooltipSnapshot
{
    internal const int Schema = 1;

    // ---------------- 原版 TextSlice 的读取（私有嵌套类型，按名字反射） ----------------

    private static Type? _sliceType;
    private static FieldInfo? _fText, _fBold, _fItalic, _fUnderline, _fStrike, _fColor, _fBackground,
        _fUnderlineColor, _fStrikeColor, _fNewLine, _fNoBreak, _fSlices;
    private static MethodInfo? _mGetString, _mGetLength;
    private static string? _bindError;
    private static bool _bound;

    /// <summary>绑定 TextSlice 的字段与方法；缺任何一项都返回说明，调用方据此整体放弃，不猜。</summary>
    internal static string? Bind()
    {
        if (_bound) return _bindError;
        _bound = true;
        var missing = new List<string>();
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        // 游戏的模组扫描器只认"能追到字面量的类型"上的反射：typeof(X)，或 Type.GetType("字面量") 的返回值
        // （可以先存进局部变量）。GetNestedType 的结果、闭包捕获的变量、静态字段都算"运行时算出的类型"，
        // 会被判成未声明的 reflection-external，自检版就启用不了。所以这里逐条写，不用局部函数。
        Type? slice = Type.GetType("RichTextBuilder+TextSlice, Assembly-CSharp");
        _sliceType = slice;
        _fSlices = typeof(RichTextBuilder).GetField("slices", Any);
        if (_fSlices == null) missing.Add("RichTextBuilder.slices");
        if (slice == null)
        {
            missing.Add("RichTextBuilder+TextSlice");
            _bindError = "缺少 " + string.Join(", ", missing);
            return _bindError;
        }
        _fText = Need(slice.GetField("text", Any), "text", missing);
        _fBold = Need(slice.GetField("bold", Any), "bold", missing);
        _fItalic = Need(slice.GetField("italic", Any), "italic", missing);
        _fUnderline = Need(slice.GetField("underline", Any), "underline", missing);
        _fStrike = Need(slice.GetField("strikethrough", Any), "strikethrough", missing);
        _fColor = Need(slice.GetField("color", Any), "color", missing);
        _fBackground = Need(slice.GetField("backgroundColor", Any), "backgroundColor", missing);
        _fUnderlineColor = Need(slice.GetField("underlineColor", Any), "underlineColor", missing);
        _fStrikeColor = Need(slice.GetField("strikethroughColor", Any), "strikethroughColor", missing);
        _fNewLine = Need(slice.GetField("isNewLine", Any), "isNewLine", missing);
        _fNoBreak = Need(slice.GetField("noBreak", Any), "noBreak", missing);
        _mGetString = slice.GetMethod("GetString", Any);
        _mGetLength = slice.GetMethod("GetLength", Any);
        if (_mGetString == null) missing.Add("TextSlice.GetString");
        if (_mGetLength == null) missing.Add("TextSlice.GetLength");
        _bindError = missing.Count > 0 ? "缺少 " + string.Join(", ", missing) : null;
        return _bindError;
    }

    private static FieldInfo? Need(FieldInfo? field, string name, List<string> missing)
    {
        if (field == null) missing.Add("TextSlice." + name);
        return field;
    }

    internal static Type? SliceType => Bind() == null ? _sliceType : null;

    /// <summary>TextSlice.GetString（录制器在它上面挂后置补丁）；绑定失败时为 null。</summary>
    internal static MethodInfo? GetStringMethod => Bind() == null ? _mGetString : null;

    /// <summary>builder 当前的片段数；拿不到时 -1。</summary>
    internal static int SliceCount(RichTextBuilder? builder)
    {
        if (builder == null || Bind() != null) return -1;
        return _fSlices!.GetValue(builder) is IList list ? list.Count : -1;
    }

    /// <summary>builder 的片段列表（不复制）；拿不到时 null。</summary>
    internal static IList? Slices(RichTextBuilder? builder)
    {
        if (builder == null || Bind() != null) return null;
        return _fSlices!.GetValue(builder) as IList;
    }

    internal static string SliceText(object slice) => _fText!.GetValue(slice) as string ?? "";
    internal static bool SliceIsNewLine(object slice) => (bool)_fNewLine!.GetValue(slice)!;

    /// <summary>一个折行后的片段：Build 交给 GetString 的那一份，字段原样。</summary>
    internal sealed class Segment
    {
        internal string Text = "";
        internal int Length;
        internal RenderHandler.ColorPalette Color, Background, UnderlineColor, StrikeColor;
        internal bool Bold, Italic, Underline, Strike, NoBreak;
        internal BarInfo? Bar;
    }

    internal sealed class BarInfo
    {
        internal int Length, Fill;
        internal float? Ratio;
        internal RenderHandler.ColorPalette Dark = RenderHandler.ColorPalette.None;
        internal bool FullLength = true;
        internal bool Recorded;
    }

    internal static Segment ReadSegment(object slice)
    {
        return new Segment
        {
            Text = _fText!.GetValue(slice) as string ?? "",
            Length = (int)_mGetLength!.Invoke(slice, null)!,
            Bold = (bool)_fBold!.GetValue(slice)!,
            Italic = (bool)_fItalic!.GetValue(slice)!,
            Underline = (bool)_fUnderline!.GetValue(slice)!,
            Strike = (bool)_fStrike!.GetValue(slice)!,
            Color = (RenderHandler.ColorPalette)_fColor!.GetValue(slice)!,
            Background = (RenderHandler.ColorPalette)_fBackground!.GetValue(slice)!,
            UnderlineColor = (RenderHandler.ColorPalette)_fUnderlineColor!.GetValue(slice)!,
            StrikeColor = (RenderHandler.ColorPalette)_fStrikeColor!.GetValue(slice)!,
            NoBreak = (bool)_fNoBreak!.GetValue(slice)!,
        };
    }

    /// <summary>按换行片段切成行；每行是该行的片段，顺序即 Build 输出顺序。</summary>
    internal static List<List<Segment>> Lines(IList folded)
    {
        var lines = new List<List<Segment>> { new() };
        foreach (object slice in folded)
        {
            if (slice == null) continue;
            if (SliceIsNewLine(slice)) { lines.Add(new List<Segment>()); continue; }
            lines[lines.Count - 1].Add(ReadSegment(slice));
        }
        return lines;
    }

    /// <summary>
    /// 自检：把记录到的片段逐个再调一次原版 GetString 拼起来，必须与 Build 的 nakedText / 前景逐字相同。
    /// 相同就说明记录到的就是 Build 实际消费的那份片段（没漏、没多、顺序没错）。
    /// 调用时不能处于记录中，否则会把自己的调用记进去。
    /// </summary>
    internal static string Recompose(IList folded)
    {
        var sb = new StringBuilder();
        foreach (object slice in folded)
        {
            if (slice == null) continue;
            object? tuple = _mGetString!.Invoke(slice, new object[] { -1 });
            if (tuple == null) continue;
            // 返回值是 (string, int) 元组；走 ITuple 取第一项，不对运行时类型做反射（扫描器会拒）。
            sb.Append((tuple as System.Runtime.CompilerServices.ITuple)?[0] as string ?? "");
        }
        return sb.ToString();
    }

    // ---------------- 一件物品的录制结果 ----------------

    internal sealed class Badge
    {
        internal string Type = "", Text = "";
        internal string? Background, Border;
        internal bool Warning;
    }

    internal sealed class ContentsCell
    {
        internal string Id = "", Name = "";
        internal int Count;
    }

    internal sealed class Contents
    {
        internal bool Sealed;
        internal string? Estimate;
        internal int Total, Overflow;
        internal readonly List<ContentsCell> Cells = new();
    }

    internal sealed class Window
    {
        internal int MaxChars;
        internal int WidthPx = -1, HeightPx = -1;
        internal string? Text;
        internal bool? BadgesShown;
        internal string? Error;
    }

    internal sealed class ItemCapture
    {
        internal GameItem Item = null!;
        internal string Source = "ingame";
        internal string? Label;
        /// <summary>录制时额外打开的状态（如 gene-scanner）；默认状态为 null。</summary>
        internal string? State;
        internal IList Folded = new List<object>();
        internal RichText? Raw;
        internal RichText? Final;
        internal int RawWidth = -1;
        internal int MaxCharWidth = -1;
        internal int BuildMaxCharWidth = -1;
        internal List<ProgressBarStyle.BarRecord>? Bars;
        internal bool SpriteBars;
        internal string? Company;
        internal List<Badge>? Badges;
        internal Contents? Contents;
        internal Window? Window;
        /// <summary>P2 原版带：由录制器写出 calls 数组；离线为 null。</summary>
        internal Action<JsonOut>? WriteCalls;
        internal int? RootBuilder;
        internal readonly List<string> Notes = new();
    }

    private static readonly Regex Spacer = new(@"^<size=(\d+)px> </size>$", RegexOptions.CultureInvariant);
    private static readonly Regex BarText = new(@"^\[(=*)( *)\]$", RegexOptions.CultureInvariant);

    /// <summary>写一件物品。片段与图层是硬数据；徽章、内容物格、窗口等拿不到就不写（不编造）。</summary>
    internal static void WriteItem(JsonOut w, ItemCapture c)
    {
        var item = c.Item;
        var lines = Lines(c.Folded);
        var bars = c.Bars != null ? new List<ProgressBarStyle.BarRecord>(c.Bars) : null;
        foreach (var line in lines)
            foreach (var seg in line)
                seg.Bar = DetectBar(seg, bars);

        w.BeginObject();
        w.Prop("id").Value(SafeString(() => item.identifier));
        w.Prop("name").Value(SafeString(() => GameRead.ItemDisplayName(item)));
        w.Prop("source").Value(c.Source);
        if (c.Label != null) w.Prop("label").Value(c.Label);
        if (c.State != null) w.Prop("state").Value(c.State);
        w.Prop("maxCharWidth").Value(c.MaxCharWidth);
        if (c.BuildMaxCharWidth >= 0 && c.BuildMaxCharWidth != c.MaxCharWidth)
            w.Prop("buildMaxCharWidth").Value(c.BuildMaxCharWidth);

        w.Prop("lines").BeginArray();
        foreach (var line in lines) WriteLine(w, line);
        w.EndArray();

        if (c.Raw != null)
        {
            w.Prop("layers").BeginObject();
            w.Prop("naked").Value(c.Raw.nakedText);
            w.Prop("foreground").Value(c.Raw.richTextForeground);
            w.Prop("background").Value(c.Raw.richTextBackground);
            w.Prop("underline").Value(c.Raw.richTextUnderline);
            w.Prop("strikethrough").Value(c.Raw.richTextStrikethrough);
            w.Prop("recommendedWidth").Value(c.RawWidth >= 0 ? c.RawWidth : c.Raw.recommendedWidth);
            w.Prop("recommendedHeight").Value(c.Raw.recommendedHeight);
            w.EndObject();

            string recomposed = Recompose(c.Folded);
            w.Prop("check").BeginObject();
            w.Prop("segmentsMatchBuild").Value(string.Equals(recomposed, c.Raw.nakedText, StringComparison.Ordinal)
                                            && string.Equals(recomposed, c.Raw.richTextForeground, StringComparison.Ordinal));
            w.EndObject();
        }

        // Build 之后被本模组后缀改过的结果（公司名并入、框宽修正）。没改时不重复写。
        if (c.Final != null)
        {
            w.Prop("frame").BeginObject();
            w.Prop("recommendedWidth").Value(c.Final.recommendedWidth);
            if (c.Window != null)
            {
                w.Prop("windowMaxChars").Value(c.Window.MaxChars);
                w.Prop("columns").Value(Math.Min(c.Final.recommendedWidth, c.Window.MaxChars));
                if (c.Window.WidthPx >= 0) w.Prop("widthPx").Value(c.Window.WidthPx);
                if (c.Window.HeightPx >= 0) w.Prop("heightPx").Value(c.Window.HeightPx);
            }
            w.EndObject();
            if (c.Raw != null && !ReferenceEquals(c.Final, c.Raw)
                && !string.Equals(c.Final.richTextForeground, c.Raw.richTextForeground, StringComparison.Ordinal))
            {
                w.Prop("finalLayers").BeginObject();
                w.Prop("foreground").Value(c.Final.richTextForeground);
                w.Prop("background").Value(c.Final.richTextBackground);
                w.EndObject();
            }
        }

        if (c.Company != null)
        {
            w.Prop("manufacturer").BeginObject();
            w.Prop("name").Value(c.Company);
            int index = -1;
            for (int i = 0; i < lines.Count && index < 0; i++)
                if (string.Equals(PlainText(lines[i]).Trim(), c.Company.Trim(), StringComparison.Ordinal)) index = i;
            w.Prop("line").Value(index);
            bool merged = c.Final != null && c.Raw != null
                          && !string.Equals(c.Final.richTextForeground, c.Raw.richTextForeground, StringComparison.Ordinal);
            w.Prop("inline").Value(merged && index > 0);
            w.EndObject();
        }

        w.Prop("spriteBars").Value(c.SpriteBars);

        if (c.Badges != null)
        {
            w.Prop("badges").BeginArray();
            foreach (var b in c.Badges)
            {
                w.BeginObject();
                w.Prop("type").Value(b.Type);
                w.Prop("text").Value(b.Text);
                if (b.Background != null) w.Prop("background").Value(b.Background);
                if (b.Border != null) w.Prop("border").Value(b.Border);
                if (b.Warning) w.Prop("warning").Value(true);
                w.EndObject();
            }
            w.EndArray();
        }

        if (c.Contents != null)
        {
            w.Prop("contentsGrid").BeginObject();
            w.Prop("sealed").Value(c.Contents.Sealed);
            if (c.Contents.Estimate != null) w.Prop("estimate").Value(c.Contents.Estimate);
            w.Prop("total").Value(c.Contents.Total);
            w.Prop("overflow").Value(c.Contents.Overflow);
            w.Prop("cells").BeginArray();
            foreach (var cell in c.Contents.Cells)
            {
                w.BeginObject();
                w.Prop("id").Value(cell.Id);
                w.Prop("name").Value(cell.Name);
                w.Prop("count").Value(cell.Count);
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
        }

        if (c.Window != null)
        {
            w.Prop("window").BeginObject();
            if (c.Window.Text != null) w.Prop("text").Value(c.Window.Text);
            if (c.Window.BadgesShown.HasValue) w.Prop("badgesShown").Value(c.Window.BadgesShown.Value);
            if (c.Window.Error != null) w.Prop("error").Value(c.Window.Error);
            w.EndObject();
        }

        w.Prop("types").BeginArray();
        List<string>? types = null;
        try { types = item.GetGameItemType(); }
        catch (Exception ex) { c.Notes.Add("读物品分类失败：" + ex.GetType().Name); }
        if (types != null)
            foreach (string type in types) w.Value(type);
        w.EndArray();
        w.Prop("tags");
        WriteTags(w, item);
        w.Prop("inventories");
        WriteInventories(w, item, 0, new Budget());

        if (c.WriteCalls != null)
        {
            if (c.RootBuilder.HasValue) w.Prop("rootBuilder").Value(c.RootBuilder.Value);
            w.Prop("calls");
            c.WriteCalls(w);
        }

        if (c.Notes.Count > 0)
        {
            w.Prop("notes").BeginArray();
            foreach (string note in c.Notes) w.Value(note);
            w.EndArray();
        }
        w.EndObject();
    }

    private static void WriteLine(JsonOut w, List<Segment> line)
    {
        w.BeginObject();
        // AddEmptyLine 是 NextLine + 一片 <size=Npx> </size>，后面的文字接在同一行：记在行首，不单独成行。
        if (line.Count >= 1)
        {
            var m = Spacer.Match(line[0].Text);
            if (m.Success) w.Prop("spacer").Value(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        w.Prop("segments").BeginArray();
        foreach (var s in line)
        {
            w.BeginObject();
            w.Prop("text").Value(s.Text);
            w.Prop("len").Value(s.Length);
            if (s.Color != RenderHandler.ColorPalette.None)
            {
                w.Prop("color").Value(Hex(s.Color));
                string? name = Enum.GetName(typeof(RenderHandler.ColorPalette), s.Color);
                if (name != null) w.Prop("colorName").Value(name);
            }
            if (s.Bold) w.Prop("bold").Value(true);
            if (s.Italic) w.Prop("italic").Value(true);
            if (s.Underline) w.Prop("underline").Value(true);
            if (s.Strike) w.Prop("strike").Value(true);
            if (s.Background != RenderHandler.ColorPalette.None) w.Prop("background").Value(Hex(s.Background));
            if (s.NoBreak) w.Prop("noBreak").Value(true);
            if (s.Bar != null)
            {
                w.Prop("bar").BeginObject();
                w.Prop("length").Value(s.Bar.Length);
                w.Prop("fill").Value(s.Bar.Fill);
                if (s.Bar.Ratio.HasValue) w.Prop("ratio").Value(s.Bar.Ratio.Value);
                if (s.Bar.Dark != RenderHandler.ColorPalette.None) w.Prop("dark").Value(Hex(s.Bar.Dark));
                if (!s.Bar.FullLength) w.Prop("fullLength").Value(false);
                w.Prop("recorded").Value(s.Bar.Recorded);
                w.EndObject();
            }
            w.EndObject();
        }
        w.EndArray();
        w.EndObject();
    }

    /// <summary>
    /// 原版 AddProgressBar 写出的片段：不折行、文字是 <c>[==   ]</c>。比例与底色按渲染层同一规则
    /// （长度相同且格数能由记录的比例算出）从本模组的记录里认领；认领不到只写格数。
    /// </summary>
    private static BarInfo? DetectBar(Segment s, List<ProgressBarStyle.BarRecord>? records)
    {
        if (!s.NoBreak) return null;
        var m = BarText.Match(s.Text);
        if (!m.Success) return null;
        int fill = m.Groups[1].Value.Length, empty = m.Groups[2].Value.Length;
        var bar = new BarInfo { Length = fill + empty, Fill = fill };
        if (bar.Length <= 0) return null;
        if (records != null)
        {
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                if (r.Length != bar.Length || !ProgressFill.Accepts(r.Ratio, r.Length, fill)) continue;
                records.RemoveAt(i);
                bar.Ratio = r.Ratio;
                bar.Dark = r.Dark;
                bar.FullLength = r.FullLength;
                bar.Recorded = true;
                break;
            }
        }
        return bar;
    }

    internal static string PlainText(List<Segment> line)
    {
        var sb = new StringBuilder();
        foreach (var s in line) sb.Append(s.Text);
        return sb.ToString();
    }

    internal static string Hex(RenderHandler.ColorPalette color) => "#" + ((int)color).ToString("x6", CultureInfo.InvariantCulture);

    internal static string Hex(UnityEngine.Color color)
    {
        UnityEngine.Color32 c = color;
        return "#" + c.r.ToString("x2", CultureInfo.InvariantCulture) + c.g.ToString("x2", CultureInfo.InvariantCulture)
               + c.b.ToString("x2", CultureInfo.InvariantCulture);
    }

    private static string SafeString(Func<string?> read)
    {
        try { return read() ?? ""; }
        catch (Exception ex) { return "?" + ex.GetType().Name; }
    }

    // ---------------- 标签与内容物树（供以后离线重放） ----------------

    /// <summary>标签快照：每个键一条，值只写非默认的分量。</summary>
    internal static void WriteTags(JsonOut w, GameItem item)
    {
        w.BeginArray();
        var dict = item.modifiedState?.dict;
        if (dict != null)
        {
            var keys = new List<string>(dict.Keys);
            keys.Sort(string.CompareOrdinal);
            foreach (string key in keys)
            {
                var tag = dict[key];
                if (tag == null) continue;
                w.BeginObject();
                w.Prop("k").Value(key);
                if (tag.valueEnabled) w.Prop("on").Value(true);
                if (tag.valueInt != 0) w.Prop("i").Value(tag.valueInt);
                if (tag.valueFloat != 0f) w.Prop("f").Value(tag.valueFloat);
                if (tag.valueLong != 0L) w.Prop("l").Value(tag.valueLong);
                if (tag.valueDouble != 0d) w.Prop("d").Value(tag.valueDouble);
                if (tag.valueBool) w.Prop("b").Value(true);
                string? s = tag.valueString;
                if (!string.IsNullOrEmpty(s)) w.Prop("s").Value(s);
                w.EndObject();
            }
        }
        w.EndArray();
    }

    /// <summary>去重用的标签签名：键与各分量的规范串。</summary>
    internal static string Signature(GameItem item)
    {
        var sb = new StringBuilder(item.identifier ?? "");
        var dict = item.modifiedState?.dict;
        if (dict == null) return sb.ToString();
        var keys = new List<string>(dict.Keys);
        keys.Sort(string.CompareOrdinal);
        foreach (string key in keys)
        {
            var t = dict[key];
            if (t == null) continue;
            sb.Append('|').Append(key).Append(t.valueEnabled ? '+' : '-')
              .Append(t.valueInt.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(t.valueFloat.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(t.valueLong.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(t.valueDouble.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(t.valueBool ? '1' : '0').Append(',').Append(t.valueString);
        }
        return sb.ToString();
    }

    internal sealed class Budget
    {
        internal int Items = 400;
    }

    /// <summary>物品下的全部库存（含扩建储藏区的每个标签页）；读不到时返回空表。</summary>
    internal static List<GameInventory> Inventories(GameItem item)
    {
        var result = new List<GameInventory>();
        var seen = new HashSet<GameInventory>();
        try
        {
            foreach (var inv in GraphUtils.FindChildrenType<GameInventory>(item))
                if (inv != null && seen.Add(inv)) result.Add(inv);
        }
        catch (Exception ex) { Log.Warn("TooltipSnapshot.Inventories", ex); }
        try
        {
            var grids = StorageInfo.GridStorage(item);
            if (grids != null)
                foreach (var grid in grids)
                    if (grid != null && seen.Add(grid)) result.Add(grid);
        }
        catch (Exception ex) { Log.Warn("TooltipSnapshot.GridStorage", ex); }
        return result;
    }

    /// <summary>嵌套库存树：每层写库存类型与其中物品（标识、名字、标签、下一层）。深度与总件数有上限。</summary>
    internal static void WriteInventories(JsonOut w, GameItem item, int depth, Budget budget)
    {
        w.BeginArray();
        if (depth < 4)
        {
            foreach (var inv in Inventories(item))
            {
                List<GameItem>? children = null;
                try { children = inv.childItems; }
                catch (Exception ex) { Log.Warn("TooltipSnapshot.ChildItems", ex); }
                w.BeginObject();
                w.Prop("kind").Value(inv.GetType().Name);
                w.Prop("items").BeginArray();
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        if (child == null) continue;
                        if (budget.Items-- <= 0) break;
                        w.BeginObject();
                        w.Prop("id").Value(SafeString(() => child.identifier));
                        w.Prop("name").Value(SafeString(() => GameRead.ItemDisplayName(child)));
                        w.Prop("tags");
                        WriteTags(w, child);
                        w.Prop("inventories");
                        WriteInventories(w, child, depth + 1, budget);
                        w.EndObject();
                    }
                }
                w.EndArray();
                if (budget.Items < 0) w.Prop("truncated").Value(true);
                w.EndObject();
            }
        }
        w.EndArray();
    }

    // ---------------- 文档 ----------------

    internal sealed class Meta
    {
        internal string Source = "ingame";
        internal string GameVersion = "";
        internal string? AssemblyVersionId;
        internal string ModVersion = "";
        internal string Language = "";
        internal float? CharWidthScale;
        internal int? EffectiveFontWidth;
        internal string? ExportedAt;
        internal int? SaveSlot;
        internal readonly List<(string Key, bool Value)> Settings = new();
        internal readonly List<(string Key, string Value)> Extra = new();
    }

    internal static void WriteHeader(JsonOut w, Meta meta)
    {
        w.Prop("schema").Value(Schema);
        w.Prop("meta").BeginObject();
        w.Prop("source").Value(meta.Source);
        w.Prop("gameVersion").Value(meta.GameVersion);
        if (meta.AssemblyVersionId != null) w.Prop("assemblyMvid").Value(meta.AssemblyVersionId);
        w.Prop("modVersion").Value(meta.ModVersion);
        w.Prop("language").Value(meta.Language);
        if (meta.CharWidthScale.HasValue) w.Prop("charWidthScale").Value(meta.CharWidthScale.Value);
        if (meta.EffectiveFontWidth.HasValue) w.Prop("effectiveFontWidth").Value(meta.EffectiveFontWidth.Value);
        if (meta.ExportedAt != null) w.Prop("exportedAt").Value(meta.ExportedAt);
        if (meta.SaveSlot.HasValue) w.Prop("saveSlot").Value(meta.SaveSlot.Value);
        w.Prop("settings").BeginObject();
        foreach (var (key, value) in meta.Settings) w.Prop(key).Value(value);
        w.EndObject();
        foreach (var (key, value) in meta.Extra) w.Prop(key).Value(value);
        w.EndObject();
    }

    internal static string AssemblyMvid()
    {
        try { return typeof(RichTextBuilder).Assembly.ManifestModule.ModuleVersionId.ToString(); }
        catch (Exception ex) { return "?" + ex.GetType().Name; }
    }

    // ---------------- 离线入口（测试用改写过的游戏程序集副本调用） ----------------

    /// <summary>离线夹具里由副本 GetString 开头调用的钩子字段名；游戏本体没有这个字段。</summary>
    internal const string OfflineHookField = "ItemDetailsSnapshotHook";

    /// <summary>
    /// 离线：对一个已写好的 builder 跑一次原版 Build，录下 GetString 依次消费的片段，序列化成一条物品记录。
    /// 需要测试先加载静态改写过的 Assembly-CSharp 副本（带 <see cref="OfflineHookField"/>）。
    /// </summary>
    internal static string OfflineItem(RichTextBuilder builder, GameItem item, string label)
    {
        string? error = Bind();
        if (error != null) throw new InvalidOperationException(error);
        var hook = typeof(RichTextBuilder).GetField(OfflineHookField, BindingFlags.Public | BindingFlags.Static)
                   ?? throw new InvalidOperationException("当前 Assembly-CSharp 没有离线钩子字段 " + OfflineHookField);
        int width = builder.maxCharWidth;
        var frame = TooltipSnapshotRecorder.BeginFrame(builder);
        RichText built;
        hook.SetValue(null, new Action<object>(TooltipSnapshotRecorder.OnSlice));
        try { built = builder.Build(); }
        finally
        {
            hook.SetValue(null, null);
            TooltipSnapshotRecorder.EndFrame(frame);
        }
        var capture = new ItemCapture
        {
            Item = item,
            Source = "offline-fixture",
            Label = label,
            Folded = frame.Folded,
            Raw = built,
            Final = built,
            RawWidth = built.recommendedWidth,
            MaxCharWidth = width,
            BuildMaxCharWidth = width,
        };
        capture.Notes.Add("离线夹具：未经过本模组的 Harmony 渲染层（Build 前后缀、SetRichText），是边界 1 的规范输出");
        var w = new JsonOut();
        WriteItem(w, capture);
        return w.ToString();
    }

    /// <summary>离线：把若干条物品记录包成一份快照文档。</summary>
    internal static string OfflineDocument(string[] items, string gameVersion, string language)
    {
        var meta = new Meta { Source = "offline-fixture", GameVersion = gameVersion, Language = language, ModVersion = "" };
        meta.AssemblyVersionId = AssemblyMvid();
        var w = new JsonOut();
        w.BeginObject();
        WriteHeader(w, meta);
        w.Prop("items").BeginArray();
        foreach (string item in items) w.Raw(item);
        w.EndArray();
        w.EndObject();
        return w.ToString();
    }
}

/// <summary>极简 JSON 写出器：只支持快照用到的形状，自己维护逗号。数组元素各占一行，便于按行比对。</summary>
internal sealed class JsonOut
{
    private readonly StringBuilder _sb = new();
    private readonly List<bool> _first = new();
    private bool _afterProp;

    public override string ToString() => _sb.ToString();

    private void Separator()
    {
        if (_afterProp) { _afterProp = false; return; }
        int top = _first.Count - 1;
        if (top < 0) return;
        if (!_first[top]) _sb.Append(',');
        _first[top] = false;
        if (top == 1 || top == 2) _sb.Append('\n');
    }

    internal JsonOut BeginObject() { Separator(); _sb.Append('{'); _first.Add(true); return this; }
    internal JsonOut EndObject() { _first.RemoveAt(_first.Count - 1); _sb.Append('}'); return this; }
    internal JsonOut BeginArray() { Separator(); _sb.Append('['); _first.Add(true); return this; }
    internal JsonOut EndArray() { _first.RemoveAt(_first.Count - 1); _sb.Append(']'); return this; }

    internal JsonOut Prop(string name)
    {
        int top = _first.Count - 1;
        if (!_first[top]) _sb.Append(',');
        _first[top] = false;
        String(name);
        _sb.Append(':');
        _afterProp = true;
        return this;
    }

    /// <summary>直接写入一段已是 JSON 的值（离线拼文档用）。</summary>
    internal JsonOut Raw(string json) { Separator(); _sb.Append(json); return this; }

    internal JsonOut Value(string? s)
    {
        Separator();
        if (s == null) _sb.Append("null"); else String(s);
        return this;
    }

    internal JsonOut Value(bool b) { Separator(); _sb.Append(b ? "true" : "false"); return this; }
    internal JsonOut Value(int n) { Separator(); _sb.Append(n.ToString(CultureInfo.InvariantCulture)); return this; }
    internal JsonOut Value(long n) { Separator(); _sb.Append(n.ToString(CultureInfo.InvariantCulture)); return this; }

    internal JsonOut Value(double d)
    {
        Separator();
        if (double.IsNaN(d) || double.IsInfinity(d)) _sb.Append("null");
        else _sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        return this;
    }

    internal JsonOut Value(float f)
    {
        Separator();
        if (float.IsNaN(f) || float.IsInfinity(f)) _sb.Append("null");
        else _sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
        return this;
    }

    private void String(string s)
    {
        _sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': _sb.Append("\\\""); break;
                case '\\': _sb.Append("\\\\"); break;
                case '\n': _sb.Append("\\n"); break;
                case '\r': _sb.Append("\\r"); break;
                case '\t': _sb.Append("\\t"); break;
                default:
                    if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else _sb.Append(c);
                    break;
            }
        }
        _sb.Append('"');
    }
}
#endif
