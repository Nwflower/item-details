using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ItemDetails;

/// <summary>
/// 官方 ModConfig 配置。游戏模组菜单会自动出现"设置"按钮并渲染面板
/// （ModConfig.HasSettings 为真 → ModConfigWindow.Open），无需自建 UI。
///
/// 分组与粒度原则（2026-10 整理）：
///   "显示内容"   —— tooltip 里补充的客观信息，同一件事只留一个开关
///   "透视"       —— 揭示本应靠鉴定/分析才能知道的东西（会降低难度）
///   "交互与外观" —— 操作方式与视觉权重
///
/// 真伪、价值构成和植物信息共用 show_item_appraisal；性价比与容器内容
/// 分别使用 show_cost_performance 和 show_container_contents。
/// 设置按下方 Define 的键读取，不读取已废弃的旧键。
///
/// 实现注意：
///   - Define 的 label/tooltip 是 Func&lt;string&gt;，游戏渲染面板时才调用，所以条目文案
///     会跟随当前语言。
///   - **分组标题不是** Func：ModConfig.Define 把 section 直接存进 ConfigEntry.Section
///     （纯字符串，internal set），ModConfigWindow.Open 里原样交给 AddHeader。
///     所以标题必须允许事后重写：见 <see cref="RefreshSections"/>，由
///     <see cref="ConfigSectionRefresh"/> 在面板每次打开前调用；注册时先给英文，
///     打开面板时再换成当前语言。
///   - **Init 阶段不读语言**：Init 跑在主菜单早期，任何 `LocHelper.GetCurrentLocaleCode()`
///     都会落到 `LocalizationSettings.SelectedLocale` → `GetSelectedLocale()`，
///     结果未就绪时会同步 `WaitForCompletion()`，等于在启动/本地化初始化途中逼一次
///     阻塞等待。分组标题和条目文案都不需要它（一个是面板打开时重写，一个是 Func）。
///
/// 默认值只在 <see cref="Toggle"/> 构造时声明一次：Define 用它注册，Init 之前
/// 或 Init 失败时也用它兜底。
/// </summary>
internal static class ModSettings
{
    // ---- 分组标题：key + 英文兜底 ----
    // ConfigEntry.Section 是纯字符串（internal set），只能在面板打开时重写，
    // 所以这里存键、由 RefreshSections 用 Loc.T 解析；注册时先用英文兜底。
    private const string SecDisplayKey = "config.section.display";
    private const string SecDisplayEn = "Display";
    private const string SecRevealKey = "config.section.reveal";
    private const string SecRevealEn = "Reveal";
    private const string SecInteractionKey = "config.section.interaction";
    private const string SecInteractionEn = "Controls";

    private static ModConfig? _cfg;

    /// <summary>已注册的条目与它的分组标题键/英文兜底，供 <see cref="RefreshSections"/> 重写。</summary>
    private static readonly List<(ConfigEntry Entry, string Key, string En)> Sections = new();

    // ---- 显示优化 ----
    private static readonly Toggle ItemAppraisalToggle = new(true);      // 真伪 + 价值构成 + 植物
    private static readonly Toggle CostPerformanceToggle = new(true);    // 性价比 + 压缩率
    private static readonly Toggle ContainerContentsToggle = new(true);  // 容器内容
    private static readonly Toggle InfoEnhanceToggle = new(true);        // 标签/模块/电池/黑市/彩票/描述/价格
    private static readonly Toggle TypeBadgeToggle = new(true);          // tooltip 上方分类标签
    private static readonly Toggle TypeBadgeMutedToggle = new(false);    // 非违禁标签淡化配色
    private static readonly Toggle ContainerNameToggle = new(false);     // 容器和机器外显文本
    private static readonly Toggle ContainerNameBgToggle = new(true);    // 外显文本半透明底
    private static readonly Toggle CustomerAlertToggle = new(true);      // 顾客到店提示

    // ---- 操作优化 ----
    private static readonly Toggle DoubleClickInspectToggle = new(true);

    // ---- 透视 ----
    private static readonly Toggle HiddenTruthToggle = new(true);        // 揭示伪装与真实属性
    private static readonly Toggle WaterWineToggle = new(false);         // 水质 + 果酿分析
    private static readonly Toggle ScratcherToggle = new(false);         // 刮刮乐预知
    private static readonly Toggle SealedBoxToggle = new(false);         // 带锁容器透视
    private static readonly Toggle ResultPrefillToggle = new(false);     // 鉴定结果预填

