using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using ItemDetails.Integration;
using Color = RenderHandler.ColorPalette;
using Run = (string Kind, string Text, RenderHandler.ColorPalette Color, float Ratio, int Length, RenderHandler.ColorPalette Background, string Tail);
using Runs = System.Collections.Generic.IReadOnlyList<(string Kind, string Text, RenderHandler.ColorPalette Color, float Ratio, int Length, RenderHandler.ColorPalette Background, string Tail)>;
using Display = System.Func<GameItem, RichTextBuilder, string, System.Collections.Generic.IReadOnlyList<(string Kind, string Text, RenderHandler.ColorPalette Color, float Ratio, int Length, RenderHandler.ColorPalette Background, string Tail)>, System.Collections.Generic.IReadOnlyDictionary<string, object>, bool>;
using Fields = System.Collections.Generic.IReadOnlyDictionary<string, object>;

namespace ItemDetails;

internal static class TooltipSections
{
    private sealed class Frame
    {
        internal int Build;
        internal readonly List<TooltipSection> Sections = new();
    }

    private static readonly ConditionalWeakTable<RichTextBuilder, Frame> Frames = new();
    private static readonly Func<RichTextBuilder, Action<string, Runs, Fields>, bool> Query = Read;
    private static readonly Fields EmptyFields = new ReadOnlyDictionary<string, object>(new Dictionary<string, object>());

    internal static void Enable() => AppDomain.CurrentDomain.SetData(ItemDetailsClient.SectionQuery, Query);

    internal static void Disable()
    {
        if (ReferenceEquals(AppDomain.CurrentDomain.GetData(ItemDetailsClient.SectionQuery), Query))
            AppDomain.CurrentDomain.SetData(ItemDetailsClient.SectionQuery, null);
        EndTooltip();
        Frames.Clear();
    }

    internal static void EndTooltip()
    {
        listenersBuild = -1;
        listeners = default;
    }

    // For a native text slice shared with another game helper (e.g. the label prefix).
    internal static void ObserveText(RichTextBuilder builder, GameItem item, string id, string text, Color color,
        string? group = null)
    {
        var output = new Writer(builder, item, id, group);
        if (!output.Capturing) return;
        output.AddLine(text, color);
        output.Observe();
    }

    private static bool Read(RichTextBuilder builder, Action<string, Runs, Fields> emit)
    {
        if (builder == null || !Frames.TryGetValue(builder, out var frame) || frame.Sections.Count == 0) return false;
        foreach (var section in frame.Sections) emit(section.Id, section.Runs, section.Fields);
        return true;
    }

    private static int listenersBuild = -1;
    private static (Display? Displays, bool Watch) listeners;

    private static (Display? Displays, bool Watch) Listeners()
    {
        bool building = TooltipPipeline.IsBuilding;
        if (building && listenersBuild == TooltipPipeline.BuildId) return listeners;
        var domain = AppDomain.CurrentDomain;
        var result = (domain.GetData(ItemDetailsClient.SectionDisplay) as Display,
                      domain.GetData(ItemDetailsClient.SectionWatch) is Action);
        if (building)
        {
            listeners = result;
            listenersBuild = TooltipPipeline.BuildId;
        }
        return result;
    }

