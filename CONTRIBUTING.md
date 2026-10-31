# 参与开发与翻译

## 改翻译

译文放在 `Localization/<语言码>.csv`，每行 `key,value`。英文不在 CSV 里：它写在代码中，同时也是任何词条缺失或写坏时的兜底。

- 语言码与游戏一致：`zh` `ru` `de` `ja` `fr` `es` `pl` `it` `ko` `ptbr`。新增语言时复制 `zh.csv` 改名即可被自动载入。
- 首尾有空格或含逗号的值，用双引号整体包住；`{0}` 这类占位符不能删改。
- 物品名、分类名、厂商名尽量与游戏自己的译法一致。游戏的官方词表在安装目录 `Probably Stolen_Data/StreamingAssets/aa/StandaloneWindows64/localization-string-tables-*` 里，可以用 UnityPy 导出后按英文原文对照。
- 法语冒号、分号前用不换行空格，与游戏原文一致；韩语用半角标点。

改完运行校验，确认键齐全、没有多余词条、占位符与英文一致：

```powershell
python tools/check_localization.py --strict-placeholders
```

## 构建

需要 PowerShell 7.4+、.NET SDK 6 或更高版本，以及 Python 3.11+。

游戏目录在构建时传入，或设置环境变量 `PS_GAME_DIR`。推荐使用统一入口：

```powershell
pwsh -NoProfile -File scripts/build.ps1 -GameDir "<游戏目录>"
```

默认编译 Release，并生成 `dist/item_details-<ModVersion>.zip`。
ZIP 内是 `item_details/`，包含 DLL、manifest、当前源码中的资源和许可/说明文件。
资源从源码目录收集，旧 `bin/` 里遗留的文件不会进入发布包；PDB、deps.json 和 `.gitkeep` 也不打包。
游戏、Unity 和 Harmony 程序集只作为本地引用，不分发。

需要排查问题时可以打开诊断构建：

```powershell
# 信息、性能与界面诊断日志
pwsh -NoProfile -File scripts/build.ps1 -GameDir "<游戏目录>" -Variant Diag

# 实机自检：载入存档后跑一轮用例并退出（同时包含诊断日志）
pwsh -NoProfile -File scripts/build.ps1 -GameDir "<游戏目录>" -Variant SelfTest
```

三种构建分别输出到 `bin/Release/netstandard2.1/`、`bin/Diag/netstandard2.1/` 和
`bin/SelfTest/netstandard2.1/`。每次显式设置诊断标志，只有 Release 生成 ZIP。
`ItemDetails.csproj` 仍可用 `dotnet build -p:GameDir=...` 直接编译。

## 检查

```powershell
# 无需游戏或 Unity Editor：公式、本地化、诊断报告与配置恢复回归
pwsh -NoProfile -File scripts/test.ps1

# 有游戏程序集时：另编译三种构建，检查原生契约、包内容和能力扫描
pwsh -NoProfile -File scripts/test.ps1 -GameDir "<游戏目录>"
```

带游戏目录的检查会编译自检构建；游戏内用例仍需按下文启动参数运行。
单个回归检查保留在 `tests/` 中，可按需单独运行。

## 游戏内自检

自检会调用真实售水等行为，先创建可丢弃的专用测试存档。用 `save_<id>.es3` 文件中的内部编号选择槽位，
不是存档列表的显示顺序。启动参数为 `-itemdetails-selftest -itemdetails-selftest-slot <id>`，
或在 `ModData/item_details/selftest.flag` 中写入该编号。空文件、无效编号、编号冲突和不存在的存档均报错退出，
不选择最近存档。自检临时开启所需设置，结束或出错时恢复，不写入配置。
`ModData/item_details/selftest.txt` 记录本次 run ID 与 `RUNNING`/`PASS`/`FAIL` 状态；失败退出码为 1。

## 第三方模组接入

第三方适配采用**显式注册**，不让 Item Details 采样随机物品、反推定价、扫描 Harmony 补丁或反射查找第三方内部方法。唯一的自动识别是水质净化手段：只读物品回调的 IL，看它是否调用原版净化函数，不调用第三方代码（见下文约定）。提供方引用 `ItemDetails.dll`，在自己的 `OnEnable` 中注册，保存句柄并在 `OnDisable` 中释放：