    internal static void Init(string modId)
    {
        try
        {
            _cfg = ModConfig.For(modId);
            Sections.Clear();

            // ================= 显示优化 =================
            ItemAppraisalToggle.Define(_cfg, "show_item_appraisal", SecDisplayKey, SecDisplayEn,
                Text("config.item_appraisal", "Item Appraisal"),
                Text("config.item_appraisal.tip", "Shows authenticity verdicts and value breakdowns, adapted to each item type."));

            CostPerformanceToggle.Define(_cfg, "show_cost_performance", SecDisplayKey, SecDisplayEn,
                Text("config.cost_performance", "Value per Cell"),
                Text("config.cost_performance.tip", "Shows value per inventory cell and the compression ratio of containers."));

            ContainerContentsToggle.Define(_cfg, "show_container_contents", SecDisplayKey, SecDisplayEn,
                Text("config.container_contents", "Container Contents"),
                Text("config.container_contents.tip", "Lists unlocked container contents; no separate total value."));

            InfoEnhanceToggle.Define(_cfg, "show_info_enhance", SecDisplayKey, SecDisplayEn,
                Text("config.info_enhance", "Extra Item Info"),
                Text("config.info_enhance.tip", "Adds item-specific details such as label and module effects, battery " +
                     "life, market modifiers, lottery results, dimmed descriptions and clearer price labels."));

            TypeBadgeToggle.Define(_cfg, "show_type_badge", SecDisplayKey, SecDisplayEn,
                Text("config.type_badge", "Item Type Badges"),
                Text("config.type_badge.tip", "Shows the item's categories above the tooltip."));

            TypeBadgeMutedToggle.Define(_cfg, "mute_type_badge_colors", SecDisplayKey, SecDisplayEn,
                Text("config.type_badge_muted", "Muted Badge Colors"),
                Text("config.type_badge_muted.tip", "Non-contraband badges use the tooltip's dark background and a " +
                     "brightened category color. Contraband keeps its red warning text."));

            ContainerNameToggle.Define(_cfg, "show_container_name_display", SecDisplayKey, SecDisplayEn,
                Text("config.container_name", "Container & Machine Labels"),
                Text("config.container_name.tip", "Shows the name at the bottom of container and machine icons."));

            ContainerNameBgToggle.Define(_cfg, "show_container_name_bg", SecDisplayKey, SecDisplayEn,
                Text("config.container_name_bg", "Label Background"),
                Text("config.container_name_bg.tip", "Adds a translucent black background (60% opacity) behind the label."));

            CustomerAlertToggle.Define(_cfg, "show_customer_alert", SecDisplayKey, SecDisplayEn,
                Text("config.customer_alert", "Customer Arrival Alert"),
                Text("config.customer_alert.tip", "When a wanted or augmented customer arrives, shows a notice with the " +
                     "actual bounty, real name and augmentation tier."));

            // ================= 操作优化 =================
            DoubleClickInspectToggle.Define(_cfg, "double_click_inspect", SecInteractionKey, SecInteractionEn,
                Text("config.double_click_inspect", "Double-Click to Inspect"),
                Text("config.double_click_inspect.tip", "Double-click an appraisal item or an authenticity-check item " +
                     "(injectors, cigarettes, food stamps, etc.) to open the inspection panel without the magnifying " +
                     "glass. While the panel is open, double-click another item to switch to it. The magnifying-glass " +
                     "hint becomes \"Double-Click To Inspect\". Turn off to restore the original flow."));

            // ================= 透视 =================
            HiddenTruthToggle.Define(_cfg, "show_hidden_truth", SecRevealKey, SecRevealEn,
                Text("config.hidden_truth", "Reveal Disguises & True Stats"),
                Text("config.hidden_truth.tip", "Shows what disguised features really are, true durability, and true " +
                     "ingot/chemical purity. Exposes fakes directly and makes appraisal much easier."));

            WaterWineToggle.Define(_cfg, "show_water_purity", SecRevealKey, SecRevealEn,
                Text("config.water_wine", "Water & Wine Analysis"),
                Text("config.water_wine.tip", "Shows water purity and impurities, plus wine brewing data, without a " +
                     "testing strip."));

            ScratcherToggle.Define(_cfg, "show_scratcher", SecRevealKey, SecRevealEn,
                Text("config.scratcher", "Scratcher Preview"),
                Text("config.scratcher.tip", "Shows whether a scratcher ticket wins, how much, and how far it has been " +
                     "scratched, before you scratch it. Read-only: it reuses the game's own rules and never scratches for you."));

            SealedBoxToggle.Define(_cfg, "show_sealed_box", SecRevealKey, SecRevealEn,
                Text("config.sealed_box", "Locked Container X-Ray"),
                Text("config.sealed_box.tip", "Lists the contents and total value of locked containers (safes, gun " +
                     "cabinets) before they are unlocked. This skips the unlocking step, so it is off by default."));

            ResultPrefillToggle.Define(_cfg, "show_result_prefill", SecRevealKey, SecRevealEn,
                Text("config.result_prefill", "Prefill Inspection Result"),
                Text("config.result_prefill.tip", "When the inspection panel opens, ticks the actual counterfeit issues " +
                     "and enters the best appraisal offer. It only fills in the controls and never submits or confirms. " +
                     "Off by default."));

            _cfg.Save();

            // 这里**不读语言**：Init 跑在主菜单早期，而 LocHelper.GetCurrentLocaleCode()
            // 最终是 LocalizationSettings.SelectedLocale → GetSelectedLocale()，在选择结果
            // 未就绪时会同步 WaitForCompletion()。模组 Init 正处于启动/本地化初始化途中，
            // 为了一行日志去逼一次同步等待不值得（多人装模组时表现为语言卡在英文、
            // 启动链上的后续步骤被拖死）。语言在这里也没有用处：分组标题由
            // RefreshSections 在面板打开时按当前语言重写，标签/说明本来就是 Func<string>。
            Log.Msg($"ModSettings initialized: {_cfg.Entries.Count} entries " +
                    $"({Sections.Count} sections); 语言在打开设置面板时再判定");
        }
        catch (Exception ex)
        {
            Log.Error("ModSettings.Init", ex);
        }
    }

