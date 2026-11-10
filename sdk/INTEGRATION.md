# 第三方接入参考

给想读取或接管物品详情输出的模组作者。先看 [快速上手](README.md) 把客户端跑起来，再回来查这里的字段、区块与规则；
哪些东西冻结、哪些是可选升级，见 [兼容契约](../COMPATIBILITY.md)。

## 可选的液体数据客户端（v1）

把 [ItemDetailsClient.cs](ItemDetailsClient.cs) 复制到你的工程，使用 C# 12 或以上编译，并引入 `ItemDetails.Integration` 命名空间。只需引用游戏程序集。客户端随你的 Mod 编译，玩家可以单独安装任一 Mod。完整上手示例与规则清单见 [sdk/README.md](README.md)。

如果你在已有界面中读取数据，直接调用 `TryGetLiquidAnalysis(item, out var analysis)` 或 `TryGetWaterAdvice(item, out var methods)`。查询失败时继续走你已有的原版显示分支。查询本身不会接管任何提示行。

如果你会重绘整个提示框，可以只注册你能完整展示的区块：

```csharp
// OnEnable 中注册，OnDisable 中调用 _liquidDisplay?.Dispose()。
_liquidDisplay = ItemDetailsClient.RegisterLiquidAnalysisDisplay((item, analysis) =>
{
    if (!CanDrawLiquidTable(item)) return false;
    // 这里调用你自己的排版方法，准备好本次 tooltip 的容量、成分和药效。
    return TryPrepareLiquidTable(item, analysis);
});
```

- 数据和回调在游戏主线程使用。上述回调在对应物品的提示框构建期间执行；仅在替代区块准备成功后返回 true。返回 false 或抛错时会尝试下一个显示方，全部放弃则由物品详情输出。区块应随本次构建清理，包括下一件物品无数据、用户关闭分析的情况。
- `analysis.Components` 按含量降序，含稳定 ID、当前语言名称、整数微升和角色（`water`、`active`、`other`）。占比的分母是 `TotalMicroliters`，包含已隐藏的微量成分。`CapacityMicroliters` 为 0 表示无容量信息，`IsSolid` 区分固体药物。可从 `Tier`、`LabelMismatch` 和 `Effects` 取水质档位、标签差异和药效（致死倍数、毒性、欣快等级、戒瘾、避孕）。`Effects.Text` 可直接显示当前语言的药效说明。要看这一帧实际画出的表格行，用区块快照的 `WaterRows`，它已经按 Ctrl 状态折叠过、用量也按该物品的规则格式化好了。
- 用 `RegisterWaterAdviceDisplay` 可单独接管净化建议。每项包括当前语言名称和纯度提升的**百分点**。成功的空列表表示该物品当前没有可列出的手段。只查成分不会触发净化模拟。
- 两项查询都遵守「液体组分分析」的设置与扫描仪条件。未安装、未启用、旧版或数据不可用时查询返回 false。注册可以早于物品详情加载。已用旧接口接管同一信息块的 Mod，迁移时应移除对应的旧接管标志，再注册新回调。
- 客户端通过带版本号的 AppDomain 键传递游戏和 .NET 自带类型。对现有签名作不兼容更改时应新增版本键。SDK 类型仅在各自程序集内部使用。
- v1 冻结哪些东西、新能力为什么可选、旧版由谁维护，统一见 [兼容契约](../COMPATIBILITY.md)。

## 通用提示框区块（v1）

液体接口只覆盖两个区块。需要重排其它内容时，用同一客户端的区块协议，不必解析物品详情写出的文字：

```csharp
// 观察：OnEnable 注册一次。之后对本次 GetTooltipBasic 用过的 builder 查询。
_watch = ItemDetailsClient.WatchTooltipSections();
if (ItemDetailsClient.TryGetTooltipSections(builder, out var sections))
    foreach (var section in sections) Reformat(section);

// 接管：回调返回 true 表示本区块由你绘制，物品详情不再写它的默认文本。
// 返回 false 或抛错时保留物品详情的输出。观察与接管可只用一个。
_claim = ItemDetailsClient.RegisterTooltipSections((item, builder, section) =>
    section.Id == "value" && TryPrepareAppraisal(section));
```

