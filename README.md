# Item Details / 物品详情

《深空当铺 / Probably Stolen》的官方路线 Mod：把物品真正的信息直接写进原版 tooltip，
不额外开窗口、不改原版 UI 布局。

A tooltip-enhancement mod for *Probably Stolen*: true value breakdown, authenticity,
defect costs, water advice and customer alerts, inlined into the vanilla tooltip.

- Mod ID：`item_details`
- 路线：正式版官方 `IMod` + `manifest.xml`（Mono）
- 版本：`0.6.2`
- 适配游戏版本：`0.46`

## 功能

| 类别 | 显示内容 |
|---|---|
| 价值构成 | 机械手 / 肋明治 / 等离子燃料匣的真实总价与逐项来源（基础价、材质、接口、磨损、组合加成…） |
| 其他鉴定类型 | 对模组新增或未知的鉴定类型，用实测分解给出结果 |
| 真伪判定 | 注射器 / 香烟 / 粮票逐项检查「指示灯、标签、序列号、瓶体、颜色…」并给出正品或赝品及不合格项 |
| 赝品原因 | 肋明治列出具体原因：生产日期缺失或越界、包装厂编号异常、达夫雷标志、批次贴纸、豪华版组合 |
| 缺陷价值 | 原版只写状态不写金额的瑕疵，这里补上对价格的影响 |
| 违禁与品质 | 违禁品黑市溢价、荧光莓品质、储存压缩率、容器内容物价值 |
| 机器电池 | 机器 tooltip 在「所需能量」行下面显示电池剩余能量与可运行天数；未装电池不显示 |
| 水质 | 含水容器显示水质与杂质，并按玩家手上的净化手段列出可行提升 |
| 顾客提示 | 顾客到店时提示通缉犯或改造体身份与举报奖励；提示条默认在屏幕正上方居中，可用标题栏拖动，**位置只在本次游戏内记住**（不写盘，重启回到正上方） |
| 鉴定面板 | 打开鉴定服务时，标题追加真实价格与完美报价区间 |
| 结果预填 | 可选：打开检测/鉴定面板时勾选赝品实际问题，并把鉴定价格填为最优报价；默认关闭，不自动提交 |
| 便捷 | 双击可检视物品（价格鉴定、真伪鉴定及 `INSPECTABLE_TAG` 物品）直接打开检视面板；检视中双击另一件直接切换 |
| 分类标签 | 物品类别以**有色底板 + 白字**排在 tooltip 上方：医疗/水/药物/毒物蓝、烟酒黑底白字、奢侈品粉、食物绿、日用品青、武器/护甲青绿、工具/材料紫、违禁/受限暗红、其他灰色；设置里可关掉，关掉后回到原版正文里的「[类型]」 |

机械手、肋明治和等离子燃料匣的鉴定总价会与游戏真实估价核对，不一致时隐藏该组分解。
最低价补差会列入明细；容器含嵌套物品或有内容无法计价时保留清单，隐藏不完整的总价。

## 安装

1. 确认游戏为正式版（Mono 后端），`Mods/` 目录存在。
2. 把 `ItemDetails.dll`、`manifest.xml` 以及 `Textures/`、`Localization/` 等资源目录放进
   `Mods/item_details/`。
3. 启动游戏，在模组菜单中启用后按提示重启。
4. 若出现问题，按 `F8` 打开控制台，用 `mod-scan item_details` 查看加载与信任状态。

创意工坊安装：订阅后在游戏中启用即可（发布后补充链接）。

## 构建

游戏目录不写死在工程里，构建时传入（或设置环境变量 `PS_GAME_DIR`）：

```powershell
dotnet build ItemDetails.csproj -c Release -p:GameDir="D:\Steam\steamapps\common\Probably Stolen"
```

输出在 `bin/Release/netstandard2.1/`，含 `ItemDetails.dll` 与随包资源。
引用的是游戏自带的 `Assembly-CSharp.dll`、Unity 与 `0Harmony.dll`（全部 `Private=false`，不会被打进产物）。

诊断构建（可选）：普通 Release 不包含信息/性能日志和自检代码，需要时显式打开：

```powershell
# 只启用信息、性能与 UI 诊断日志，不启用自动读档或自检
dotnet build ItemDetails.csproj -c Release -p:GameDir="..." -p:Diag=true

# 自检构建会同时启用诊断日志
dotnet build ItemDetails.csproj -c Release -p:GameDir="..." -p:SelfTest=true -p:OutputPath=bin/ValidationSelfTest/
```

自检会调用真实售水等游戏行为，请先创建可丢弃的专用测试存档。用该存档文件
`save_<id>.es3` 中的编号指定槽位，编号不是存档列表中的显示顺序：