    /// <summary>首次刷新分组时记录语言。</summary>
    private static bool _localeLogged;

    /// <summary>
    /// 按当前语言重写分组标题。ConfigEntry.Section 的 setter 是 internal，
    /// 公开 API 无法修改，只能走反射；失败时保持原值，不影响面板打开。
    /// 由 <see cref="ConfigSectionRefresh"/> 在每次打开设置面板前调用。
    /// </summary>
    internal static void RefreshSections()
    {
        if (Sections.Count == 0) return;

        if (!_localeLogged)
        {
            _localeLogged = true;
            try
            {
                Log.Msg($"settings opened: locale='{Loc.Code}' zh={Loc.IsChinese} " +
                        $"section='{Sections[0].Entry.Section}'");
            }
            catch { }
        }

        MethodInfo? setter = null;
        foreach (var (entry, key, en) in Sections)
        {
            try
            {
                string desired = Loc.T(key, en);
                if (entry.Section == desired) continue;

                setter ??= AccessTools.PropertySetter(typeof(ConfigEntry), "Section");
                if (setter == null)
                {
                    Log.Warn("ModSettings.Section", "ConfigEntry.Section 没有可用的 setter，分组标题保持启动时的语言");
                    return;
                }

                setter.Invoke(entry, new object[] { desired });
            }
            catch (Exception ex)
            {
                Log.Warn("ModSettings.Section", ex);
                return;
            }
        }
    }

    /// <summary>标签/说明延迟到面板渲染时求值，可随语言切换。</summary>
    private static Func<string> Text(string key, string en) => () => Loc.T(key, en);

    internal static bool Auth => ItemAppraisalToggle.Value;
    internal static bool Value => ItemAppraisalToggle.Value;
    internal static bool Plant => ItemAppraisalToggle.Value;
    internal static bool InfoEnhance => InfoEnhanceToggle.Value;
    internal static bool ValueEnhance => CostPerformanceToggle.Value;
    internal static bool Container => ContainerContentsToggle.Value;
    internal static bool ModuleEffects => InfoEnhanceToggle.Value;
    internal static bool MachineBattery => InfoEnhanceToggle.Value;
    internal static bool PriceImpact => InfoEnhanceToggle.Value;
    internal static bool Contraband => InfoEnhanceToggle.Value;
    internal static bool Lottery => InfoEnhanceToggle.Value;
    internal static bool HideEstimate => InfoEnhanceToggle.Value;
    internal static bool DimDescription => InfoEnhanceToggle.Value;
    internal static bool CustomerAlert => CustomerAlertToggle.Value;
    internal static bool HiddenTruth => HiddenTruthToggle.Value;
    internal static bool WaterPurity => WaterWineToggle.Value;
    internal static bool Wine => WaterWineToggle.Value;
    internal static bool Scratcher => ScratcherToggle.Value;
    internal static bool SealedBox => SealedBoxToggle.Value;
    internal static bool ResultPrefill => ResultPrefillToggle.Value;
    internal static bool DoubleClickInspect => DoubleClickInspectToggle.Value;
    internal static bool TypeBadge => TypeBadgeToggle.Value;
    internal static bool ContainerNames => ContainerNameToggle.Value;
    internal static bool TypeBadgeMuted => TypeBadgeMutedToggle.Value;
    internal static bool ItemNameBackground => ContainerNameBgToggle.Value;
    internal static float ItemNameBackgroundAlpha => 0.6f;

    /// <summary>
    /// 单个开关：默认值只声明一次，Define 用它注册，Init 之前或失败时也用它兜底。
    /// 同时把条目记进 <see cref="Sections"/>，供分组标题事后重写。
    /// </summary>
    private sealed class Toggle
    {
        private readonly bool _default;
        private ConfigEntry<bool>? _entry;

        internal Toggle(bool defaultValue) => _default = defaultValue;

        internal void Define(ModConfig cfg, string key, string sectionKey, string sectionEn,
            Func<string> label, Func<string> tip)
        {
            // 注册时一律用英文分组名，不在这里读语言（见 Init 里关于 Init 阶段不碰本地化的说明）。
            // 玩家能看到的标题由 RefreshSections 在面板打开前重写成当前语言。
            _entry = cfg.Define(key, _default, sectionEn, label, tip);
            Sections.Add((_entry, sectionKey, sectionEn));
        }

        internal bool Value => _entry?.Value ?? _default;
    }
}
