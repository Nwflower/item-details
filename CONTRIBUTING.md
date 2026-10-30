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

需要 PowerShell 7.4+ 和 .NET SDK 8 或更高版本；离线公式测试还需要 .NET 8 运行时
（SDK 8 已包含，较新 SDK 可与它并存）。校验脚本使用 Python 3.11+。

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
单个回归检查保留在 `tools/` 中，可按需单独运行。

## 游戏内自检

自检会调用真实售水等行为，先创建可丢弃的专用测试存档。用 `save_<id>.es3` 文件中的内部编号选择槽位，
不是存档列表的显示顺序。启动参数为 `-itemdetails-selftest -itemdetails-selftest-slot <id>`，
或在 `ModData/item_details/selftest.flag` 中写入该编号。空文件、无效编号、编号冲突和不存在的存档均报错退出，
不选择最近存档。

自检临时开启所需设置并在结束或抛错时恢复，不把这些临时值写入配置。
每次运行先写入新的 `selftest.txt`，状态为 `RUNNING`，完成后以相同 run ID 写入 `PASS` 或 `FAIL`。
退出码 0 表示成功，1 表示失败；检查结果时同时核对状态与 run ID。编译自检构建不代表运行过游戏内用例。

渲染器快照只包含模组追加内容；原版 tooltip 的抑制与改写由原生入口用例检查。

## CI

`.github/workflows/ci.yml` 在 push、pull request 和手动触发时运行 Windows 离线检查，
执行的入口与本地相同：`scripts/test.ps1`。工作流安装 .NET 10 和 .NET 8（供公式测试运行）、
Python 3.13；Action 引用固定到提交 SHA，仓库令牌只读且不保留 checkout 凭据。

GitHub 托管 runner 没有游戏程序集，因此完整模组编译、原生契约与发布包检查由本地
`scripts/test.ps1 -GameDir "<游戏目录>"` 执行。游戏 DLL 不提交到仓库或上传为 CI 产物。
工作流使用方式见 [GitHub 的 .NET CI 说明](https://docs.github.com/en/actions/tutorials/build-and-test-code/net)。

## 目录

- 原有功能目录与 `Pure/`：模组代码与共享公式。
- `scripts/`：构建、打包和检查入口。
- `tools/`：校验器及各项回归检查。
- `bin/<Variant>/netstandard2.1/`：各构建的 DLL 与资源；`obj/` 为编译中间文件。
- `dist/`：安装用 Release ZIP。生成目录不提交到 Git。
- `Localization/`、`Textures/`、`Sounds/`、`LootTables/`、`Books/`：官方加载方式使用的资源目录；空目录的占位文件仅保留在源码中。

## 写代码时

- 所有玩家可见的文字都经过 `Loc.T("key", "English text", args)`，英文写在调用处，再到各语言 CSV 补译文。
- 定价公式和赝品规则放在 `Pure/`，不依赖 Unity，便于离线测试；常量改动后需要与游戏原值比对。
- 读不到或算不准时宁可不显示，不要用默认值拼出一个确定结论。