    // Outside integration mode writes go straight to the original game builder.
    internal readonly struct Writer
    {
        internal readonly RichTextBuilder Native;
        private readonly Buffer? buffer;
        internal bool Capturing => buffer != null;

        /// <summary>当前 tooltip 的折行预算（字符数）；原版按物品给 40/60/80。</summary>
        internal int MaxCharWidth => Native.maxCharWidth;

        internal Writer(RichTextBuilder builder, GameItem item, string id) : this(builder, item, id, null) { }

        internal Writer(RichTextBuilder builder, GameItem item, string id, string? group)
        {
            Native = builder;
            var (displays, watch) = Listeners();
            buffer = displays != null || watch ? new Buffer(item, id, displays, watch) : null;
            // 归属词表随每个区块一起发出去：读取方不必为每个新 id 补一条映射，认不出的组按原样保留。
            if (buffer != null && group != null) Field("group", group);
        }

        internal void Field<T>(string key, T value) where T : notnull
        {
            if (buffer != null) (buffer.Fields ??= new Dictionary<string, object>())[key] = value;
        }

        internal void Add(string text, Color color) => Write(("text", text, color, 0, 0, default, ""));
        internal void Add(string text, Color color, string end)
        {
            // end 是下次 Add 前插入的续行前缀（nextBegin）；不记录就无法逐字回放。
            Write(("text", text, color, 0, 0, default, end));
        }
        internal void AddLine(string text, Color color) => Write(("line", text, color, 0, 0, default, ""));
        internal void NextLine() => Write(("break", "", default, 0, 0, default, ""));
        internal void Bar(TooltipLine line) => Write(("bar", line.Text, line.Color, line.Bar, line.BarLength, line.BarDark, line.Tail ?? ""));
        internal void InlineBar(TooltipLine line) => Write(("inline-bar", line.Text, line.Color, line.Bar, line.BarLength, line.BarDark, line.Tail ?? ""));

        private void Write(Run run)
        {
            if (buffer == null) Replay(Native, run);
            else (buffer.Runs ??= new List<Run>()).Add(run);
        }

#if ITEMDETAILS_DIAG
        internal bool Buffered => buffer != null;
#endif

        // 原地改写行专用：方法自己决定怎么写出这一行。
        // 无观察者时 Add/NextLine 已经直写；有观察者时它们只进缓冲，Emit 负责回放一次。
        internal void Direct(string text, Color color) => Write(("text", text, color, 0, 0, default, ""));
        internal void Direct(TooltipLine line) => InlineBar(line);
        internal void DirectNextLine() => NextLine();



        internal void RequireWidth(int width)
        {
            if (buffer == null) Native.maxCharWidth = Math.Max(width, Native.maxCharWidth);
            else buffer.Width = Math.Max(width, buffer.Width);
        }

        // A native method owns this line or overlay. Expose its data without changing its rendering.
        internal void Observe()
        {
            if (buffer == null) return;
            Finish(observeOnly: true);
        }

        // 原地改写行的收尾。无观察者时上面的 Add/NextLine 已经直写；有观察者时只进了缓冲，
        // 由 Finish 决定：接管方认领就由它画，没人认领就原样回放——输出与无观察者时逐字一致。
        // 不要在这里设 Handled：那会走 observeOnly 分支，在回放之前就返回，缓冲里的内容会整段消失。
        internal void Emit()
        {
            if (buffer == null) return;
            Finish();
        }


        // true means a replacement actually claimed the section (also used by the contents overlay).
        internal bool Finish(bool observeOnly = false)
        {
            if (buffer == null) return false;
            Runs runs = buffer.Runs == null ? Array.Empty<Run>() : buffer.Runs.AsReadOnly();
            var frame = buffer.Watch ? Frames.GetOrCreateValue(Native) : null;
            if (frame != null && frame.Build != TooltipPipeline.BuildId)
            {
                frame.Sections.Clear();
                frame.Build = TooltipPipeline.BuildId;
            }
            if (runs.Count == 0 && buffer.Fields == null) return false;
            if (observeOnly) Field("canReplace", false);
            Fields fields = buffer.Fields == null ? EmptyFields : new ReadOnlyDictionary<string, object>(buffer.Fields);
            frame?.Sections.Add(new TooltipSection(buffer.Id, runs, fields));
            bool claimed = false;
            if (buffer.Displays != null && !observeOnly)
            {
                foreach (Display prepare in buffer.Displays.GetInvocationList())
                {
                    try { if (prepare(buffer.Item, Native, buffer.Id, runs, fields)) { claimed = true; break; } }
                    catch (Exception ex) { Log.Warn("TooltipDisplay." + buffer.Id, ex); }
                }
            }
            if (observeOnly) return false;
            if (claimed) return true;
            if (buffer.Width > 0) Native.maxCharWidth = Math.Max(buffer.Width, Native.maxCharWidth);
            foreach (var run in runs) Replay(Native, run);
            return false;
        }

        private static void Replay(RichTextBuilder builder, Run run)
        {
            switch (run.Kind)
            {
                case "text": builder.Add(run.Text, run.Color, run.Tail); break;
                case "line": ModEntry.AddLine(builder, run.Text, run.Color); break;
                case "break": builder.NextLine(); break;
                case "bar": ModEntry.AddBarLine(builder, new TooltipLine(run.Text, run.Ratio, run.Length,
                    run.Tail, run.Color, run.Background)); break;
                case "inline-bar": ModEntry.AddInlineBarLine(builder, new TooltipLine(run.Text, run.Ratio, run.Length,
                    run.Tail, run.Color, run.Background)); break;
            }
        }
    }

    private sealed class Buffer
    {
        internal readonly GameItem Item;
        internal readonly string Id;
        internal readonly Display? Displays;
        internal readonly bool Watch;
        internal int Width;
        internal List<Run>? Runs;
        internal Dictionary<string, object>? Fields;

        internal Buffer(GameItem item, string id, Display? displays, bool watch)
        {
            Item = item;
            Id = id;
            Displays = displays;
            Watch = watch;
        }
    }
}
