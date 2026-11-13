# TooltipPreview

Item Details tooltip 快照的正式离线预览工具。它把 schema 1 快照构建成一个静态站点：**HTML 只是加载器**，
文字、颜色、折行和原版带数据都在外部 `data/*.js` 里；默认只渲染典型物品，其余物品搜索或点按钮时才加载。

快照格式见 [SNAPSHOT.md](SNAPSHOT.md)，录制入口见 [tools/record_tooltip_variants.ps1](../../../../tools/record_tooltip_variants.ps1)。

## 目录

| 路径 | 内容 |
|---|---|
| `build.py` | 站点构建器：读取快照、计算典型分类/对比行/差异，写出 `index.html`、`assets/`、`data/`、`fonts/` |
| `fonts.py` | 从游戏 `*.assets` 抽出 tooltip 字体文件，缓存到 `out/tooltip-preview/fonts/` |
| `typical.json` | 典型物品规则（水、改良种子、净水器、带自学习词条的模组、鉴定品、锁箱、电池机器等） |
| `viewer/index.html` | 固定 HTML 外壳，不含物品数据 |
| `viewer/app.js` | 浏览器渲染器：富文本标签、条、徽章、内容物格、对比/差异、搜索与分页 |
| `viewer/style.css` | 样式；卡片 `flex:0 0 auto`，对比列横向滚动，默认不压窄 |
| `../../tests/fixtures/tooltip-preview/` | 手写小样例与夹具回归 |

生成物在 `Modding/ItemDetails/out/tooltip-preview/`：

```text
out/tooltip-preview/
├─ fonts/                         # 字体缓存（游戏资源，忽略，不入库）
└─ sites/
   ├─ 0.46D-zh-item_details/
   │  ├─ index.html               # 约 0.5 KB
   │  ├─ assets/{app.js,style.css,fonts.css}
   │  ├─ data/index.js            # 站点元数据、行索引、典型分组
   │  ├─ data/doc-0-typical.js    # 首屏数据
   │  ├─ data/doc-0-rest.js       # 点“显示其余”或搜索时才加载
   │  └─ fonts/*.ttf
   └─ compare/
```

`data/` 用脚本文件而不是 `fetch` JSON：这样直接双击 `file://` 打开也能工作，不需要起本地服务器。
数据文件本身仍是结构化序列化结果，渲染逻辑只在 `assets/app.js`，两者分离。

## 构建

```powershell
# 单个快照
python Modding/ItemDetails/tests/TooltipPreview/build.py `
  Modding/ItemDetails/out/tooltip-snapshots/0.46D-zh-item_details `
  -o Modding/ItemDetails/out/tooltip-preview/sites/0.46D-zh-item_details

# 多变体并排
python Modding/ItemDetails/tests/TooltipPreview/build.py --compare <目录A> <目录B> <目录C> `
  -o Modding/ItemDetails/out/tooltip-preview/sites/compare

# 两次录制差异
python Modding/ItemDetails/tests/TooltipPreview/build.py <新快照> --diff <旧快照> -o <站点目录>
```

- `-o` 是**站点目录**，不是 `.html`。目录里已经有 `TooltipPreview` 生成的 `.tooltip-preview-site` 标记时才会清空重建；
  其它非空目录会被拒绝，防止误删。
- `--raw none|full`：默认 `none`，页面不显示写入次数/来源等调试信息；`full` 才在折叠区里放完整 `calls`、标签和内容物树。
- `--only-typical`：不写 `rest` 行和 `rest` 数据块。
- `--fonts`：字体缓存目录，默认 `out/tooltip-preview/fonts`。
- 浏览器不需要联网；字体只从站点 `fonts/` 读。

首次抽字体（每台机器一次）：

```powershell
python Modding/ItemDetails/tests/TooltipPreview/fonts.py --game-dir "<游戏目录>" `
  -o Modding/ItemDetails/out/tooltip-preview/fonts
```

字体是游戏资源，只放到被忽略的 `out/`；不要提交、不要打进发布包。

## 页面行为

- 首屏渲染典型物品，其余不建 DOM；点“显示其余 N 件”才加载 `*-rest.js` 并每页 40 件。
- 左侧“分类标签”按 `TypeBadge` 类型筛选，按物品数从多到少排列（同数按名称），点分类时按需加载全部剩余数据。
- 搜索框会按需加载全部剩余数据，再按文字过滤，最多显示 80 条。
- 卡片宽度默认保留（`flex:0 0 auto`）；对比页三列横向滚动，不因视口变窄而挤压 tooltip。
- 对比行按同一存档的（标识、状态、出现序号）对齐，并标注“各变体文字相同/不同”。
- Ticket/Classic 的纸单、侧边面板是 SectionedTooltips 自绘的 Unity UI，文字快照不包含，页面会显示范围提示。

## 测试

```powershell
python -m unittest discover -s tests -p test_tooltip_preview.py
node --check tests/TooltipPreview/viewer/app.js
pwsh tests/test_tooltip_snapshot.ps1 -GameDir "<游戏目录>"   # 离线夹具，构建站点并核对数据块
```

页面本身需要在浏览器里看；离线测试只验证构建器、数据分块、CSS 不收缩和输出目录保护，不能证明浏览器排版与游戏逐像素一致。
