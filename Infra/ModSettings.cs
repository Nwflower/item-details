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
    // ---- 分组标题（中 / 英），标题需要事后重写，所以两种语言都留着 ----
    private const string SecDisplayZh = "显示内容";
    private const string SecDisplayEn = "Display";
    private const string SecCustomerZh = "顾客提示";
    private const string SecCustomerEn = "Customer Alerts";
    private const string SecRevealZh = "透视";
    private const string SecRevealEn = "Reveal";
    private const string SecInteractionZh = "交互与外观";
    private const string SecInteractionEn = "Interaction & Visuals";

    private static ModConfig? _cfg;

    /// <summary>已注册的条目与它的两种分组标题，供 <see cref="RefreshSections"/> 重写。</summary>
    private static readonly List<(ConfigEntry Entry, string Zh, string En)> Sections = new();

    // ---- 显示内容 ----
    private static readonly Toggle AuthToggle = new(true);           // 真伪结论
    private static readonly Toggle ValueToggle = new(true);          // 价值构成 + 完美区间 + 赝品原因 + 面板真实价
    private static readonly Toggle ValueEnhanceToggle = new(true);   // 性价比（非鉴定物品）
    private static readonly Toggle PriceImpactToggle = new(true);    // 标签、瑕疵与市场事件的价格影响
    private static readonly Toggle ModuleEffectsToggle = new(true);  // 模组效果的「基准 → 当前」实际变更
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
    private static readonly Toggle WineToggle = new(true);            // 酒的分数/成分/赝品（透视）

    // ---- 交互与外观 ----
    private static readonly Toggle DoubleClickInspectToggle = new(true);
    private static readonly Toggle HideEstimateToggle = new(true);
    private static readonly Toggle DimDescriptionToggle = new(true);
    private static ConfigEntry<string>? _nameMode;                     // 图标名显示范围：无 / 容器名 / 仅改名物品

    // 图标名外观（非布尔，单独存条目）
    private static ConfigEntry<string>? _namePos;
    private static ConfigEntry<float>? _nameScale;
    private static ConfigEntry<bool>? _nameBg;
    private static ConfigEntry<float>? _nameBgAlpha;

    internal static void Init(string modId)
    {
        try
        {
            _cfg = ModConfig.For(modId);
            Sections.Clear();

            // ================= 显示内容 =================
            AuthToggle.Define(_cfg, "show_auth", SecDisplayZh, SecDisplayEn,
                Text("真伪鉴定", "Authenticity"),
                Text("注射器/香烟/食品券显示正品/赝品结论与具体不合格项；" +
                     "只有存在已注册真实价值的物品才补充真实价值。",
                     "Shows genuine/counterfeit conclusions and specific failed checks for " +
                     "injectors, cigarettes and food stamps. True value is added only when registered."));

            ValueToggle.Define(_cfg, "show_value", SecDisplayZh, SecDisplayEn,
                Text("价值构成与鉴定信息", "Value & Appraisal Details"),
                Text("鉴定物品显示总价、完美报价区间、来源明细与赝品原因；" +
                     "鉴定服务标题附带真实价格。未知或模组新增的鉴定类型使用实测分解。",
                     "For appraisal items: total value, perfect offer range, source breakdown and " +
                     "counterfeit reasons; the appraisal title also shows true price. Unknown or " +
                     "modded appraisal types use a measured breakdown."));

            ValueEnhanceToggle.Define(_cfg, "show_value_enhance", SecDisplayZh, SecDisplayEn,
                Text("性价比（非鉴定物品）", "Value per Cell (Non-Appraisal)"),
                Text("非鉴定物品显示单价×数量与性价比（价值÷占格）。" +
                     "鉴定物品已有真实价，不再重复显示，避免与顾客报价混淆。",
                     "For non-appraisal items: unit value × count and value per occupied cell. " +
                     "Appraisal items are skipped because they already show true value."));

            PriceImpactToggle.Define(_cfg, "show_price_impact", SecDisplayZh, SecDisplayEn,
                Text("标签、瑕疵与市场影响", "Label, Condition & Market Impact"),
                Text("在标签行与「凹陷/锈蚀」等状态后追加价格影响百分比，" +
                     "并列出当前生效的店铺事件对这件物品的修正；原版只写名字不写数值。",
                     "Appends the price modifier after label lines and conditions such as " +
                     "Dented/Rusted, and lists active store events affecting the item. " +
                     "Vanilla only shows the names."));

            ModuleEffectsToggle.Define(_cfg, "show_module_effects", SecDisplayZh, SecDisplayEn,
                Text("模组效果的实际变更", "Module Effect Deltas"),
                Text("机器装模块后，原版只写「性能/效率/质量 +16%」这类百分比。" +
                     "这里直接在该行后面补上游戏算好的实际值：性能=加工速度变化、效率=耗电变化、" +
                     "质量=品质折算值（仅限设置了品质比率的机器）。三项都按物品标签推导，" +
                     "没有对应数据的机器保留原版百分比。",
                     "Vanilla only lists percentages such as performance/efficiency/quality +16%. " +
                     "This appends the values the game computed: processing speed for performance, power " +
                     "draw for efficiency, adjusted quality for quality (machines with a quality ratio only). " +
                     "All derived from item tags; machines without the data keep the vanilla line."));

            ContainerToggle.Define(_cfg, "show_container", SecDisplayZh, SecDisplayEn,
                Text("容器信息", "Container Details"),
                Text("容器显示压缩率（内部格数÷自身占格）、内容物清单与合计价值。",
                     "Shows compression ratio (inside cells ÷ item cells), contents and total value."));

            PlantToggle.Define(_cfg, "show_plant", SecDisplayZh, SecDisplayEn,
                Text("植物信息", "Plant Details"),
                Text("荧光莓显示品质（正常/发霉/未成熟等）；种子显示携带性状与等级。",
                     "Berries show quality (normal/moldy/unripe); seeds show carried traits and ranks."));

            ContrabandToggle.Define(_cfg, "show_contraband", SecDisplayZh, SecDisplayEn,
                Text("违禁品黑市溢价", "Contraband Black-Market Markup"),
                Text("违禁品追加黑市溢价百分比（低35/中70/高100/严重150）；" +
                     "违禁等级本身由原版显示。",
                     "Appends black-market markup (Low 35 / Mid 70 / High 100 / Critical 150). " +
                     "The contraband level itself is shown by vanilla."));

            LotteryToggle.Define(_cfg, "show_lottery", SecDisplayZh, SecDisplayEn,
                Text("彩票命中数与奖金", "Lottery Matches & Prize"),
                Text("开奖后显示命中几个号码与对应奖金；原版只把命中的号码变色。",
                     "After the draw, shows how many numbers matched and the prize. " +
                     "Vanilla only colors the matched numbers."));

            // ================= 顾客提示 =================
            CustomerAlertToggle.Define(_cfg, "show_customer_alert", SecCustomerZh, SecCustomerEn,
                Text("顾客到店提示", "Customer Arrival Alert"),
                Text("顾客到店时提示是否为通缉犯或改造体，并给出实际举报奖励、" +
                     "通缉犯真名与改造体等级。",
                     "Alerts when a wanted or augmented client arrives, including the actual " +
                     "bounty, real name and augmentation tier."));

            // ================= 透视 =================
            HiddenTruthToggle.Define(_cfg, "show_hidden_truth", SecRevealZh, SecRevealEn,
                Text("揭示伪装与真实属性", "Reveal Disguises & True Stats"),
                Text("显示被伪装前的真实条件差异、真实耐久与锭/化学品真实纯度。" +
                     "会直接暴露假货，明显降低鉴定难度。",
                     "Shows true condition differences, true durability and ingot/chemical purity. " +
                     "Directly exposes fakes and lowers appraisal difficulty."));

            WaterPurityToggle.Define(_cfg, "show_water_purity", SecRevealZh, SecRevealEn,
                Text("水质与建议", "Water Quality & Advice"),
                Text("液体显示纯度百分比、游戏档位名、杂质构成与非水液体体积，并给出处理建议。" +
                     "开启时不需要水质检测仪；关闭后恢复原版流程（需检测仪/试纸）。" +
                     "同时启用专门的水质模组（如 AquaSense）时，这一段让给该模组显示。",
                     "Shows purity %, vanilla tier name, impurities, non-water volume and treatment " +
                     "advice without needing a water tester. Turn it off to go back to the vanilla " +
                     "flow (tester required). When a dedicated water mod (e.g. AquaSense) is enabled, " +
                     "it takes over this section."));

            ScratcherToggle.Define(_cfg, "show_scratcher", SecRevealZh, SecRevealEn,
                Text("刮刮卡结果", "Scratcher Result"),
                Text("未刮开就给出中奖金额（或不中奖）与已刮进度。按游戏同一套规则只读复算，" +
                     "不替玩家刮开；关闭后则不显示结果。",
                     "Shows the prize (or no prize) and scratched progress before scratching. " +
                     "Read-only recomputation; it never scratches for you. Turn it off to hide the result."));

            SealedBoxToggle.Define(_cfg, "show_sealed_box", SecRevealZh, SecRevealEn,
                Text("密封箱内容与价值", "Sealed Container Contents"),
                Text("带锁容器（保险箱/枪柜）未开锁时也列出箱内物品与箱内总价值。" +
                     "等于绕过开锁环节，默认关闭。",
                     "Lists contents and total value of locked containers before unlocking. " +
                     "Bypasses the lock; off by default."));
            WineToggle.Define(_cfg, "show_wine", SecRevealZh, SecRevealEn,
                Text("酒的分数与成分", "Wine Score and Composition"),
                Text("显示原版隐藏的酿造数据：酒分数与档位、糖度/污染物、是否赝品（非法酵母）、" +
                     "陈化天数与每日加成、发酵进度。",
                     "Shows wine data the base game hides: score and tier, extract/contaminant, " +
                     "counterfeit (illegal yeast), aging days and per-day bonus, fermentation progress."));

            // ================= 交互与外观 =================
            DoubleClickInspectToggle.Define(_cfg, "double_click_inspect", SecInteractionZh, SecInteractionEn,
                Text("双击待鉴定物品直接鉴定", "Double-Click to Inspect"),
                Text("双击待鉴定物品直接打开鉴定面板，不必先拿放大镜再拖动；" +
                     "提示文案同步改成双击提示。关掉则恢复原版流程与原文案。",
                     "Double-click an appraisal item to open the appraisal panel without the " +
                     "magnifier. Changes the hint text; disable to restore vanilla flow."));

            HideEstimateToggle.Define(_cfg, "hide_estimate_value", SecInteractionZh, SecInteractionEn,
                Text("隐藏原版「预估价值」行", "Hide Vanilla Estimate Line"),
                Text("那两行显示的是顾客报价，与鉴定真实价无关（可能差很多），容易误读。" +
                     "只在鉴定物品上隐藏，且我们算不出总价时不隐藏。",
                     "Those lines show the customer's offer, not true appraisal value. Hidden only " +
                     "on appraisal items and only when a total can be computed."));

            DimDescriptionToggle.Define(_cfg, "dim_description", SecInteractionZh, SecInteractionEn,
                Text("描述文本淡化", "Dim Item Description"),
                Text("把物品描述改成灰色，降低视觉权重，让鉴定信息更突出。只改颜色不改文本。",
                     "Renders item descriptions in gray so appraisal information stands out. " +
                     "Color only, no text changes."));

            _nameMode = _cfg.Define("name_label_mode", "容器名 (Containers)", SecInteractionZh,
                Text("图标显示名称", "Name Labels on Icons"),
                new[] { "无 (Off)", "容器名 (Containers)", "仅改名物品 (Renamed only)" },
                Text("无：不显示。容器名：只给容器与机器。仅改名物品：只显示用标签打印机改过名的物品。",
                     "Off: hide labels. Containers: only containers and machines. Renamed only: only items renamed with the label printer."));

            _namePos = _cfg.Define("name_label_position", "底部 (Bottom)", SecInteractionZh,
                Text("图标名位置", "Name Label Position"),
                new[] { "底部 (Bottom)", "中间 (Middle)", "顶部 (Top)" },
                Text("名字贴在物品图标的下/中/上哪一侧。", "Where the name sits on the item icon."));
            _nameScale = _cfg.Define("name_label_scale", 35f, SecInteractionZh,
                Text("图标名相对大小", "Name Label Scale"),
                10f, 100f,
                Text("字号相对图标高度的百分比（10–100）。相对大小会跟着物品图标一起缩放，不随界面分辨率失真。",
                     "Font size as a percentage of the icon height (10-100), so it scales with the icon instead of the UI resolution."));
            _nameBg = _cfg.Define("name_label_background", false, SecInteractionZh,
                Text("图标名半透明底", "Name Label Background"),
                Text("给名字垫一层半透明黑底，压在花纹复杂的图标上更清楚。",
                     "Draws a translucent black plate behind the name for busy icons."),
                false);
            _nameBgAlpha = _cfg.Define("name_label_background_alpha", 0.55f, SecInteractionZh,
                Text("底色不透明度", "Background Alpha"),
                0f, 1f,
                Text("半透明底的不透明度（0–1）。", "Opacity of the background plate, 0-1."));

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

    /// <summary>首次重写时记一次语言判定结果：这是本模组第一次、也是唯一一次主动读 locale。</summary>
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
                Log.Msg($"settings opened: locale='{Locales.Code}' zh={Locales.IsChinese} " +
                        $"section='{Sections[0].Entry.Section}'");
            }
            catch { }
        }

        MethodInfo? setter = null;
        foreach (var (entry, zh, en) in Sections)
        {
            try
            {
                string desired = Locales.L(zh, en);
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
    private static Func<string> Text(string zh, string en) => () => Locales.L(zh, en);

    internal static bool Auth => AuthToggle.Value;
    internal static bool Value => ValueToggle.Value;
    internal static bool ValueEnhance => ValueEnhanceToggle.Value;
    internal static bool ModuleEffects => ModuleEffectsToggle.Value;
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
    internal static bool Wine => WineToggle.Value;
    internal static bool DoubleClickInspect => DoubleClickInspectToggle.Value;
    internal static bool HideEstimate => HideEstimateToggle.Value;
    internal static bool DimDescription => DimDescriptionToggle.Value;
    internal static string ItemNameMode => _nameMode?.Value ?? "容器名 (Containers)";
    internal static string ItemNamePosition => _namePos?.Value ?? "底部 (Bottom)";
    internal static float ItemNameScale => _nameScale?.Value ?? 35f;
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

        internal void Define(ModConfig cfg, string key, string sectionZh, string sectionEn,
            Func<string> label, Func<string> tip)
        {
            // 注册时一律用英文分组名，不在这里读语言（见 Init 里关于 Init 阶段不碰本地化的说明）。
            // 玩家能看到的标题由 RefreshSections 在面板打开前重写成当前语言。
            _entry = cfg.Define(key, _default, sectionEn, label, tip);
            Sections.Add((_entry, sectionZh, sectionEn));
        }

        internal bool Value => _entry?.Value ?? _default;
    }
}