- `section.Runs` 是这次构建实际要输出的有序片段：`text`（行内文字）、`line`（整行）、`break`（换行）、`bar`（进度条，带 0–1 比例、格数与后缀）。每段带游戏调色板枚举。直接复用这些片段即可换行、缩进、改列宽，不必再猜字符串。
- `section.Fields` 是结构化字段，键为字符串，值为 BCL 元组、基础类型或游戏类型。列表型区块（滤芯、工具、电池、性状、特性等）把逐行数据放在 `rowData`：每项是 `(行号, 数据键, 值)`，行号对应 `Runs` 下标。常用区块有便捷读取：`TryGetAppraisal`（总价、完美区间、逐项金额）、`TryGetMachineEffect`（性能/效率/质量注释与数值差）、`Liquid`（同液体分析快照）、`WaterRows`（`water` 区块这一帧实际画出的成分行）、`PurificationMethods`（净化建议）。`WaterRows` 每项是 `(Kind, 名称, 用量, 百分比)`，Kind 取 `WaterRowKinds.Component` 或 `WaterRowKinds.FoldHint`；折叠提示行的用量为空，百分比是被折起来那几项的合计。行序即输出顺序，表头不在其中。
- `water` 区块还有一个 `trace` 字段：“（另 N 项低于显示阈值）”那一行的整行文本，只有存在隐藏成分时才有。它不是成分行，不在 `WaterRows` 里；读取用 SDK 的 `TryGetTrace`。
- `section.CanReplace` 为 false 的区块（`label`、`manufacturer`、`inspect-hint`、`liquid-volume`、`purchase`、`description` 等）只发布原生行的数据，那一行仍由游戏写出，接管方应改写对应原生行而不是删除。
- 每个区块另带 `Fields["group"]`：这一块在玩家眼里属于哪类信息，取值见 `TooltipGroupIds`（`liquid`、`plant`、`machine`、`condition`、`appraisal`、`food`、`lottery`、`brew`、`module`、`label`、`description`、`price`、`contents`）。只增不改；读取方遇到不认识的组按原样保留该行。用组归类可以不必为每个新 id 补映射。
- 区块 ID 即功能名：`auth`、`value`、`injected-value`、`ribwich`、`berry`、`hidden`、`value-enhance`、`feature-reveal`、`storage`、`machine-interior`、`module-learning`、`conditions`、`contraband`、`lottery`、`water`、`sealed-box`、`scratcher`、`seed`、`water-advice`、`wine`、`meat`、`market`，以及内嵌改写 `price`、`calories`、`machine-speed`、`machine-power`、`machine-effect`、`categories`、`contents`、`sealed-contents`、`label`、`manufacturer`、`inspect-hint`。字段随功能演进可能新增，读取方用 `TryGet` 并给默认值。
- 已用旧 `ItemDetailsTooltipSection` 标志接管的区块仍按旧行为跳过，不产生快照。两种接管不要对同一区块混用。
- 快照按 `RichTextBuilder` 实例和构建序号归档；每次重新构建会替换上次内容。只在游戏主线程查询。

## 可选客户端对整个提示框的适配

同一份客户端也支持声明排版和内容物接管。重绘方在自己的 `OnEnable` 注册，在 `OnDisable` 释放两个句柄：

```csharp
_layout = ItemDetailsClient.RegisterTooltipLayout(item =>
{
    if (!WillRenderTooltip(item)) return null;
    var options = TooltipLayoutOptions.NativeProgressBars | TooltipLayoutOptions.FlowingText;
    if (WillDrawContents(item)) options |= TooltipLayoutOptions.DrawsContents;
    return options;
});
_anchor = ItemDetailsClient.RegisterTooltipAnchor(() => currentVisibleTooltipRect);
```

进度条一律由游戏的 `AddProgressBar` 写出，成分表一律提供自然文本；sprite 与 `<pos>` 呈现留在原版窗口的渲染层。`NativeProgressBars` / `FlowingText` 的位及原有输出保证保留，旧消费者无需改声明。`DrawsContents` 只对本次物品让出内容物区块，声明前应确认本次能展示该区块（包括上锁容器的显示资格）。布局回调返回 null 时依次尝试其他显示方，首个非 null 声明用于本次构建；返回 `None` 表示本次由你绘制并使用默认样式。回调只读取本次排版状态。

