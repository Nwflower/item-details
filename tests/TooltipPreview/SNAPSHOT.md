# tooltip 快照格式（schema 1）

一种快照格式、多个生产者、一个渲染器：

| 生产者 | `source` | 说明 |
|---|---|---|
| SelfTest 构建的游戏内导出（`TooltipSnapshotExport`） | `ingame` | 走实机悬停同一条路径：`GameItemElement.GetTooltip()` → `OverlayHandler.ShowTooltip` |
| 离线夹具（`tests/test_tooltip_snapshot.ps1`） | `offline-fixture` | 游戏外用真实程序集跑本模组渲染器；没有 Harmony 渲染层，是边界 1 的规范输出 |
| 以后：原版带 + 真实模组代码重放 | `replay` | 未实现 |

渲染器是 [`TooltipPreview/build.py`](README.md) 与 `viewer/`。序列化代码在 `src/Diagnostics/TooltipSnapshot.cs`，只编进 SelfTest 构建。

## 文档

```json
{"schema": 1, "meta": {...}, "items": [ {...}, ... ]}
```

`meta`：

| 字段 | 含义 |
|---|---|
| `source` | 见上表 |
| `gameVersion` | `Application.version`（小补丁不变，见 `assemblyMvid`） |
| `assemblyMvid` | Assembly-CSharp 的模块 MVID，区分同一 `gameVersion` 下的不同构建 |
| `modVersion` | manifest 的 `ModVersion` |
| `language` | 本模组的语言码（`Loc.Code`） |
| `charWidthScale` / `effectiveFontWidth` | `UISettings.current` 的字宽缩放与每字像素（中日韩 > 1 时折行预算按 0.66 / 0.875） |
| `exportedAt` / `saveSlot` | 导出时间（UTC）与存档槽位 |
| `settings` | 影响呈现的设置：`infoEnhance` `spriteBars` `typeBadge` `typeBadgeMuted` `contentsGrid` `manufacturerInline` `tooltipFormatting` |
| `variant` | 录制时指定的变体名，区分"只有我们"与"我们 + 别的排版模组"（如 `item_details`、`item_details+sectioned_tooltips-classic`）；渲染器按它给每列命名 |
| `mods` / `suspended` | 录制时实际在运行的模组（`id@版本; …`）；经模组开关探针临时停用的模组。变体名是录制方的声明，这两项是证据 |
| `geneScanner` | `off+on`：每件物品录了两遍，基因扫描仪开着时显示不同的另存一条（见 `state`）；`on`：存档里本来就开着，只录一遍 |
| `font` / `fontFamily` / `fontSize` / `fontLineHeight` / `fontFallbacks` | tooltip 文本实际用的 TMP 字体资源名、字体族与样式（`族\|样式`）、字号、行高/磅值、回退链（`资源名=族\|样式; …`）。渲染器据此从 `tooltip_fonts.py` 抽出的游戏字体里挑同一款 |

## 物品记录

| 字段 | 含义 | 来源 |
|---|---|---|
| `id` / `name` / `source` / `label` | 物品标识、当前语言显示名、来源、夹具说明 | |
| `state` | 录这一条时额外打开的状态；目前只有 `gene-scanner`（内存里把 `PlayerStore.isGeneScannerScanning` 置真，录完改回）。默认状态不写 | 游戏内 |
| `maxCharWidth` | builder 的折行宽度（Build 前） | |
| `buildMaxCharWidth` | Build 实际用的宽度（本模组为条收窄预算时才出现） | 游戏内 |
| `lines[]` | 折行后的行，顺序即 Build 输出 | Build 依次交给 `TextSlice.GetString` 的片段 |
| `lines[].spacer` | 行首是 `AddEmptyLine(N)` 写的 `<size=Npx> </size>` 片段时为 N（原版之后的文字接在同一行） | |
| `lines[].segments[]` | `text`（片段原文，可含 TMP 标签）、`len`（原版 `GetLength` 可见长度）、`color`/`colorName`、`bold` `italic` `underline` `strike`、`background`、`noBreak`；默认值不写 | |
| `segments[].bar` | 原版 `AddProgressBar` 片段：`length` 格数、`fill` 实心格；本模组记录到的条另有 `ratio` 精确比例、`dark` 底色、`fullLength`，`recorded` 表示是否认领到记录 | |
| `layers` | Build 返回的原始图层：`naked` `foreground` `background` `underline` `strikethrough`，`recommendedWidth`（框宽修正之前）、`recommendedHeight` | |
| `check.segmentsMatchBuild` | 把记录的片段逐个再调原版 `GetString` 拼起来，与 `naked`/`foreground` 逐字相同时为 true | |
| `frame` | Build 后缀修正后的 `recommendedWidth`；游戏内另有 `windowMaxChars`（悬停窗口字符上限 60/72/90/120）、`columns`（两者取小）、`widthPx` `heightPx` | 游戏内为准 |
| `finalLayers` | 本模组 Build 后缀改过前景（公司名并入）时的 `foreground` `background` | 游戏内 |
| `manufacturer` | `name` 公司名、`line` 公司行下标、`inline` 是否并到上一行 | 游戏内 |
| `spriteBars` | 原版窗口是否把条换成 sprite | |
| `badges[]` | 分类徽章：`type` `text` `background` `border` `warning` | 游戏内 |
| `contentsGrid` | 内容物图标格：`sealed` `estimate` `total` `overflow`、`cells[]`（`id` `name` `count`） | 游戏内 |
| `window` | `text` 交给 TMP 的最终前景（sprite 条等已替换）、`badgesShown`、`error` | 游戏内 |
| `types[]` / `tags[]` | 物品分类与标签快照；标签每键一条：`k`，`on` 启用，`i` `f` `l` `d` `b` `s` 只写非默认分量 | |
| `inventories[]` | 嵌套库存树：`kind` 库存类型、`items[]`（`id` `name` `tags` `inventories`），深度 ≤ 4、总件数 ≤ 400 | |
| `rootBuilder` / `calls[]` | 原版带（P2），见下节 | 游戏内 |
| `notes[]` | 这条记录哪些部分没录到及原因 | |

