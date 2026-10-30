# Item Details / 物品详情

这是《深空当铺 / Probably Stolen》的一个模组。把鼠标停在物品上，就能在提示框里看到它的真伪、价值构成等信息，不用再打开别的窗口。

[Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3828868413) · [English](#english) · [参与翻译与开发](CONTRIBUTING.md)

版本：0.6.3。Mod ID：`item_details`。使用正式版 Mono 的官方 `IMod` 加载方式。

## 主要功能

**真伪鉴定**
免疫宁注射器、海明威香烟和食品券会直接显示是正品还是赝品；如果是赝品，还会列出不合格的项目。肋明治也会列出判为赝品的原因。

**真实价值**
仿生机械手、肋明治和等离子燃料匣会显示总价和各部分的价值，例如：

```
基础 100 + 碳复合材料 50 + EMG+ 接口 45 + 刀刃 80 = 275
```

其他模组新增的鉴定物品也会尝试显示价值构成。

**详细显示**
提示框上方会显示物品分类。物品标签和瑕疵对价格的影响、每格价值、容器压缩率及内容物、违禁品黑市溢价、机器电池可用天数和彩票结果也会显示出来。

**植物信息**
荧光莓会显示正常、较小、发霉、未熟或干枯。扫描仪未开启时，还能看到种子和基因磁盘尚未分析的性状。

**鉴定服务标题**
鉴定服务面板的标题会显示物品标价和最优报价。

**改造体与通缉犯提醒**
改造体或通缉犯到店时会出现提示，并显示举报奖励、真名和改造等级。游戏隐藏的潜行通缉状态不会被揭示。提示条可以拖动，位置会保留到游戏进程退出。

**容器与机器名称（可选）**
可以在容器和机器图标底部显示名称，也可以给名称加半透明背景。这个功能默认关闭。

**双击鉴定**
双击待鉴定物品就能打开鉴定面板，不用先拿放大镜再拖动。面板打开后，双击另一件物品即可切换。

**透视（可选）**
这一组会明显降低难度，按喜好开启：

| 功能 | 效果 | 默认 |
|---|---|---|
| 揭示伪装与真实属性 | 被伪装特性的真实情况、真实耐久度与纯度 | 开 |
| 水质与果酿分析 | 不用试纸就能看到水质、杂质和果酿数据，药物成分显示具体用量 | 关 |
| 刮刮乐预知 | 刮开前就知道能否中奖、奖金多少 | 关 |
| 带锁容器透视 | 保险箱、枪柜未开锁时也能看到内容物与总价值 | 关 |
| 鉴定结果预填 | 打开检视面板时勾好赝品问题、填好最优报价，不会替你提交 | 关 |

这些功能都可以在游戏「模组」菜单中调整，设置由游戏保存，应用后生效。
性价比和容器内容物已分成两个开关，升级后默认开启。如果之前关闭了信息增强，可以分别调整。
分类标签的淡化配色默认关闭。开启后，标签会换成深色底和提亮的分类文字，违禁或受限物品仍使用警示色。

## 设计原则

- **信息展示**：模组不修改游戏的定价规则、顾客生成逻辑或存档结构。
- **权限范围**：普通发布版不联网、不启动外部进程，也没有声明额外能力。设置由游戏的配置系统保存；自检构建见 [开发说明](CONTRIBUTING.md)。
- **数值自校验**：机械手、肋明治和等离子燃料匣的鉴定总价会与游戏估价核对。两者不一致时，价值分解不会显示；最低价补差会列在明细中。
- **与其他模组共存**：启用 AquaSense 后，本模组不再显示水质详情，但仍会显示净化收益建议。

未上锁的普通容器只列出内容物，不另算总价。带锁容器的透视金额后如果有 `+`，说明有物品没有计入，例如嵌套容器或无法估价的物品。
药物活性成分沿用扫描仪的 µL/mL/L 单位，并保留单个 µL 的精度。

## 安装

- **创意工坊**：[订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3828868413)后在游戏「模组」菜单中启用，按提示重启游戏。
- **手动**：将发布 ZIP 解压到游戏的 `Mods/` 目录，得到 `Mods/item_details/ItemDetails.dll` 和
  `Mods/item_details/manifest.xml`，再在「模组」菜单中启用并重启。

需要游戏正式版（运行时版本 0.46D）。遇到问题时按 `F8` 打开控制台，输入 `mod-scan item_details` 查看加载状态。

## 语言

跟随游戏语言，支持游戏自带的全部 11 种：英语、简体中文、俄语、德语、日语、法语、西班牙语、波兰语，以及游戏有词表但下拉框里没有的意大利语、韩语、巴西葡萄牙语。优先采用游戏原版用词，部分短语按提示框语境调整。

除中英文外，其余语言是按游戏原版用词校对过的机器翻译初稿，欢迎指正，方法见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 反馈

欢迎在 [Issues](https://github.com/Nwflower/item-details/issues) 或创意工坊页面留言，也可以加入 QQ 交流群：**1132567943**。

已知限制：游戏更新改了定价或界面后，相关信息可能暂时不显示，等模组跟进更新。

- 水质建议缓存 30 秒；拥有的净化手段扫描缓存 2 秒，检测到设备、已装滤芯或语言变化后重算。
- 密封箱显示缓存 5 秒、子项估价缓存 10 秒；未纳入指纹的全局价格变化可能约 15 秒后反映。
- 初次计算水质建议仍有主线程开销。

## 来源与许可

鉴定价值公式、赝品判定规则和荧光莓品质映射来自对游戏程序的逐项核对，是游戏规则本身；文案、分类与代码均为本项目原创。

本项目以 [GNU AGPL-3.0](LICENSE) 发布：可自由使用、修改、分发，衍生作品须以同一许可公开源码。游戏本体、Unity 与官方 SDK 不在本仓库授权范围内。

---

## English

This *Probably Stolen* mod adds details to item tooltips. Hover over an item to see its authenticity, value breakdown and other information where available, without opening another window.

**Features**

- **Authenticity check** — See whether Immunivax Injectors, Hemingway cigarettes and Food Stamps are genuine. Counterfeits list the failed checks; counterfeit Ribwiches show their reasons too.
- **True value** — Cybernetic Hands, Ribwiches and Plasma Cartridges show the total value plus a line-by-line breakdown, e.g. `Base 100 + Carbon composite 50 + EMG+ interface 45 + Blade add-on 80 = 275`.
- **Rich item details** — type badges above the tooltip, price impact of labels and defects, value per cell, container compression and contents, contraband markups, machine battery days and lottery results.
- **Plant details** — See BloomBerry quality and, when the scanner is off, unanalyzed traits on seeds and gene disks.
- **Appraisal Service title** — shows the item's list price and the best offer.
- **Augmented / wanted alerts** — Get an alert when an augmented or wanted customer arrives, with their bounty, real name and augmentation tier when available. Stealth-wanted status stays hidden. You can drag the alert; its position lasts for the current game session.
- **Container and machine labels** — optionally show names at the bottom of their icons; disabled by default.
- **Double-click to inspect** — open the inspection panel without the Magnifying Glass; double-click another item to switch.
- **Reveal (optional)** — Reveal Disguises & True Stats is on by default; Water & Wine Analysis, Scratcher Preview, Locked Container X-Ray and Prefill Inspection Result are off by default because they make the game noticeably easier.

You can change these settings in the game's Mods menu. Value per Cell and Container Contents now have separate switches, both enabled by default on upgrade. Muted badge colors are optional and off by default.

**Design** — does not change pricing rules, customer generation or save structures; the normal release declares no extra capabilities and uses no network or external processes. Settings use the game's configuration system. Hand, Ribwich and Plasma appraisal totals are checked against the native value, with minimum-price adjustments included. When AquaSense is enabled, water details are left to it while purification advice remains available.

Unlocked ordinary containers list contents without a separate total. A locked-container amount ending in `+` excludes some contents, such as nested containers or items that could not be valued.
Active-liquid amounts retain individual µL in the scanner's µL/mL/L units.
Developer self-tests require an explicitly selected disposable save; see [CONTRIBUTING.md](CONTRIBUTING.md).

**Install** — [subscribe on the Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3828868413), or extract the release ZIP into `Mods/` (it contains the `item_details/` folder), then enable it in the Mods menu and restart. Requires the full release of the game (runtime version 0.46D).

**Languages** — follows the game language; all 11 bundled languages are supported. Translations other than Chinese and English are reviewed machine translations — improvements are welcome, see [CONTRIBUTING.md](CONTRIBUTING.md).

Licensed under [GNU AGPL-3.0](LICENSE).
