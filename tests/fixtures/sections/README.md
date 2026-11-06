# 区块契约样例

`contract.txt` 是 `tests/test_section_replay.ps1` 导出的固定物品区块快照，用作协议的形状判据：
区块 id、`group` 字段、其余字段名与标量值、每段 run 的 `Kind / Text / Color / Ratio / Length / Background / Tail`。
它不含真实存档内容，物品是脚本里手工搭的未初始化 `GameItem` 加标签，所以结果与机器、区域设置无关。

## 格式

```
id|key=value;…|kind:text:color:ratio:length:background:tail;…
```

- 字段按键名排序；标量按不变文化格式化；集合等复杂值只写类型名（`<ReadOnlyCollection`1>`），
  读法由 SDK 的类型化访问器负责，样例只管形状。
- 一行一个区块，区块顺序即发出顺序。

## 怎么用

- 我方：`pwsh tests/test_section_replay.ps1 -GameDir <游戏目录>` 逐字比对；改动了区块形状时用 `-Update` 重生成，
  并在提交信息里说明变更属于"只增字段"还是"改形状"。
- 对方（SectionedTooltips）：直接读这份文件测归类——按 id 查到 `group`，再用 `SectionOf` 兜底，不需要启动游戏。

## 不要做的事

- 不要为了让测试变绿去改样例：先判断这次改动是否真的该改协议。
- 不要把旧的 `tests/fixtures/legacy-v1/`（冻结的 v1 消费者源码）与这份样例混用：前者测"旧消费者不改代码仍能读"，
  后者测"新快照的形状没有意外变化"。
