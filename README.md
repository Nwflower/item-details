# Item Details / 物品详情

《深空当铺 / Probably Stolen》的官方路线 Mod：把物品真正的信息直接写进原版 tooltip，
不额外开窗口、不改原版 UI 布局。

A tooltip-enhancement mod for *Probably Stolen*: true value breakdown, authenticity,
defect costs, water advice and customer alerts, inlined into the vanilla tooltip.

- Mod ID：`item_details`
- 路线：正式版官方 `IMod` + `manifest.xml`（Mono）
- 版本：`0.5.0`
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
| 水质 | 含水容器显示水质与杂质，并按玩家手上的净化手段列出可行提升 |
| 顾客提示 | 顾客到店时提示通缉犯或改造体身份与举报奖励 |
| 鉴定面板 | 打开鉴定服务时，标题追加真实价格与完美报价区间 |
| 便捷 | 双击待鉴定物品直接打开鉴定面板；鉴定中双击另一件直接切换 |

所有数值都有一道自校验：算出的总价必须等于游戏给出的真实价值，否则**整行不显示**——
宁可少说，也不说错。

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

诊断构建（可选）：模组内自带的无头自检默认**不编译进产物**，需要时显式打开：

```powershell
dotnet build ItemDetails.csproj -c Release -p:GameDir="..." -p:SelfTest=true
# 启动游戏时带 -itemdetails-selftest，或在 ModData/item_details/ 放一个 selftest.flag
```

自检会在载入存档后跑一轮用例并退出，用于验证 tooltip 渲染、探针隔离与公式一致性。

## 设置

模组菜单内可逐项开关，全部即时生效并持久化：真伪、价值构成、赝品原因、缺陷价值、
违禁溢价、荧光莓、压缩率、水质透视、顾客提示、双击鉴定、鉴定面板标题、描述淡化等。

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
- 游戏更新后定价或标签结构变化时，相关行会整行消失而非显示旧值。