- 启动参数：`-itemdetails-selftest -itemdetails-selftest-slot <id>`。
- 或在 `ModData/item_details/selftest.flag` 中写入该编号，启动后文件会被消费。
  空文件、无效编号、两处编号冲突或指定存档不存在时，自检报错退出，不选择最近存档。

自检加载指定存档，临时开启所需的鉴定、信息增强、双击与水质设置；结束或抛错时恢复，
临时设置不写入玩家配置。用例使用独立样本，不要求存档中已有含水容器。
渲染器快照只包含模组追加内容；原版 tooltip 的抑制与改写由另设的原生入口用例检查。

每次运行先覆盖 `ModData/item_details/selftest.txt` 为 `RUNNING`，随后写入相同 run ID 的
`PASS` 或 `FAIL`。退出码 0 表示成功，1 表示失败；检查结果时同时核对状态与 run ID，
`RUNNING` 表示尚未完成。编译成功不代表这些游戏内用例已运行。

无需 Unity Editor 的离线回归（公式项目需 .NET 8 SDK 或更高版本，脚本使用 PowerShell 7）：

```powershell
dotnet run --project tools/FormulaTests/FormulaTests.csproj -c Release
python -B -m unittest discover -s tools -p test_check_localization.py
pwsh -NoProfile -File tools/test_localization_runtime.ps1
pwsh -NoProfile -File tools/test_selftest_results.ps1
pwsh -NoProfile -File tools/test_selftest_fixtures.ps1
```

发布包只包含 DLL、manifest、资源目录和发布文档；本地 PDB 与 deps.json 不上传。

## 设置

模组菜单内共 12 项，全部即时生效并持久化：

- **显示优化**：物品鉴定、信息增强、物品分类标签、容器与机器外显文本、外显文本半透明底、顾客到店提示
- **操作优化**：双击开始鉴定
- **透视**：揭示货不对板、水质鉴定（含酒分析）、刮刮卡提前看、密封箱透视、结果预填

## 多语种

界面文案跟随游戏语言，覆盖游戏内置的全部 11 套语言表：

| 语言码 | 语言 | 游戏下拉框可选 |
|---|---|---|
| `en` | English（兜底语言） | ✅ |
| `zh` | 简体中文 | ✅ |
| `ru` `de` `ja` `fr` `es` `pl` | 俄语 / 德语 / 日语 / 法语 / 西班牙语 / 波兰语 | ✅ |
| `it` `ko` `ptbr` | 意大利语 / 韩语 / 巴西葡萄牙语 | ⬜ 词表存在，但游戏界面没有提供选项 |

实现约定：

- **英文文案写在代码里**（`Loc.T("key", "English …")`），它就是兜底值；其余语言放在
  `Localization/<语言码>.csv`。缺文件、漏词条、翻译写坏时都回落到英文，
  绝不会把 `price.sell` 这类内部键名显示给玩家。
- 换语言即时生效（语言码缓存 0.25 秒），不需要重启。
- 想加一门语言：复制 `Localization/zh.csv` 改名为目标语言码（必须与游戏
  `SelectedLocale.Identifier.Code` 一致），按 `key,value` 逐行翻译即可被自动载入。
  **首尾带空格或含逗号的值要用双引号整体包住**，`{0}` 这类占位符不能删改。
- 改完跑一次 `python tools/check_localization.py`：校验键是否齐全、有无孤儿词条、
  占位符是否与英文一致。

> 除简体中文与英文外，其余 9 种语言为机器翻译初稿，措辞欢迎提 PR 修正。

## 来源说明

- 鉴定价值公式、赝品判定规则、荧光莓品质映射来自对游戏 `0.46D` 的 `Assembly-CSharp`
  反编译逐项核对——这些是游戏规则本身，不是从其他 Mod 抄来的实现。
- 文案、分类表与代码结构为本项目原创；早期版本开发时参考过社区模组的行为，
  相关文案与结构已全部重写。
- 常量修改后需与游戏原值比对，公式漂移时模组会静默隐藏该行，而不是显示错误数字。

## 许可

本项目以 [GNU AGPL-3.0](LICENSE) 发布：可自由使用、修改、分发，衍生作品须以同一许可公开源码。

游戏本体、Unity 与官方 SDK 不在本仓库授权范围内。

## 已知限制

- 鉴定面板标题依赖遍历 `appraisalPanel` 子树查找「鉴定服务」文本，游戏改文案或层级会失配（日志会给出降级提示）。
- 水质模拟在缓存未命中时会构造探针物品跑真实净化流程，属主线程开销。
- 游戏更新后鉴定公式或标签结构变化时，未通过总价核对的鉴定分解会隐藏。
- 水质建议中的已装滤芯耐久、孔径与语言变化会触发重算；其它拥有的净化手段最迟 30 秒刷新。
- 图标外显文本固定为容器/机器、位置固定底部；半透明底不透明度固定 0.6，不再提供下拉框。
