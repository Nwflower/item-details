// Item Details 区块协议 v1 —— C# 5 客户端
//
// 给只支持旧语言版本的工程（例如 SectionedTooltips 用的 C# 5 + 游戏自带的 csc）复制使用。
// 与 sdk/ItemDetailsClient.cs 是同一份协议：槽位名、字段名、区块 id 与 group 词表都只以这里为准，
// 两边不一致时以本文件为准，主客户端跟随。
//
// 规则：
//   - 物品详情没装、没启用或版本不符时，每个入口都安静返回 false / null，照常走原版显示；
//   - 只增不改：新增字段与区块 id 时不要改已有名字；
//   - 全部调用都在游戏主线程。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ItemDetails.Integration
{
    // 线上的真实类型：物品详情发布的委托签名里，Runs 是这个 ValueTuple 列表。
    // 槽位按委托的运行时类型匹配，换成自定义结构体就永远对不上，所以签名必须逐字一致；
    // TooltipRun 只是给调用方的易读视图，在本文件内转换。
    using WireRun = ValueTuple<string, string, RenderHandler.ColorPalette, float, int, RenderHandler.ColorPalette, string>;
    using WireRuns = IReadOnlyList<ValueTuple<string, string, RenderHandler.ColorPalette, float, int, RenderHandler.ColorPalette, string>>;
    using Fields = IReadOnlyDictionary<string, object>;

    /// <summary>区块里的一段文字或一条进度条。</summary>
    public struct TooltipRun
    {
        public string Kind;                      // text / line / break / bar / inline-bar
        public string Text;
        public RenderHandler.ColorPalette Color;
        public float Ratio;
        public int Length;
        public RenderHandler.ColorPalette Background;
        public string Tail;
    }

    /// <summary>一次构建里的一个信息块。</summary>
    public sealed class TooltipSection
    {
        private readonly IReadOnlyDictionary<string, object> fields;

        private readonly WireRuns wire;
        private IReadOnlyList<TooltipRun> runs;

        public string Id { get; private set; }

        /// <summary>这一块的文字与进度条，按输出顺序。第一次读取时才从线上格式转换。</summary>
        public IReadOnlyList<TooltipRun> Runs
        {
            get
            {
                if (runs == null) runs = Convert(wire);
                return runs;
            }
        }

        internal TooltipSection(string id, WireRuns wireRuns, Fields values)
        {
            Id = id;
            wire = wireRuns;
            fields = values;
        }

        /// <summary>false 表示这一块是原生行的展示，只能读，不能接管后删掉原行。</summary>
        public bool CanReplace
        {
            get
            {
                bool replace;
                return !TryGet<bool>("canReplace", out replace) || replace;
            }
        }

        private static IReadOnlyList<TooltipRun> Convert(WireRuns source)
        {
            var list = new List<TooltipRun>(source == null ? 0 : source.Count);
            if (source == null) return list.AsReadOnly();
            for (int i = 0; i < source.Count; i++)
            {
                WireRun r = source[i];
                TooltipRun run;
                run.Kind = r.Item1;
                run.Text = r.Item2;
                run.Color = r.Item3;
                run.Ratio = r.Item4;
                run.Length = r.Item5;
                run.Background = r.Item6;
                run.Tail = r.Item7;
                list.Add(run);
            }
            return list.AsReadOnly();
        }

        /// <summary>这一块在玩家眼里属于哪一类信息；读不到返回空串。</summary>
        public string Group
        {
            get
            {
                if (fields == null) return "";
                object value;
                return fields.TryGetValue("group", out value) ? (value as string) ?? "" : "";
            }
        }

        public bool TryGet<T>(string name, out T value)
        {
            value = default(T);
            if (fields == null) return false;
            object raw;
            if (!fields.TryGetValue(name, out raw) || !(raw is T)) return false;
            value = (T)raw;
            return true;
        }

        /// <summary>“（另 N 项低于显示阈值）”那一行的整行文本；没有隐藏成分时返回 false。</summary>
        public bool TryGetTrace(out string text)
        {
            return TryGet("trace", out text);
        }
    }

    public static class ItemDetailsClient
    {
        private const string SectionDisplay = "item_details.tooltip_sections.v1.display";
        private const string SectionQuery = "item_details.tooltip_sections.v1.read";
        private const string SectionWatch = "item_details.tooltip_sections.v1.watch";
        private const string LayoutSlot = "item_details.tooltip_layout.v1";
        private const string AnchorSlot = "item_details.tooltip_anchor.v1";
        private const string BadgeSlot = "item_details.tooltip_badge.v1";

        /// <summary>物品详情已启用区块协议。</summary>
        public static bool SectionProtocolAvailable
        {
            get { return AppDomain.CurrentDomain.GetData(SectionQuery) != null; }
        }

        /// <summary>开始捕获区块之前装上；不装就只能读别人的快照。</summary>
        public static IDisposable WatchTooltipSections()
        {
            return new Registration(SectionWatch, (Action)delegate { });
        }

        /// <summary>
        /// 接管自己画得出的区块：返回 true 表示这次已经画好，原版不再输出这一段。
        /// 返回 false 或抛异常都算不接管。
        /// </summary>
        public static IDisposable RegisterTooltipSections(Func<GameItem, RichTextBuilder, TooltipSection, bool> prepare)
        {
            if (prepare == null) throw new ArgumentNullException("prepare");
            Func<GameItem, RichTextBuilder, string, WireRuns, Fields, bool> callback =
                delegate(GameItem item, RichTextBuilder builder, string id, WireRuns runs, Fields fields)
                {
                    return prepare(item, builder, new TooltipSection(id, runs, fields));
                };
            return new Registration(SectionDisplay, callback);
        }

        /// <summary>在原版钩子里按 builder 读整份区块快照。</summary>
        public static bool TryGetTooltipSections(RichTextBuilder builder, out IReadOnlyList<TooltipSection> sections)
        {
            sections = new TooltipSection[0];
            if (builder == null) return false;
            object raw = AppDomain.CurrentDomain.GetData(SectionQuery);
            var read = raw as Func<RichTextBuilder, Action<string, WireRuns, Fields>, bool>;
            if (read == null) return false;
            var result = new List<TooltipSection>();
            try
            {
                Action<string, WireRuns, Fields> emit =
                    delegate(string id, WireRuns runs, Fields fields)
                    {
                        result.Add(new TooltipSection(id, runs, fields));
                    };
                if (!read(builder, emit)) return false;
                sections = result.AsReadOnly();
                return true;
            }
            catch { return false; }
        }

        /// <summary>逐件声明自己怎么画；返回 null 表示这件物品不表态。</summary>
        public static IDisposable RegisterTooltipLayout(Func<GameItem, TooltipLayoutOptions?> describe)
        {
            if (describe == null) throw new ArgumentNullException("describe");
            Func<GameItem, int?> callback = delegate(GameItem item)
            {
                TooltipLayoutOptions? options = describe(item);
                return options.HasValue ? (int?)options.Value : null;
            };
            return new Registration(LayoutSlot, callback);
        }

        /// <summary>当前可见提示框的根；没显示时返回 null。</summary>
        public static IDisposable RegisterTooltipAnchor(Func<RectTransform> anchor)
        {
            if (anchor == null) throw new ArgumentNullException("anchor");
            return new Registration(AnchorSlot, anchor);
        }

        /// <summary>当前可见的分类徽章容器；没显示时返回 null。排版方据此量高度，不必按节点名找。</summary>
        public static bool TryGetTooltipBadge(out RectTransform badge)
        {
            badge = null;
            object raw = AppDomain.CurrentDomain.GetData(BadgeSlot);
            if (!(raw is Func<RectTransform>)) return false;
            var providers = (Func<RectTransform>)raw;
            Delegate[] list = providers.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    RectTransform rect = ((Func<RectTransform>)list[i])();
                    if (rect != null) { badge = rect; return true; }
                }
                catch { }
            }
            return false;
        }

        private sealed class Registration : IDisposable
        {
            private readonly string slot;
            private Delegate callback;

            internal Registration(string slot, Delegate callback)
            {
                this.slot = slot;
                this.callback = callback;
                AppDomain domain = AppDomain.CurrentDomain;
                domain.SetData(slot, Delegate.Combine(domain.GetData(slot) as Delegate, callback));
            }

            public void Dispose()
            {
                if (callback == null) return;
                AppDomain domain = AppDomain.CurrentDomain;
                domain.SetData(slot, Delegate.Remove(domain.GetData(slot) as Delegate, callback));
                callback = null;
            }
        }
    }

    /// <summary>排版方逐件声明的绘制能力。</summary>
    [Flags]
    public enum TooltipLayoutOptions
    {
        None = 0,
        NativeProgressBars = 1,
        FlowingText = 2,
        DrawsContents = 4,
    }

    /// <summary>区块 Id。只增不改。</summary>
    public static class TooltipSectionIds
    {
        public const string Authenticity = "auth";
        public const string Appraisal = "value";
        public const string InjectedAppraisal = "injected-value";
        public const string RibwichReasons = "ribwich";
        public const string Berry = "berry";
        public const string Hidden = "hidden";
        public const string ValueEnhance = "value-enhance";
        public const string FeatureReveal = "feature-reveal";
        public const string Storage = "storage";
        public const string MachineInterior = "machine-interior";
        public const string ModuleLearning = "module-learning";
        public const string Conditions = "conditions";
        public const string Contraband = "contraband";
        public const string Lottery = "lottery";
        public const string Liquid = "water";
        public const string SealedBox = "sealed-box";
        public const string Scratcher = "scratcher";
        public const string Seed = "seed";
        public const string WaterAdvice = "water-advice";
        public const string Wine = "wine";
        public const string Meat = "meat";
        public const string Market = "market";
        public const string Price = "price";
        public const string Calories = "calories";
        public const string MachineSpeed = "machine-speed";
        public const string MachinePower = "machine-power";
        public const string MachineEffect = "machine-effect";
        public const string Categories = "categories";
        public const string Contents = "contents";
        public const string SealedContents = "sealed-contents";
        public const string Label = "label";
        public const string Manufacturer = "manufacturer";
        public const string InspectHint = "inspect-hint";
    }

    /// <summary>`group` 字段的固定词表。只增不改；认不出的组按原样保留该行。</summary>
    public static class TooltipGroupIds
    {
        public const string Liquid = "liquid";
        public const string Plant = "plant";
        public const string Machine = "machine";
        public const string Condition = "condition";
        public const string Appraisal = "appraisal";
        public const string Food = "food";
        public const string Lottery = "lottery";
        public const string Brew = "brew";
        public const string Module = "module";
        public const string Label = "label";
        public const string Description = "description";
        public const string Price = "price";
        public const string Contents = "contents";
    }
}
