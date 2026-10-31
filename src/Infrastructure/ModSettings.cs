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
/// 拆分过的项（旧值迁移到全部新键，见 <see cref="MigrateLegacyAppraisalToggle"/>）：
///   show_item_appraisal → show_authenticity（真伪鉴定）+ show_valuation（估价计算），并移到「透视」
/// 换了控件的项：
///   show_container_name_bg（开关）→ container_name_bg_opacity（0–100 滑条；旧值关 → 0）
/// 换了分组的项（键不变，值原样保留）：
///   show_customer_alert：显示优化 → 透视
///
/// 合并过的项（旧键已废弃，读取时回落到默认；水质旧键有迁移，见 <see cref="MigrateLegacyWaterToggle"/>）：
///   show_berry            + show_seed_traits      → show_plant
///   show_lock_preview     + show_scratcher（现已按分级拆回独立开关，默认值不同）
///   show_storage_ratio    + show_container_contents → show_container
///   show_label_impact     + show_condition_impact  → show_price_impact
///   show_value + show_perfect_range + show_ribwich_reasons
///               + show_generic_breakdown + show_appraisal_title → show_value
///   show_feature_reveal   + show_hidden_real       → show_hidden_truth
///   show_water_purity     + show_water_advice      → liquid_analysis（三档下拉）
///   show_wine                                      → show_info_enhance（果酿并入信息增强）
///   block_progress_bar    + manufacturer_inline    → show_info_enhance
///   show_contents_grid                              取消（图标格常开，默认即按图标显示）
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
    private static readonly Toggle CostPerformanceToggle = new(true);    // 性价比 + 压缩率
    private static readonly Toggle ContainerContentsToggle = new(true);  // 容器内容
    private static readonly Toggle InfoEnhanceToggle = new(true);        // 标签/模块/电池/黑市/彩票/描述/价格
    private static readonly Toggle TypeBadgeToggle = new(true);          // tooltip 上方分类标签
    private static readonly Toggle TypeBadgeMutedToggle = new(false);    // 非违禁标签淡化配色
    private static readonly Toggle ContainerNameToggle = new(false);     // 容器和机器外显文本
    private static readonly Toggle CustomerHistoryToggle = new(true);    // 顾客对话历史（悬停）

    // ---- 操作优化 ----
    private static readonly Toggle DoubleClickInspectToggle = new(true);
    private static readonly Toggle DoubleClickScanToggle = new(true);    // 双击扫描

    // ---- 透视 ----
    private static readonly Toggle AuthenticityToggle = new(true);       // 真伪结论 + 赝品原因 + 植物品质
    private static readonly Toggle ValuationToggle = new(true);          // 价值构成 + 完美区间 + 鉴定标题报价
    private static readonly Toggle CustomerAlertToggle = new(true);      // 顾客到店提示
    private static readonly Toggle HiddenTruthToggle = new(true);        // 揭示伪装与真实属性
    private static readonly Toggle ScratcherToggle = new(false);         // 刮刮乐预知
    private static readonly Toggle SealedBoxToggle = new(false);         // 带锁容器透视
    private static readonly Toggle ResultPrefillToggle = new(false);     // 鉴定结果预填

    /// <summary>液体组分分析：三档。选项是纯字符串（ModConfig 不做本地化），沿用中英并列写法。</summary>
    private static ConfigEntry<string>? _liquidAnalysis;
    internal const string LiquidOffChoice = "关闭 (Off)";
    internal const string LiquidScannerChoice = "有扫描器时开启 (With scanner)";
    internal const string LiquidAlwaysChoice = "总是开启 (Always)";
    private const string DefaultLiquidChoice = LiquidScannerChoice;

    /// <summary>「鉴定结果预填」下面那项：预填哪种价格。选项是纯字符串，中英并列。</summary>
    private static ConfigEntry<string>? _prefillPrice;
    private const string PrefillTrueValueChoice = "真实估价 (True value)";
    private const string PrefillBestOfferChoice = "最优报价 (Best offer)";

    /// <summary>外显文本底色的不透明度（0–100，滑条）；0 即不加底色。</summary>
    private static ConfigEntry<int>? _nameBgOpacity;
    private const int DefaultNameBgOpacity = 60;

    internal static void Init(string modId)
    {
        try
        {
            _cfg = ModConfig.For(modId);
            Sections.Clear();

            // ================= 显示优化 =================
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

            // 有范围的 int 条目，面板渲染成整数滑条。
            _nameBgOpacity = _cfg.Define("container_name_bg_opacity", DefaultNameBgOpacity, SecDisplayEn,
                Text("config.container_name_bg", "Label Background"), 0, 100,
                Text("config.container_name_bg.tip", "Opacity of the black background behind the label (0–100%). " +
                     "At 0 there is no background."));

            CustomerHistoryToggle.Define(_cfg, "show_customer_history", SecDisplayKey, SecDisplayEn,
                Text("config.customer_history", "Customer Dialogue on Hover"),
                Text("config.customer_history.tip", "Hover a customer in the store to see what was said during this " +
                     "visit. Cleared when the next customer arrives."));

            // ================= 操作优化 =================
            DoubleClickInspectToggle.Define(_cfg, "double_click_inspect", SecInteractionKey, SecInteractionEn,
                Text("config.double_click_inspect", "Double-Click to Inspect"),
                Text("config.double_click_inspect.tip", "Double-click an appraisal item or an authenticity-check item " +
                     "(injectors, cigarettes, food stamps, etc.) to open the inspection panel without the magnifying " +
                     "glass. While the panel is open, double-click another item to switch to it. The magnifying-glass " +
                     "hint becomes \"Double-Click To Inspect\". Turn off to restore the original flow."));

            // 紧跟在「双击鉴定」下面：同样是双击接管，只是目标换成了扫描面板。
            DoubleClickScanToggle.Define(_cfg, "double_click_scan", SecInteractionKey, SecInteractionEn,
                Text("config.double_click_scan", "Double-Click to Scan"),
                Text("config.double_click_scan.tip", "While you own the matching device, double-click an item to open " +
                     "its scan panel: water or chem scanner for liquids and chemicals, X-ray scanner for locked supply " +
                     "crates, plant analyser for seeds. Without the device the original double-click still applies."));

            // ================= 透视 =================
            AuthenticityToggle.Define(_cfg, "show_authenticity", SecRevealKey, SecRevealEn,
                Text("config.authenticity", "Authenticity Check"),
                Text("config.authenticity.tip", "Tells genuine from counterfeit and names the flaws, explains why a " +
                     "ribwich is counterfeit, and shows bloomberry quality and unanalyzed seed traits."));

            ValuationToggle.Define(_cfg, "show_valuation", SecRevealKey, SecRevealEn,
                Text("config.valuation", "Appraisal Calculator"),
                Text("config.valuation.tip", "Shows the true value breakdown and perfect price range of appraisal " +
                     "items, and the list price and best offer in the appraisal service title."));

            CustomerAlertToggle.Define(_cfg, "show_customer_alert", SecRevealKey, SecRevealEn,
                Text("config.customer_alert", "Customer Arrival Alert"),
                Text("config.customer_alert.tip", "When a wanted or augmented customer arrives, shows a notice with the " +
                     "actual bounty, real name, and an augmented customer's kill reward and threat level."));

            HiddenTruthToggle.Define(_cfg, "show_hidden_truth", SecRevealKey, SecRevealEn,
                Text("config.hidden_truth", "Reveal Disguises & True Stats"),
                Text("config.hidden_truth.tip", "Shows what disguised features really are, true durability, and true " +
                     "ingot/chemical purity. Exposes fakes directly and makes appraisal much easier."));

            _liquidAnalysis = _cfg.Define("liquid_analysis", DefaultLiquidChoice, SecRevealEn,
                Text("config.liquid_analysis", "Liquid Composition Analysis"),
                new[] { LiquidOffChoice, LiquidScannerChoice, LiquidAlwaysChoice },
                Text("config.liquid_analysis.tip", "Shows water purity and impurities, and the composition and effects " +
                     "of drugs and chemicals. \"With scanner\" only shows it while you own a water or chemical scanner."));

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
                     "and fills the appraisal price with the true value or the best offer, as chosen below. It only " +
                     "fills in the controls and never submits or confirms. Off by default."));

            // 下拉框的选项是纯字符串（ModConfig 不做本地化），沿用本工程既有写法：中英并列。
            _prefillPrice = _cfg.Define("result_prefill_price", PrefillBestOfferChoice, SecRevealEn,
                Text("config.result_prefill_source", "Prefilled Price"),
                new[] { PrefillTrueValueChoice, PrefillBestOfferChoice },
                Text("config.result_prefill_source.tip",
                     "True value fills the game's real value; best offer fills the top of the perfect range. " +
                     "Same rule the tooltip shows."));

            MigrateLegacyAppraisalToggle();
            MigrateLegacyNameBgToggle();
            MigrateLegacyWaterToggle();

            _cfg.Save();

            // 漏网的条目也要登记：`_cfg.Define` 直接注册的下拉框没有经过
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
    /// 旧版用来控制水质面板的布尔键，按"新 → 旧"顺序尝试。0.7.x 是 <c>show_water_purity</c>；
    /// <c>show_water_advice</c> 见于更早的合并记录，这里一并兼容。
    /// </summary>
    private static readonly string[] LegacyWaterKeys = { "show_water_purity", "show_water_advice" };

    /// <summary>
    /// 把旧版布尔「水质检测」迁移到新版三档 <c>liquid_analysis</c>：
    ///   · 旧值开 → 有扫描器时开启（With scanner）
    ///   · 旧值关 → 关闭（Off）
    ///
    /// 只在文件里还没有 <c>liquid_analysis</c> 时迁移。最后一个公开版本 0.7.1 只写
    /// <c>show_water_purity</c>，三档是它之后才加的，所以从公开版升级上来的玩家一定没有新键；
    /// 已经有新键的说明跑过带三档的构建或手改过文件，那就是玩家当前的选择，旧键不再插手。
    ///
    /// 迁移后不需要额外标记：`Init` 末尾的 `_cfg.Save()` 会把新键写进文件，下次启动就不再满足条件。
    /// 旧键不删——`ModConfig.Save()` 会保留未注册条目，退回旧版本时原值还在。
    ///
    /// 读旧键只能走 <see cref="ModConfig"/> 的私有 <c>rawValues</c>：公开 API 只按已注册的键取值，
    /// 对旧键 <c>Get(key)</c> 返回 null；改读配置文件则要 <c>files</c> 能力，不值得。
    /// 取不到字段就当没配过，不猜。
    /// </summary>
    private static void MigrateLegacyWaterToggle()
    {
        if (_cfg == null || _liquidAnalysis == null) return;
        try
        {
            var raw = RawValues();
            if (raw == null) return;

            if (raw.ContainsKey("liquid_analysis")) return;   // 已有新键：玩家当前的选择优先

            foreach (string key in LegacyWaterKeys)
            {
                if (!raw.TryGetValue(key, out string? stored)) continue;
                if (!bool.TryParse(stored, out bool on))
                {
                    Log.Warn("ModSettings.Migrate", $"旧设置 {key}='{stored}' 不是布尔值，保留新默认");
                    return;
                }

                _liquidAnalysis.Set(on ? LiquidScannerChoice : LiquidOffChoice);
                Log.Msg($"ModSettings: 旧设置 {key}={stored} → liquid_analysis={_liquidAnalysis.Value}");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("ModSettings.Migrate", ex);
        }
    }

    /// <summary>
    /// 把旧版「物品鉴定」（<c>show_item_appraisal</c>）的取值带给拆出来的两个开关：
    /// 旧值关，真伪鉴定与估价计算都关；旧值开（或没配过）就保持新默认（都开）。
    ///
    /// 每个新键单独判断：文件里已经有某个新键，说明玩家在新版里设过它，不再覆盖。
    /// 旧键不删，理由同 <see cref="MigrateLegacyWaterToggle"/>。
    /// </summary>
    private static void MigrateLegacyAppraisalToggle()
    {
        if (_cfg == null) return;
        try
        {
            var raw = RawValues();
            if (raw == null || !raw.TryGetValue("show_item_appraisal", out string? stored)) return;
            if (!bool.TryParse(stored, out bool on))
            {
                Log.Warn("ModSettings.Migrate", $"旧设置 show_item_appraisal='{stored}' 不是布尔值，保留新默认");
                return;
            }

            foreach (var (toggle, key) in new[] { (AuthenticityToggle, "show_authenticity"), (ValuationToggle, "show_valuation") })
            {
                if (raw.ContainsKey(key)) continue;
                toggle.Set(on);
                Log.Msg($"ModSettings: 旧设置 show_item_appraisal={stored} → {key}={on}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("ModSettings.Migrate", ex);
        }
    }

    /// <summary>
    /// 旧版布尔「名称标签底色」（<c>show_container_name_bg</c>）→ 不透明度滑条：
    /// 旧值关 → 0（不加底色）；旧值开就保持新默认 60%，与旧版固定的不透明度一致。
    /// 文件里已有新键时不覆盖；旧键不删，理由同 <see cref="MigrateLegacyWaterToggle"/>。
    /// </summary>
    private static void MigrateLegacyNameBgToggle()
    {
        if (_cfg == null || _nameBgOpacity == null) return;
        try
        {
            var raw = RawValues();
            if (raw == null || raw.ContainsKey("container_name_bg_opacity")) return;
            if (!raw.TryGetValue("show_container_name_bg", out string? stored)) return;
            if (!bool.TryParse(stored, out bool on))
            {
                Log.Warn("ModSettings.Migrate", $"旧设置 show_container_name_bg='{stored}' 不是布尔值，保留新默认");
                return;
            }
            if (on) return;

            _nameBgOpacity.Set(0);
            Log.Msg($"ModSettings: 旧设置 show_container_name_bg={stored} → container_name_bg_opacity=0");
        }
        catch (Exception ex)
        {
            Log.Warn("ModSettings.Migrate", ex);
        }
    }

    /// <summary>
    /// 配置文件里的原始键值（含未注册的旧键）。公开 API 只按已注册的键取值，
    /// 读旧键只能走 <see cref="ModConfig"/> 的私有 <c>rawValues</c>；取不到就当没配过，不猜。
    /// </summary>
    private static Dictionary<string, string>? RawValues()
    {
        if (AccessTools.Field(typeof(ModConfig), "rawValues")?.GetValue(_cfg) is Dictionary<string, string> raw)
            return raw;
        Log.Warn("ModSettings.Migrate", "读不到 ModConfig.rawValues，旧设置不迁移");
        return null;
    }

    /// <summary>
    /// 英文分组名 → 本地化键。用于把「注册时写下的英文分组名」反查回键，
    /// 这样不管是 Toggle.Define 还是直接 _cfg.Define 注册的条目都能被重写。
    /// </summary>
    private static readonly (string En, string Key)[] SectionMap =
    {
        (SecDisplayEn, SecDisplayKey),
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

    internal static bool Auth => AuthenticityToggle.Value;
    internal static bool Value => ValuationToggle.Value;
    internal static bool Plant => AuthenticityToggle.Value;
    internal static bool InfoEnhance => InfoEnhanceToggle.Value;
    internal static bool ValueEnhance => CostPerformanceToggle.Value;
    internal static bool Container => ContainerContentsToggle.Value;
    internal static bool ContentsGrid => true;   // 图标格常开，默认按图标显示内容物
    internal static bool ModuleEffects => InfoEnhanceToggle.Value;
    internal static bool MachineBattery => InfoEnhanceToggle.Value;
    internal static bool PriceImpact => InfoEnhanceToggle.Value;
    internal static bool Contraband => InfoEnhanceToggle.Value;
    internal static bool Lottery => InfoEnhanceToggle.Value;
    internal static bool HideEstimate => InfoEnhanceToggle.Value;
    internal static bool DimDescription => InfoEnhanceToggle.Value;
    internal static bool CustomerAlert => CustomerAlertToggle.Value;
    internal static bool CustomerHistory => CustomerHistoryToggle.Value;
    internal static bool HiddenTruth => HiddenTruthToggle.Value;
    internal static bool Wine => InfoEnhanceToggle.Value;   // 果酿并入「信息增强」
    internal static bool Scratcher => ScratcherToggle.Value;
    internal static bool SealedBox => SealedBoxToggle.Value;
    internal static bool ResultPrefill => ResultPrefillToggle.Value;

    /// <summary>液体组分分析的三档。显示与否还要过 <see cref="LiquidGate"/>（“有扫描器”要查拥有关系）。</summary>
    internal enum LiquidMode
    {
        Off,
        WithScanner,
        Always,
    }

    internal static LiquidMode LiquidAnalysis
    {
        get
        {
            string value = _liquidAnalysis?.Value ?? DefaultLiquidChoice;
            if (string.Equals(value, LiquidAlwaysChoice, StringComparison.Ordinal)) return LiquidMode.Always;
            if (string.Equals(value, LiquidOffChoice, StringComparison.Ordinal)) return LiquidMode.Off;
            return LiquidMode.WithScanner;
        }
    }

    /// <summary>预填价格用「最优报价」；为 false 时用真实估价。</summary>
    internal static bool PrefillBestOffer =>
        !string.Equals(_prefillPrice?.Value, PrefillTrueValueChoice, StringComparison.Ordinal);

    internal static bool DoubleClickInspect => DoubleClickInspectToggle.Value;
    internal static bool DoubleClickScan => DoubleClickScanToggle.Value;
    internal static bool TypeBadge => TypeBadgeToggle.Value;
    internal static bool TypeBadgeMuted => TypeBadgeMutedToggle.Value;
    internal static bool BlockProgressBar => InfoEnhanceToggle.Value;
    internal static bool ManufacturerInline => InfoEnhanceToggle.Value;
    internal static string ItemNameMode => ContainerNameToggle.Value ? "容器名 (Containers)" : "无 (Off)";
    internal static string ItemNamePosition => "底部 (Bottom)";
    internal static bool ItemNameBackground => NameBgOpacity > 0;
    internal static float ItemNameBackgroundAlpha => NameBgOpacity / 100f;
    private static int NameBgOpacity => Math.Max(0, Math.Min(100, _nameBgOpacity?.Value ?? DefaultNameBgOpacity));

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

        /// <summary>迁移旧设置用；不单独保存，由 Init 末尾统一 Save。</summary>
        internal void Set(bool value) => _entry?.Set(value);
    }
}