未接入协议的 SectionedTooltips 1.6.2 / 1.6.3 仍由我方旧兼容层支持：尊重启用状态和 `Enabled` / `ContentsPanel` 开关，只让出普通容器图标格，不接管密封箱透视、压缩率或机器内部信息。协议优先与维护承诺见[兼容契约](../COMPATIBILITY.md)第 9–10 条。

分类徽章容器也可以经 `RegisterTooltipBadge(() => rect)` 发布（消费者用 `TryGetTooltipBadge` 读），排版方据此量高度，不必按节点名 `ItemDetailsTypeBadge` 找 GameObject。锚点用于分类徽章与旁边的内容物浮窗。回调每次返回当前可见的提示框，无可用窗口时返回 null。已有直接引用接口的锚点优先；使用可选客户端时优先采用首个有效锚点。

徽章槽位只返回可见容器，隐藏或未创建时返回 null；由生产方保证这个条件。旧消费者按名字查找的 `ItemDetailsTypeBadge` 节点和锚点同级定位路径继续保留。

## 直接引用 ItemDetails.dll 的扩展接口

第三方适配采用**显式注册**，不让 Item Details 采样随机物品、反推定价、扫描 Harmony 补丁或反射查找第三方内部方法。唯一的自动识别是水质净化手段：只读物品回调的 IL，看它是否调用原版净化函数，不调用第三方代码（见下文约定）。提供方引用 `ItemDetails.dll`，在自己的 `OnEnable` 中注册，保存句柄并在 `OnDisable` 中释放：

```csharp
using System;
using ItemDetails;

private IDisposable? _appraisalRegistration;
private IDisposable? _machineRegistration;
private IDisposable? _purifierRegistration;
private IDisposable? _tooltipRegistration;
private IDisposable? _tooltipAnchorRegistration;
private IDisposable? _itemTooltipRegistration;

public void OnEnable()
{
    _appraisalRegistration = ItemDetailsApi.RegisterAppraisalBreakdown(
        "my_mod.appraisal_type",
        item =>
        {
            int total = MyRules.GetValue(item);
            return new ItemDetailsBreakdown(total, new[]
            {
                new ItemDetailsBreakdownPart("Base", 100, isBase: true),
                new ItemDetailsBreakdownPart("My attribute", 25),
            });
        });

    _machineRegistration = ItemDetailsApi.RegisterMachineEffects(
        "my_mod.machine",
        (machine, stat) => stat switch
        {
            ItemDetailsModuleStat.Performance => "+20% output",
            ItemDetailsModuleStat.Efficiency => "-3 power/day",
            _ => null,
        });

    _purifierRegistration = ItemDetailsApi.RegisterWaterPurifier(
        "my_mod.purifier_tablet", ItemDetailsPurifierKind.ChemicalAgent);

    // 自己通过 ModHook.OnCreateTooltipLate 显示内容物和液体分析时，按位选择要接管的信息块。
    _tooltipRegistration = ItemDetailsApi.RegisterTooltipDisplay("my_mod",
        ItemDetailsTooltipSection.Contents | ItemDetailsTooltipSection.LiquidAnalysis);

    // 如果本 Mod 会重建美化后的 tooltip，动态返回当前实例，让分类徽章贴到它上面。
    _tooltipAnchorRegistration = ItemDetailsApi.RegisterTooltipAnchor(() => MyTooltip.CurrentRect);

    // 重画所有物品的美化 Mod 按物品接管，比如只接管自己画了内容物的那些。
    _itemTooltipRegistration = ItemDetailsApi.RegisterTooltipDisplay(item =>
        MyTooltip.DrawsContents(item) ? ItemDetailsTooltipSection.Contents : ItemDetailsTooltipSection.None);
}

public void OnDisable()
{
    _appraisalRegistration?.Dispose();
    _machineRegistration?.Dispose();
    _purifierRegistration?.Dispose();
    _tooltipRegistration?.Dispose();
    _tooltipAnchorRegistration?.Dispose();
    _itemTooltipRegistration?.Dispose();
    _appraisalRegistration = null;
    _machineRegistration = null;
    _purifierRegistration = null;
    _tooltipRegistration = null;
    _tooltipAnchorRegistration = null;
    _itemTooltipRegistration = null;
}
```

