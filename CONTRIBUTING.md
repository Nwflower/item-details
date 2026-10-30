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

自检会调用真实售水等行为，先创建可丢弃的专用测试存档。用 `save_<id>.es3` 文件中的内部编号选择槽位，
不是存档列表的显示顺序。启动参数为 `-itemdetails-selftest -itemdetails-selftest-slot <id>`，
或在 `ModData/item_details/selftest.flag` 中写入该编号。空文件、无效编号、编号冲突和不存在的存档均报错退出，
不选择最近存档。

自检临时开启所需设置并在结束或抛错时恢复，不把这些临时值写入配置。
每次运行先写入新的 `selftest.txt`，状态为 `RUNNING`，完成后以相同 run ID 写入 `PASS` 或 `FAIL`。
退出码 0 表示成功，1 表示失败；检查结果时同时核对状态与 run ID。编译自检构建不代表运行过游戏内用例。

无需 Unity Editor 的离线检查（公式项目需 .NET 8 SDK 或更高版本，脚本使用 PowerShell 7）：

```powershell
dotnet run --project tools/FormulaTests/FormulaTests.csproj -c Release
python -B -m unittest discover -s tools -p test_check_localization.py
pwsh -NoProfile -File tools/test_localization_runtime.ps1
pwsh -NoProfile -File tools/test_selftest_results.ps1
pwsh -NoProfile -File tools/test_selftest_fixtures.ps1
# 编译 Release 后，使用实际游戏程序集验证剂量格式、指纹和容器签名
pwsh -NoProfile -File tools/test_upstream_contracts.ps1 -GameDir "<游戏目录>"
```

渲染器快照只包含模组追加内容；原版 tooltip 的抑制与改写由原生入口用例检查。

## 写代码时

- 所有玩家可见的文字都经过 `Loc.T("key", "English text", args)`，英文写在调用处，再到各语言 CSV 补译文。
- 定价公式和赝品规则放在 `Pure/`，不依赖 Unity，便于离线测试；常量改动后需要与游戏原值比对。
- 读不到或算不准时宁可不显示，不要用默认值拼出一个确定结论。
