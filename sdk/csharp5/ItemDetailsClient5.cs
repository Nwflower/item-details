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

        public string Id { get; private set; }
        public IReadOnlyList<TooltipRun> Runs { get; private set; }

        public TooltipSection(string id, IReadOnlyList<TooltipRun> runs, IReadOnlyDictionary<string, object> values)
        {
            Id = id;
            Runs = runs;
            fields = values;
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
            Func<GameItem, RichTextBuilder, string, IReadOnlyList<TooltipRun>, IReadOnlyDictionary<string, object>, bool> callback =
                delegate(GameItem item, RichTextBuilder builder, string id, IReadOnlyList<TooltipRun> runs,
                         IReadOnlyDictionary<string, object> fields)
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
            if (!(raw is Func<RichTextBuilder, Action<string, IReadOnlyList<TooltipRun>, IReadOnlyDictionary<string, object>>, bool>))
                return false;
            var read = (Func<RichTextBuilder, Action<string, IReadOnlyList<TooltipRun>, IReadOnlyDictionary<string, object>>, bool>)raw;
            var result = new List<TooltipSection>();
            try
            {
                Action<string, IReadOnlyList<TooltipRun>, IReadOnlyDictionary<string, object>> emit =
                    delegate(string id, IReadOnlyList<TooltipRun> runs, IReadOnlyDictionary<string, object> fields)
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
