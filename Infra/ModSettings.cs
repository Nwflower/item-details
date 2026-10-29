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
///   "顾客提示"   —— 顾客维度的提示
///   "透视"       —— 揭示本应靠鉴定/分析才能知道的东西（会降低难度）
///   "交互与外观" —— 操作方式与视觉权重
///
/// 合并过的项（旧键已废弃，读取时回落到默认）：
///   show_berry            + show_seed_traits      → show_plant
///   show_lock_preview     + show_scratcher（现已按分级拆回独立开关，默认值不同）
///   show_storage_ratio    + show_container_contents → show_container
///   show_label_impact     + show_condition_impact  → show_price_impact
///   show_value + show_perfect_range + show_ribwich_reasons
///               + show_generic_breakdown + show_appraisal_title → show_value
///   show_feature_reveal   + show_hidden_real       → show_hidden_truth
///   show_water_purity     + show_water_advice      → show_water_purity
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
    private const string SecCustomerKey = "config.section.customer";
    private const string SecCustomerEn = "Customer Alerts";
    private const string SecRevealKey = "config.section.reveal";
    private const string SecRevealEn = "Reveal";
    private const string SecInteractionKey = "config.section.interaction";
    private const string SecInteractionEn = "Interaction & Visuals";

    private static ModConfig? _cfg;

    /// <summary>已注册的条目与它的分组标题键/英文兜底，供 <see cref="RefreshSections"/> 重写。</summary>
    private static readonly List<(ConfigEntry Entry, string Key, string En)> Sections = new();

    // ---- 显示内容 ----
    private static readonly Toggle AuthToggle = new(true);           // 真伪结论
    private static readonly Toggle ValueToggle = new(true);          // 价值构成 + 完美区间 + 赝品原因 + 面板真实价
    private static readonly Toggle ValueEnhanceToggle = new(true);   // 性价比（非鉴定物品）
    private static readonly Toggle PriceImpactToggle = new(true);    // 标签、瑕疵与市场事件的价格影响
    private static readonly Toggle ModuleEffectsToggle = new(true);  // 模组效果的「基准 → 当前」实际变更
    private static readonly Toggle MachineBatteryToggle = new(true); // 机器电池当前/最大电量
    private static readonly Toggle ContainerToggle = new(true);      // 压缩率 + 内容物
    private static readonly Toggle PlantToggle = new(true);          // 荧光莓品质 + 种子性状
    private static readonly Toggle ContrabandToggle = new(true);     // 违禁溢价
    private static readonly Toggle LotteryToggle = new(true);        // 彩票命中数与奖金
    private static readonly Toggle ScratcherToggle = new(true);      // 刮刮卡结果（透视）

    // ---- 顾客提示 ----
    private static readonly Toggle CustomerAlertToggle = new(true);  // 通缉/改造体提示（含真名与等级）

    // ---- 透视 ----
    private static readonly Toggle HiddenTruthToggle = new(true);    // 伪装特性 + 真实耐久/纯度
    private static readonly Toggle WaterPurityToggle = new(true);   // 水质纯度、杂质与建议（默认开，不必有检测仪）
    private static readonly Toggle SealedBoxToggle = new(false);     // 密封箱内容与箱内价值
    private static readonly Toggle ResultPrefillToggle = new(false); // 鉴定结果/最优报价预填
    private static readonly Toggle WineToggle = new(true);            // 酒的分数/成分/赝品（透视）

    // ---- 交互与外观 ----
    private static readonly Toggle DoubleClickInspectToggle = new(true);
    private static readonly Toggle HideEstimateToggle = new(true);
    private static readonly Toggle DimDescriptionToggle = new(true);
    private static readonly Toggle TypeBadgeToggle = new(true);        // tooltip 上方的物品分类小框
    private static ConfigEntry<string>? _nameMode;                     // 图标名显示范围：无 / 容器名 / 仅改名物品

    // 图标名外观（非布尔，单独存条目）
    private static ConfigEntry<string>? _namePos;
    private static ConfigEntry<bool>? _nameBg;
    private static ConfigEntry<float>? _nameBgAlpha;

    internal static void Init(string modId)
    {
        try
        {
            _cfg = ModConfig.For(modId);
            Sections.Clear();

            // ================= 显示内容 =================
            AuthToggle.Define(_cfg, "show_auth", SecDisplayKey, SecDisplayEn,
                Text("config.auth", "Authenticity"),
                Text("config.auth.tip", "Shows genuine/counterfeit conclusions and specific failed checks for " +
                     "injectors, cigarettes and food stamps. True value is added only when registered."));

            ValueToggle.Define(_cfg, "show_value", SecDisplayKey, SecDisplayEn,
                Text("config.value", "Value & Appraisal Details"),
                Text("config.value.tip", "For appraisal items: total value, perfect offer range, source breakdown and " +
                     "counterfeit reasons; the appraisal title also shows true price. Unknown or " +
                     "modded appraisal types use a measured breakdown."));

            ValueEnhanceToggle.Define(_cfg, "show_value_enhance", SecDisplayKey, SecDisplayEn,
                Text("config.value_enhance", "Value per Cell (Non-Appraisal)"),
                Text("config.value_enhance.tip", "For non-appraisal items: unit value × count and value per occupied cell. " +
                     "Appraisal items are skipped because they already show true value."));

            PriceImpactToggle.Define(_cfg, "show_price_impact", SecDisplayKey, SecDisplayEn,
                Text("config.price_impact", "Label, Condition & Market Impact"),
                Text("config.price_impact.tip", "Appends the price modifier after label lines and conditions such as " +
                     "Dented/Rusted, and lists active store events affecting the item. " +
                     "Vanilla only shows the names."));

            ModuleEffectsToggle.Define(_cfg, "show_module_effects", SecDisplayKey, SecDisplayEn,
                Text("config.module_effects", "Module Effect Deltas"),
                Text("config.module_effects.tip", "Vanilla only lists percentages such as performance/efficiency/quality +16%. " +
                     "This appends the values the game computed: processing speed for performance, power " +
                     "draw for efficiency, adjusted quality for quality (machines with a quality ratio only). " +
                     "All derived from item tags; machines without the data keep the vanilla line."));

            MachineBatteryToggle.Define(_cfg, "show_machine_battery", SecDisplayKey, SecDisplayEn,
                Text("config.machine_battery", "Machine Battery"),
                Text("config.machine_battery.tip", "Shows the installed battery's remaining energy and estimated days " +
                     "directly under the machine's daily energy line. Machines without a battery are skipped."));

            ContainerToggle.Define(_cfg, "show_container", SecDisplayKey, SecDisplayEn,
                Text("config.container", "Container Details"),
                Text("config.container.tip", "Shows compression ratio (inside cells ÷ item cells), contents and total value."));

            PlantToggle.Define(_cfg, "show_plant", SecDisplayKey, SecDisplayEn,
                Text("config.plant", "Plant Details"),
                Text("config.plant.tip", "Berries show quality (normal/moldy/unripe); seeds show carried traits and ranks."));

            ContrabandToggle.Define(_cfg, "show_contraband", SecDisplayKey, SecDisplayEn,
                Text("config.contraband", "Contraband Black-Market Markup"),
                Text("config.contraband.tip", "Appends black-market markup (Low 35 / Mid 70 / High 100 / Critical 150). " +
                     "The contraband level itself is shown by vanilla."));

            LotteryToggle.Define(_cfg, "show_lottery", SecDisplayKey, SecDisplayEn,
                Text("config.lottery", "Lottery Matches & Prize"),
                Text("config.lottery.tip", "After the draw, shows how many numbers matched and the prize. " +
                     "Vanilla only colors the matched numbers."));

            // ================= 顾客提示 =================
            CustomerAlertToggle.Define(_cfg, "show_customer_alert", SecCustomerKey, SecCustomerEn,
                Text("config.customer_alert", "Customer Arrival Alert"),
                Text("config.customer_alert.tip", "Alerts when a wanted or augmented client arrives, including the actual " +
                     "bounty, real name and augmentation tier."));

            // ================= 透视 =================
            HiddenTruthToggle.Define(_cfg, "show_hidden_truth", SecRevealKey, SecRevealEn,
                Text("config.hidden_truth", "Reveal Disguises & True Stats"),
                Text("config.hidden_truth.tip", "Shows true condition differences, true durability and ingot/chemical purity. " +
                     "Directly exposes fakes and lowers appraisal difficulty."));

            WaterPurityToggle.Define(_cfg, "show_water_purity", SecRevealKey, SecRevealEn,
                Text("config.water_purity", "Water Quality & Advice"),
                Text("config.water_purity.tip", "Shows purity %, vanilla tier name, impurities, non-water volume and treatment " +
                     "advice without needing a water tester. Turn it off to go back to the vanilla " +
                     "flow (tester required). When a dedicated water mod (e.g. AquaSense) is enabled, " +
                     "it takes over this section."));

            ScratcherToggle.Define(_cfg, "show_scratcher", SecRevealKey, SecRevealEn,
                Text("config.scratcher", "Scratcher Result"),
                Text("config.scratcher.tip", "Shows the prize (or no prize) and scratched progress before scratching. " +
                     "Read-only recomputation; it never scratches for you. Turn it off to hide the result."));

            SealedBoxToggle.Define(_cfg, "show_sealed_box", SecRevealKey, SecRevealEn,
                Text("config.sealed_box", "Sealed Container Contents"),
                Text("config.sealed_box.tip", "Lists contents and total value of locked containers before unlocking. " +
                     "Bypasses the lock; off by default."));

            ResultPrefillToggle.Define(_cfg, "show_result_prefill", SecRevealKey, SecRevealEn,
                Text("config.result_prefill", "Result Prefill"),
                Text("config.result_prefill.tip", "Prefills the actual counterfeit issues and the best appraisal offer " +
                     "when the inspection panel opens. It only fills the controls; it never submits or confirms. Off by default."));

            WineToggle.Define(_cfg, "show_wine", SecRevealKey, SecRevealEn,
                Text("config.wine", "Wine Score and Composition"),
                Text("config.wine.tip", "Shows wine data the base game hides: score and tier, extract/contaminant, " +
                     "counterfeit (illegal yeast), aging days and per-day bonus, fermentation progress."));

            // ================= 交互与外观 =================
            DoubleClickInspectToggle.Define(_cfg, "double_click_inspect", SecInteractionKey, SecInteractionEn,
                Text("config.double_click_inspect", "Double-Click to Inspect"),
                Text("config.double_click_inspect.tip", "Double-click an appraisal or inspection item (injectors, cigarettes, " +
                     "stamps, etc.) to open the inspection panel without the magnifier. Also changes the hint text; " +
                     "disable to restore vanilla flow."));

            HideEstimateToggle.Define(_cfg, "hide_estimate_value", SecInteractionKey, SecInteractionEn,
                Text("config.hide_estimate_value", "Hide Vanilla Estimate Line"),
                Text("config.hide_estimate_value.tip", "Those lines show the customer's offer, not true appraisal value. Hidden only " +
                     "on appraisal items and only when a total can be computed."));

            DimDescriptionToggle.Define(_cfg, "dim_description", SecInteractionKey, SecInteractionEn,
                Text("config.dim_description", "Dim Item Description"),
                Text("config.dim_description.tip", "Renders item descriptions in gray so appraisal information stands out. " +
                     "Color only, no text changes."));

            TypeBadgeToggle.Define(_cfg, "show_type_badge", SecInteractionKey, SecInteractionEn,
                Text("config.type_badge", "Item Type Badges"),
                Text("config.type_badge.tip", "Shows the item's categories above the tooltip as colored plates with white text: " +
                     "medical/water/drugs/poison blue, tobacco/alcohol gold, luxury pink, food green, household cyan, " +
                     "weapons/armor teal, tools/materials purple, contraband/restricted dark red, other gray. " +
                     "Turn it off to go back to vanilla: the [type] written inline in the tooltip body."));

            // 新键名用于切断旧版默认值“容器名”。旧配置里的 name_label_mode
            // 不再被读取；新键缺失时按默认“无 (Off)”处理，已有开启项自动回到关闭。
            _nameMode = _cfg.Define("name_label_display_mode", "无 (Off)", SecInteractionEn,
                Text("config.name_label_mode", "Name Labels on Icons"),
                new[] { "无 (Off)", "容器名 (Containers)", "仅改名物品 (Renamed only)" },
                Text("config.name_label_mode.tip", "Off: hide labels. Containers: only containers and machines. Renamed only: only items renamed with the label printer."));

            _namePos = _cfg.Define("name_label_position", "底部 (Bottom)", SecInteractionEn,
                Text("config.name_label_position", "Name Label Position"),
                new[] { "底部 (Bottom)", "中间 (Middle)", "顶部 (Top)" },
                Text("config.name_label_position.tip", "Where the name sits on the item icon."));

            _nameBg = _cfg.Define("name_label_background", false, SecInteractionEn,
                Text("config.name_label_background", "Name Label Background"),
                Text("config.name_label_background.tip", "Draws a translucent black plate behind the name for busy icons."),
                false);
            _nameBgAlpha = _cfg.Define("name_label_background_alpha", 0.55f, SecInteractionEn,
                Text("config.name_label_background_alpha", "Background Alpha"),
                0f, 1f,
                Text("config.name_label_background_alpha.tip", "Opacity of the background plate, 0-1."));

            _cfg.Save();

            // 漏网的条目也要登记：`_cfg.Define` 直接注册的条目（四个下拉框）没有经过
            // Toggle.Define，如果只重写登记过的那批，它们的 Section 会一直是英文，
            // 于是面板里会多出一个独立的英文分组头（和中文标题并列）。
            // 注册顺序保证了这里读到的一定是注册时的英文名，所以能按英文名反查键。
            RegisterUntrackedSections();

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

    /// <summary>首次重写时记一次语言判定结果：这是本模组第一次、也是唯一一次主动读 locale。</summary>
    private static bool _localeLogged;

    /// <summary>
    /// 英文分组名 → 本地化键。用于把「注册时写下的英文分组名」反查回键，
    /// 这样不管是 Toggle.Define 还是直接 _cfg.Define 注册的条目都能被重写。
    /// </summary>
    private static readonly (string En, string Key)[] SectionMap =
    {
        (SecDisplayEn, SecDisplayKey),
        (SecCustomerEn, SecCustomerKey),
        (SecRevealEn, SecRevealKey),
        (SecInteractionEn, SecInteractionKey),
    };

    /// <summary>
    /// 把没经过 <see cref="Toggle.Define"/> 的条目补进 <see cref="Sections"/>。
    /// 必须在所有 Define 调用之后、且在任何重写之前执行：那时 Section 还是注册时的英文名。
    ///
    /// 同时兼容「注册时误把键当分组名传进来」的写法（历史上真发生过：
    /// 直接 Define 的四个下拉框把 `config.section.interaction` 当 section 传了，
    /// 面板里因此多出一个写着原始键名的英文分组头）。
    /// </summary>
    private static void RegisterUntrackedSections()
    {
        if (_cfg == null) return;
        foreach (var entry in _cfg.Entries)
        {
            if (Sections.Exists(s => ReferenceEquals(s.Entry, entry))) continue;
            foreach (var (en, key) in SectionMap)
            {
                if (entry.Section != en && entry.Section != key) continue;
                Sections.Add((entry, key, en));
                break;
            }
        }
    }

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

    internal static bool Auth => AuthToggle.Value;
    internal static bool Value => ValueToggle.Value;
    internal static bool ValueEnhance => ValueEnhanceToggle.Value;
    internal static bool ModuleEffects => ModuleEffectsToggle.Value;
    internal static bool MachineBattery => MachineBatteryToggle.Value;
    internal static bool PriceImpact => PriceImpactToggle.Value;
    internal static bool Container => ContainerToggle.Value;
    internal static bool Plant => PlantToggle.Value;
    internal static bool Contraband => ContrabandToggle.Value;
    internal static bool Lottery => LotteryToggle.Value;
    internal static bool CustomerAlert => CustomerAlertToggle.Value;
    internal static bool HiddenTruth => HiddenTruthToggle.Value;
    internal static bool WaterPurity => WaterPurityToggle.Value;
    internal static bool Scratcher => ScratcherToggle.Value;
    internal static bool SealedBox => SealedBoxToggle.Value;
    internal static bool ResultPrefill => ResultPrefillToggle.Value;
    internal static bool Wine => WineToggle.Value;
    internal static bool DoubleClickInspect => DoubleClickInspectToggle.Value;
    internal static bool HideEstimate => HideEstimateToggle.Value;
    internal static bool DimDescription => DimDescriptionToggle.Value;
    internal static bool TypeBadge => TypeBadgeToggle.Value;
    internal static string ItemNameMode => _nameMode?.Value ?? "无 (Off)";
    internal static string ItemNamePosition => _namePos?.Value ?? "底部 (Bottom)";
    internal static bool ItemNameBackground => _nameBg?.Value ?? false;
    internal static float ItemNameBackgroundAlpha => _nameBgAlpha?.Value ?? 0.55f;

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