拿不到的字段不写，不填默认值。

## 原版带（`calls[]`）

录制期间，`RichTextBuilder` 的写入入口（`Add` 两个重载、`AddLine`、`NextLine`、`AddEmptyLine`、`AddProgressBar`、
`AddProgressBarOld`、`AddProgressTest`、`AddIcon`、`Clear`、`AddRichTable`）各有一个最高优先级前缀，在本模组任何补丁改写参数之前
按顺序记下最外层调用（内部互调不重复记）：

| 字段 | 含义 |
|---|---|
| `seq` | 本件物品内的顺序号 |
| `builder` | builder 编号（按首次出现）；`rootBuilder` 是最终被 Build 成 tooltip 的那个 |
| `method` / `args` | 方法名与参数（按参数名；颜色写枚举名，builder 参数写 `{builder, slices}`，其余对象写 `{type, name}`） |
| `origin` | 调用方：`vanilla`、`item_details`、`other:<程序集>`、`unknown`（按调用栈判定，见下） |
| `caller` | 调用方方法 |
| `fate` | `kept` 原样写出；`augmented` 原文在但同次调用多写了片段；`rewritten` 被跳过但写了替代内容、或写出的文字已被改；`dropped` 被跳过且没写 |

已知盲区：本模组用 transpiler 替换掉的原版写入（如机器电量行）不会经过入口，因而不在带里。

`origin` 的判定：从录制补丁往外数调用栈，跳过录制器、Harmony、`RichTextBuilder` 自身和系统库，第一帧属于哪个程序集就记哪个。
被 Harmony 打过补丁的方法在 Mono 上以动态方法运行：有的栈帧没有声明类型，有的声明类型是 `MonoMod.Utils.DynamicMethodDefinition`，
方法名形如 `RichTextBuilder.AddLine_Patch6`。名字里带 `RichTextBuilder` 的是写入方法的补丁包装（跳过），
其余是被打过补丁的原版方法的副本，记 `vanilla`。一路数到导出驱动都没有可识别的帧，同样记 `vanilla`（`caller` 写 `(patched vanilla method)`）。
仍然归不了的记 `unknown`，导出报告 `report.txt` 里附去重后的调用栈样本。
第一版要求"先见到 `RichTextBuilder` 帧"，而那一帧正是没有类型的动态方法，2026-11-11 的首次实机录制 17431 次调用全部是 `unknown`。

## 触发与变体

`ModData/item_details/tooltipsnap.flag` 的内容是存档槽位 ID，或若干行 `key=value`：

| 键 | 含义 |
|---|---|
| `slot` | 必填，可丢弃的存档槽位 |
| `variant` | 写进 `meta.variant` |
| `suspend` | 录制前经模组开关探针（`Modding/ModToggleProbe` 的 `mod_toggle_probe.suspend.v1`）临时停用的模组 ID，逗号分隔；没在运行就跳过，探针不在则整体失败 |
| `require` | 必须正在运行的模组 ID；不在就整体失败，免得录出标错变体的数据 |
| `gene` | `1` 时每件物品再按基因扫描仪开着录一遍 |

一般不手写：`tools/run_selftest.ps1 -TooltipSnapshot -SaveSlot <N> [-Variant … -SuspendMod … -RequireMod … -GeneScan]` 录一份，
`tools/record_tooltip_variants.ps1 -SaveSlot <N>` 依次录"只有我们 / + 物品详情美化 Classic / + 物品详情美化 Ticket"三份并生成对比页。
启用模组只能由玩家在模组菜单里点，脚本不代办；Item Details、探针和要对比的模组得先启用好。

## 渲染

渲染器是正式的 [`TooltipPreview/`](README.md) 测试工具，输出一个静态站点目录，不是单个 HTML：

```powershell
python Modding/ItemDetails/tests/TooltipPreview/fonts.py --game-dir "<游戏目录>" `
  -o Modding/ItemDetails/out/tooltip-preview/fonts
python Modding/ItemDetails/tests/TooltipPreview/build.py <快照目录> `
  -o Modding/ItemDetails/out/tooltip-preview/sites/<名称>
python Modding/ItemDetails/tests/TooltipPreview/build.py --compare <目录A> <目录B> <目录C> `
  -o Modding/ItemDetails/out/tooltip-preview/sites/compare
```

- HTML 只是加载器（约 0.5 KB）；物品数据在 `data/*.js`，渲染逻辑在 `assets/app.js`。每个变体再拆成 `typical` 和 `rest` 两块，
  首屏只加载典型块，其余在点按钮或搜索时加载。
- 字体：按 `meta.font*` 记录的字体与回退链，从 `out/tooltip-preview/fonts/` 挑同一款字体复制到站点 `fonts/`；没有就退回浏览器等宽字体。
  字体是游戏资源，只留在被忽略的 `out/` 里，不入库、不随包发布。
- `--compare`：每份快照一列，同一份存档里的物品按（标识, 状态, 第几件）对齐，行首标出各列文字是否相同。
- `--raw none|full`：默认 `none`，页面不显示写入次数/来源等调试信息；`full` 才带每次写入的参数、标签和内容物树。
- `--only-typical`：只写典型物品，不生成 `rest` 数据块。典型物品规则在 [`typical.json`](typical.json)。
- 生成的站点目录带 `.tooltip-preview-site` 标记；构建器只清空自己生成的目录，非空外来目录会拒绝。