```csharp
using System;
using ItemDetails;

private IDisposable? _appraisalRegistration;
private IDisposable? _machineRegistration;
private IDisposable? _purifierRegistration;

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
}

public void OnDisable()
{
    _appraisalRegistration?.Dispose();
    _machineRegistration?.Dispose();
    _purifierRegistration?.Dispose();
    _appraisalRegistration = null;
    _machineRegistration = null;
    _purifierRegistration = null;
}
```

约定：

- 鉴定注册键是 `AppraisalItemType.id`；机器注册键是物品的 `identifier`。同一键只允许一个提供方，重复注册会抛错，不隐式覆盖。
- 鉴定提供方返回自己计算的总价与结构化贡献。游戏能取得原生总价时，两者必须一致，否则本条分解不显示。注册后也接管同 ID 的原版精选分解，避免重复输出；注销才恢复。
- 机器提供方返回**已本地化、不含括号**的注释；`null` 或空串表示该项不写。注册后这个 ID 的原版注释也由提供方接管，注销才恢复。
- 净水手段注册键是物品的 `identifier`，种类决定水质建议用哪个原版函数模拟：`Machine` → `MachinePurifier.PurifyContainer`，`Portable` → `WaterHelper.PurifyToBaseWater`，`ChemicalAgent` → `ChemicalHelper.DissolveInto` 加反应链。不注册也会自动识别回调直接调用这些函数的物品（化学剂还要在标准试验水里确实提纯），注册后以注册为准；净化逻辑是自己写的、不走这些函数时，不要注册，水质建议也不会模拟它。
- 回调在游戏主线程、构建 tooltip 时执行，应保持只读和轻量。第三方私有状态与缓存由提供方自己管理。
- 只需要追加独立 tooltip 行时，直接使用游戏官方 `ModHook.OnCreateTooltipLate` 即可，不必注册到这里。

渲染器快照只包含模组追加内容；原版 tooltip 的抑制与改写由原生入口用例检查。

## CI

`.github/workflows/ci.yml` 在 push、pull request 和手动触发时运行 Windows 离线检查，
执行的入口与本地相同：`scripts/test.ps1`。工作流安装 .NET 6 和 Python 3.13；
Action 引用固定到提交 SHA，仓库令牌只读且不保留 checkout 凭据。

GitHub 托管 runner 没有游戏程序集，因此完整模组编译、原生契约与发布包检查由本地
`scripts/test.ps1 -GameDir "<游戏目录>"` 执行。游戏 DLL 不提交到仓库或上传为 CI 产物。
工作流使用方式见 [GitHub 的 .NET CI 说明](https://docs.github.com/en/actions/tutorials/build-and-test-code/net)。

## 目录

- `src/Bootstrap/`：`IMod` 入口、生命周期与功能注册。
- `src/Features/`：按业务领域划分的玩家功能；功能自己的 Harmony 补丁随功能放置。
- `src/Domain/`：不依赖 Unity 的纯业务公式，供离线测试直接编译。
- `src/Presentation/`：tooltip 管线、共享行模型、通用行改写与 UI 控件。
- `src/Infrastructure/`：设置、本地化、日志、游戏读取、缓存与跨功能补丁。
- `src/Compatibility/Vanilla/`：原版问题修复；这些不是模组业务功能，不放进对应 `Features/`。
- `src/Compatibility/ThirdParty/`：第三方模组兼容；公开接入契约位于 `src/Api/`。
- `src/Diagnostics/`：条件编译进 Diag/SelfTest 构建的诊断与游戏内自检。
- `tests/`：离线公式测试、校验器回归和构建/包级检查。
- `scripts/`：构建、打包和检查入口；`tools/` 只保留校验器等开发工具。
- `bin/<Variant>/netstandard2.1/`：各构建的 DLL 与资源；`obj/` 为编译中间文件。
- `dist/`：安装用 Release ZIP。生成目录不提交到 Git。
- `Localization/`、`Textures/`、`Sounds/`、`LootTables/`、`Books/`：官方加载方式使用的资源目录；空目录的占位文件仅保留在源码中。

## 写代码时

- 所有玩家可见的文字都经过 `Loc.T("key", "English text", args)`，英文写在调用处，再到各语言 CSV 补译文。
- 定价公式和赝品规则放在 `src/Domain/`，不依赖 Unity，便于离线测试；常量改动后需要与游戏原值比对。
- 读不到或算不准时宁可不显示，不要用默认值拼出一个确定结论。
