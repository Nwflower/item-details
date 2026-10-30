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

游戏目录不写死在工程里，构建时传入，或设置环境变量 `PS_GAME_DIR`：

```powershell
dotnet build ItemDetails.csproj -c Release -p:GameDir="<游戏目录>"
```

输出在 `bin/Release/netstandard2.1/`，包含 `ItemDetails.dll` 与随包资源。引用的游戏程序集、Unity 与 Harmony 都不会被打进产物。发布包只需要 DLL、`manifest.xml` 和资源目录。

需要排查问题时可以打开诊断构建：

```powershell
# 信息、性能与界面诊断日志
dotnet build ItemDetails.csproj -c Release -p:GameDir="<游戏目录>" -p:Diag=true

# 实机自检：载入存档后跑一轮用例并退出（同时包含诊断日志）
dotnet build ItemDetails.csproj -c Release -p:GameDir="<游戏目录>" -p:SelfTest=true
```

自检构建在启动参数带 `-itemdetails-selftest`，或 `ModData/item_details/` 下有 `selftest.flag` 时运行。

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

## 写代码时

- 所有玩家可见的文字都经过 `Loc.T("key", "English text", args)`，英文写在调用处，再到各语言 CSV 补译文。
- 定价公式和赝品规则放在 `Pure/`，不依赖 Unity，便于离线测试；常量改动后需要与游戏原值比对。
- 读不到或算不准时宁可不显示，不要用默认值拼出一个确定结论。
