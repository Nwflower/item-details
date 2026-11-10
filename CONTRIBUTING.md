# 参与开发与翻译

## 改翻译

译文放在 `Localization/<语言码>.csv`，每行 `key,value`。英文不在 CSV 里：它写在代码中，同时也是任何词条缺失或写坏时的兜底。

- 语言码与游戏一致：`zh` `ru` `de` `ja` `fr` `es` `pl` `it` `ko` `ptbr`。新增语言时复制 `zh.csv` 改名即可被自动载入。
- 首尾有空格或含逗号的值，用双引号整体包住；`{0}` 这类占位符不能删改。
- 物品名、分类名、厂商名尽量与游戏自己的译法一致。游戏的官方词表在安装目录 `Probably Stolen_Data/StreamingAssets/aa/StandaloneWindows64/localization-string-tables-*` 里，可以用 UnityPy 导出后按英文原文对照。
- 法语在 `: ; ! ? »` 前、`«` 后，以及数字和单位之间用不换行空格。韩语用半角标点。
- 随数量变化的词写成 `{1|Tag|Tage}`，按第 1 个参数选词，数字仍写 `{1}`。俄语、波兰语按 one|few|many 写三个词形；中日韩不用。
- 鉴定问题名（`auth.*`）优先读游戏词表，CSV 只是兜底。

改完运行校验，确认键齐全、没有多余词条、占位符与英文一致：

```powershell
python tools/check_localization.py --strict-placeholders
```

加 `--lengths` 会列出超过游戏折行宽度的词条（中日韩 26 字，其余 40 字符）。超出的行会被折成两行，能缩短就缩短。

## 构建

需要 PowerShell 7.4+、.NET SDK 6 或更高版本，以及 Python 3.11+。

游戏目录在构建时传入，或设置环境变量 `PS_GAME_DIR`。推荐使用统一入口：

```powershell
pwsh -NoProfile -File scripts/build.ps1 -GameDir "<游戏目录>"

# 构建并部署到游戏 Mods 目录（旧版先备份；游戏在运行时拒绝部署）
pwsh -NoProfile -File scripts/deploy.ps1 -GameDir "<游戏目录>"

# 部署诊断版（带 [perf] 日志）。本地副本会顶掉工坊订阅的版本，测完删掉 Mods/item_details 即可恢复
pwsh -NoProfile -File scripts/deploy.ps1 -GameDir "<游戏目录>" -Variant Diag

# 游戏启动并加载过一次后：对照 Player.log 里的 audit 行，检查检测到的能力是否都已在清单声明
pwsh -NoProfile -File scripts/deploy.ps1 -GameDir "<游戏目录>" -Audit
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

渲染器快照只包含模组追加内容；原版 tooltip 的抑制与改写由原生入口用例检查。

## 第三方模组接入

其他模组读取或接管物品详情的输出，不需要改本仓库：

- [sdk/README.md](sdk/README.md)：复制即用的客户端与上手示例。
- [sdk/INTEGRATION.md](sdk/INTEGRATION.md)：液体数据、区块协议、布局声明与直接引用 DLL 的接口参考。
- [COMPATIBILITY.md](COMPATIBILITY.md)：对第三方的兼容承诺。

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
- 改 tooltip 输出、区块协议、布局声明、公开 API 或旧兼容层之前，先逐条对一遍[兼容契约](COMPATIBILITY.md)。