约定：

- 鉴定注册键是 `AppraisalItemType.id`；机器注册键是物品的 `identifier`。同一键只允许一个提供方，重复注册会抛错，不隐式覆盖。
- 鉴定提供方返回自己计算的总价与结构化贡献。游戏能取得原生总价时，两者必须一致，否则本条分解不显示。注册后也接管同 ID 的原版精选分解，避免重复输出；注销才恢复。
- 机器提供方返回**已本地化、不含括号**的注释；`null` 或空串表示该项不写。注册后这个 ID 的原版注释也由提供方接管，注销才恢复。
- 净水手段注册键是物品的 `identifier`，种类决定水质建议用哪个原版函数模拟：`Machine` → `MachinePurifier.PurifyContainer`，`Portable` → `WaterHelper.PurifyToBaseWater`，`ChemicalAgent` → `ChemicalHelper.DissolveInto` 加反应链。不注册也会自动识别回调直接调用这些函数的物品（化学剂还要在标准试验水里确实提纯），注册后以注册为准；净化逻辑是自己写的、不走这些函数时，不要注册，水质建议也不会模拟它。
- `RegisterTooltipDisplay(modId, sections)` 按物品来源 Mod 接管选中的信息块。`modId` 通常填自己 manifest 的 ID，不是物品 `identifier`；来源识别与原版一致，优先读 `MOD_ID_TAG`，否则查官方物品工厂所属的 Mod。同一 ID 只允许一个注册，重复注册会抛错，注销后恢复。`sections` 是 `[Flags]`，可用 `|` 组合 `Authenticity`、`ValueBreakdown`、`BerryQuality`、`HiddenInfo`、`ValueEnhance`、`Contents`、`MachineInterior`、`ModuleLearning`、`ConditionEffects`、`Contraband`、`Lottery`、`LiquidAnalysis`、`Scratcher`、`Seed`、`WaterAdvice`、`Wine`、`Meat`、`Market`，或选 `All`。各值的精确范围见公开枚举注释；未选中的信息块不受影响。`RegisterContentsDisplay(modId)` 是只选 `Contents` 的便捷入口。
- 普通/密封容器的内容物只显示在额外图标窗口，不再有文字清单、数量摘要或独立箱内价值行；密封箱估价显示在图标窗口标题，嵌套容器或读取失败时用 `+` 表示下限额。`Contents` 接管会跳过这些图标窗口以及机器的通用内容物文字清单；`ValueEnhance` 单独控制压缩率，`MachineInterior` 单独控制滤芯、水量等状态。这个接口只避免 Item Details 重复追加信息，第三方替代内容仍通过官方 `ModHook.OnCreateTooltipLate` 添加。`All` 也不删除原版 tooltip，不禁用分类徽章、价格行改写等界面补丁。
- `RegisterTooltipAnchor` 用于美化 Mod 告诉分类徽章当前 tooltip 在哪里。固定实例可传 `RectTransform`；会重建 tooltip 时传 `Func<RectTransform?>`，每次显示都会调用。徽章贴在该提示框上方，父节点与 TMP 字体也取自它。内容物图标窗口也贴在它旁边。提供器返回 `null` 或抛错会回退原版；同时只允许一个锚点提供方，`Dispose` 后恢复原版定位。贴着锚点时内容物图标窗口不每帧置顶，免得和锚点那边抢最上层。
- `RegisterTooltipDisplay(item => sections)` 是逐件判断的版本，不看来源 Mod，原版物品也能接管。每次构建 tooltip 时调用，返回 `None` 表示这件不接管。适合重画所有物品的美化 Mod，比如只对自己画了内容物的物品返回 `Contents`，没画的（像上锁箱子）照旧由 Item Details 画。可以有多个注册，结果按位或；回调抛错的那个这次算不接管。
- 回调在游戏主线程、构建 tooltip 时执行，应保持只读和轻量。第三方私有状态与缓存由提供方自己管理。
- 只需要追加独立 tooltip 行时，直接使用游戏官方 `ModHook.OnCreateTooltipLate` 即可，不必注册到这里。
