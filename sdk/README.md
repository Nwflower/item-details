# ItemDetailsClient 快速上手

`ItemDetailsClient.cs` 是给第三方 Mod 的**复制即用**客户端（C# 12+）：把文件放进你的工程，引用游戏程序集即可，不必引用 ItemDetails.dll。只支持旧语言版本的工程改用 `sdk/csharp5/ItemDetailsClient5.cs`——同一份协议，`LangVersion 5` 可编译，由 `tests/CSharp5Client` 在测试里校验。所有类型在 `ItemDetails.Integration` 命名空间内是 `internal` 的，不会污染你的公共 API。物品详情没装、没启用或版本不符时，每个入口都安静地返回 false，你照常走原版显示。

## 生命周期

```csharp
using ItemDetails.Integration;

public class MyMod : IMod
{
    private IDisposable? _watch;
    private IDisposable? _claim;

    public void OnEnable()
    {
        _watch = ItemDetailsClient.WatchTooltipSections();
        _claim = ItemDetailsClient.RegisterTooltipSections(OnSection);
    }

    public void OnDisable()
    {
        _claim?.Dispose(); _claim = null;
        _watch?.Dispose(); _watch = null;
    }

    // 只接管你这次确实画得出的区块；其余保留物品详情的输出。
    private bool OnSection(GameItem item, RichTextBuilder builder, TooltipSection section)
    {
        if (section.Id != TooltipSectionIds.Appraisal) return false;
        if (!section.TryGetAppraisal(out var appraisal)) return false;
        _pendingAppraisal = appraisal;   // 本次构建暂存；tooltip 关闭时清理
        return true;
    }
}
```

## 在原版钩子里读取整份提示框

物品详情的区块快照按 `RichTextBuilder` 实例归档。你在 `ModHook.OnCreateTooltipLate` 里拿到的就是同一个 builder，直接查询：

```csharp
private void OnTooltipLate(RichTextBuilder builder, GameItem item)
{
    if (!ItemDetailsClient.TryGetTooltipSections(builder, out var sections)) return;
    foreach (var section in sections)
    {
        if (!section.CanReplace) continue;   // 原生行的展示，别删除，改写对应原生行
        foreach (var run in section.Runs)
        {
            // run.Kind: text / line / break / bar（bar 带 Ratio、Length、Tail）
            ReformatMyStyle(run, section.Fields);
        }
    }
}
```

## 成分表的可见行

`water` 区块除了 `analysis`（原始成分），还带一份 `rows`：这一帧**实际画出来的**表格行，已经按 Ctrl 状态折叠过。

```csharp
if (section.Id == TooltipSectionIds.Liquid && section.WaterRows.Count > 0)
{
    foreach (var row in section.WaterRows)
    {
        // row.Kind: WaterRowKinds.Component（三格都有值）或 WaterRowKinds.FoldHint（用量为空）
        // row.Name / row.Amount / row.Percent 已经是分好的格子，不必再按全角空格或 <pos> 反推
        DrawMyColumn(row.Name, row.Amount, row.Percent);
    }
}
```

- 表头（容量条、档位）不在 `rows` 里，用 `analysis` 或 `Runs` 里的 `bar` 段。
- 行序就是输出顺序，折叠提示行排在最后一行成分之后。它的百分比是被折起来那几项的合计，没有折起来的成分时为空。
- 液体容器和固体化学品发同一份结构，用量格的单位由物品详情按自己的规则写好。固体里只有带化学纯度的化学品（药片、粉末）才画成分行；作物、粪便、片剂这类只发药效结论，`rows` 会是空的。

C# 5 的老编译器也能读，不必复制 `ItemDetailsClient.cs`：按字段名取，值类型是 BCL 元组。

```csharp
// C# 5：用 -langversion:5、引用游戏 Managed 的 mscorlib/netstandard 已验证可编译。
private static void ReadWaterRows(IReadOnlyDictionary<string, object> fields)
{
    object raw;
    if (!fields.TryGetValue("rows", out raw)) return;
    var rows = raw as IReadOnlyList<ValueTuple<string, string, string, string>>;
    if (rows == null) return;
    foreach (var row in rows)
    {
        string kind = row.Item1;     // "component" / "fold-hint"
        string name = row.Item2;
        string amount = row.Item3;   // 折叠提示行为空
        string percent = row.Item4;  // 含 %，可能为空
    }
}
```

## 规则清单

- **可用性**：`SectionProtocolAvailable` / `LiquidProtocolAvailable` 反映物品详情是否在线；区块是否**适用**仍以快照/查询返回为准（设置关闭、缺扫描仪时返回空）。
- **接管要成套**：`RegisterTooltipSections` 的回调只在本次构建区块准备完成后返回 true；返回 false 或抛错都算不接管。接管 `contents`/`sealed-contents` 时同步用 `RegisterTooltipLayout` 声明 `DrawsContents`，让分类徽章和图标浮窗贴你的窗口。
- **字段只增不改**：区块 ID 用 `TooltipSectionIds` 常量；读取用 `TryGet` 并给默认值。`CanReplace=false` 的区块（`label`、`manufacturer`、`inspect-hint`、`liquid-volume`、`purchase`、`description` 等）只发布原生行的数据，那一行仍由游戏写出，接管压不掉它。`price`、`calories`、`machine-effect` 由物品详情整行写出，可以接管。每个区块的 runs 与 builder 里那一行文本一致；原版窗口里个别行的换词（如购买价格→进价、ml→L）只在渲染时换入，不进 runs。
- **离线契约样例**：`tests/fixtures/sections/contract.txt` 是固定物品的区块快照（id、`group`、字段名与标量、每段 run 的形状），不启动游戏就能拿来测归类。
- **布局声明**：附加进度条统一走游戏 `AddProgressBar`，成分表统一提供自然文本，旧 `NativeProgressBars` / `FlowingText` 声明继续有效且无需迁移。回调返回 null 表示本次不接管排版。
- **旧版支持**：新字段和槽位可选，已有消费者无需更新 SDK。SectionedTooltips 1.6.2 / 1.6.3 的配置兼容由物品详情维护；范围、显式协议优先与 v1 冻结范围见[兼容契约](../COMPATIBILITY.md)。徽章未显示时槽位返回 null，旧节点名仍保留。
- **锚点**：`RegisterTooltipAnchor` 每次返回当前可见提示框的 `RectTransform`；窗口重建后无需重新注册。
- **液体**：`TryGetLiquidAnalysis` / `TryGetWaterAdvice` 与通用区块独立。净化收益单位是**百分点**；空列表是有效结果（当前无可列手段）。0.9.0 起两者各受一个开关控制：液体组分分析关闭时 `TryGetLiquidAnalysis` 返回空，`TryGetWaterAdvice` 仍照常返回；只有分析设为「有扫描器时开启」时，净化建议才一起要求扫描器。见[兼容契约](../COMPATIBILITY.md)的「显隐变更」。
- **线程与清理**：全部在游戏主线程；每帧悬停可能重建。逐件暂存随本次构建清理，句柄在 `OnDisable` 释放。

字段与区块清单见 [INTEGRATION.md](INTEGRATION.md)。
