# Item Details / 物品详情

《深空当铺 / Probably Stolen》模组：把鼠标放到物品上，提示框里就直接显示真伪、真实价值和隐藏信息。不用额外开窗口，也不改原版界面布局。

[Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3828868413) · [English](#english) · [参与翻译与开发](CONTRIBUTING.md)

## 主要功能

**真伪鉴定**
免疫宁注射器、海明威香烟、食品券 —— 直接给出正品或赝品结论。判为赝品时会指出具体哪一项不合格；肋明治也会列出判为赝品的原因。

**真实价值**
仿生机械手、肋明治、等离子燃料匣 —— 显示总价，并逐项拆解为何是这个价：

```
基础 100 + 碳复合材料 50 + EMG+ 接口 45 + 刀刃 80 = 275
```

其他模组新增的鉴定物品也会尽量给出价值构成。

**详细显示**
物品分类以提示框上方的独立小标签呈现；标签和瑕疵显示对价格的影响；显示性价比、容器压缩率、容器内容物、违禁品黑市溢价、机器电池可用天数和彩票结果。

**荧光莓品质**
直接显示正常、较小、发霉、未熟、干枯。

**鉴定服务标题**
鉴定服务面板标题会附带该物品的标价与最优报价。

**改造体与通缉犯提醒**
顾客到店时提示谁是改造体、谁是通缉犯，并给出举报奖励、真名与改造等级。提示条可以拖动。

**双击鉴定**
双击待鉴定物品直接打开鉴定面板，不必先拿放大镜再拖动；面板打开时双击另一件可直接切换。

**透视（可选）**
这一组会明显降低难度，按喜好开启：

| 功能 | 效果 | 默认 |
|---|---|---|
| 揭示伪装与真实属性 | 被伪装特性的真实情况、真实耐久度与纯度 | 开 |
| 水质与果酿分析 | 不用试纸就能看到水质与杂质，药物成分显示具体用量 | 关 |
| 刮刮乐预知 | 刮开前就知道能否中奖、奖金多少 | 关 |
| 带锁容器透视 | 保险箱、枪柜未开锁时也能看到内容物与总价值 | 关 |
| 鉴定结果预填 | 打开检视面板时勾好赝品问题、填好最优报价，不会替你提交 | 关 |

所有功能都能在游戏「模组」菜单里本模组的设置中单独开关，改完立即生效。

## 设计原则

- **只读游戏数据**：不改价格、不改顾客、不碰存档。
- **无需权限**：不联网、不启动进程，不声明任何额外能力，启用时不会弹出权限确认。唯一写入的是游戏自带的模组设置文件。
- **数值自校验**：价值拆解的合计必须与游戏自己算出的数值完全一致才显示；对不上就不显示，而不是显示错的。
- **与其他模组共存**：同时启用 AquaSense 等专职水质模组时，自动让出水质部分。

## 安装

- **创意工坊**：[订阅](https://steamcommunity.com/sharedfiles/filedetails/?id=3828868413)后在游戏「模组」菜单中启用，按提示重启游戏。
- **手动**：把发布包放进游戏目录的 `Mods/item_details/`，在「模组」菜单中启用并重启。

需要游戏正式版（运行时版本 0.46D）。遇到问题时按 `F8` 打开控制台，输入 `mod-scan item_details` 查看加载状态。

## 语言

跟随游戏语言，支持游戏自带的全部 11 种：英语、简体中文、俄语、德语、日语、法语、西班牙语、波兰语，以及游戏有词表但下拉框里没有的意大利语、韩语、巴西葡萄牙语。物品名等用词与游戏原文一致。

除中英文外，其余语言是按游戏原版用词校对过的机器翻译初稿，欢迎指正，方法见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 反馈

欢迎在 [Issues](https://github.com/Nwflower/item-details/issues) 或创意工坊页面留言，也可以加入 QQ 交流群：**1132567943**。

已知限制：游戏更新改了定价或界面后，相关信息可能暂时不显示，等模组跟进更新。

## 来源与许可

鉴定价值公式、赝品判定规则和荧光莓品质映射来自对游戏程序的逐项核对，是游戏规则本身；文案、分类与代码均为本项目原创。

本项目以 [GNU AGPL-3.0](LICENSE) 发布：可自由使用、修改、分发，衍生作品须以同一许可公开源码。游戏本体、Unity 与官方 SDK 不在本仓库授权范围内。

---

## English

A *Probably Stolen* mod: hover over an item and its tooltip shows authenticity, true value and hidden details right away — no extra window, no changes to the vanilla UI layout.

**Features**

- **Authenticity check** — Immunivax Injectors, Hemingway cigarettes and Food Stamps are marked genuine or counterfeit, and a counterfeit lists exactly which check failed. Counterfeit Ribwiches list their reasons too.
- **True value** — Cybernetic Hands, Ribwiches and Plasma Cartridges show the total value plus a line-by-line breakdown, e.g. `Base 100 + Carbon composite 50 + EMG+ interface 45 + Blade add-on 80 = 275`.
- **Rich item details** — type badges above the tooltip, price impact of labels and defects, value per cell, container compression and contents, contraband markups, machine battery days and lottery results.
- **BloomBerry quality** — Normal, Small, Moldy, Underripe or Dried.
- **Appraisal Service title** — shows the item's list price and the best offer.
- **Augmented / wanted alerts** — bounty, real name and augmentation tier when such a customer arrives.
- **Double-click to inspect** — open the inspection panel without the Magnifying Glass; double-click another item to switch.
- **Reveal (optional)** — Reveal Disguises & True Stats is on by default; Water & Wine Analysis, Scratcher Preview, Locked Container X-Ray and Prefill Inspection Result are off by default because they make the game noticeably easier.

Every feature can be toggled in this mod's settings in the game's Mods menu.

**Design** — read-only (no price, customer or save changes); declares no capabilities, with no network access or processes (the only thing written is the game's own mod settings file); a breakdown is shown only when it exactly matches the game's own value; steps aside for dedicated water mods such as AquaSense.

**Install** — [subscribe on the Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3828868413), or copy the release into `Mods/item_details/`, then enable it in the Mods menu and restart. Requires the full release of the game (runtime version 0.46D).

**Languages** — follows the game language; all 11 bundled languages are supported. Translations other than Chinese and English are reviewed machine translations — improvements are welcome, see [CONTRIBUTING.md](CONTRIBUTING.md).

Licensed under [GNU AGPL-3.0](LICENSE).
