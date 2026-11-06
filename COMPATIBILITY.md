# 兼容契约

物品详情对第三方承诺的东西，逐条可核对。改 tooltip 输出、区块协议、布局声明、公开 API 或旧兼容层之前对一遍；
接入方用法见 [sdk/README.md](sdk/README.md) 与 [CONTRIBUTING.md](CONTRIBUTING.md#第三方模组接入)。

## 协议形状：v1 冻结

1. 槽位名、委托签名、Runs 元组形状、`ItemDetailsApi` 签名、`ItemDetailsTooltipSection` 各位取值不改；字段只增不改。不兼容的变更开新版本键，与 v1 并存。
2. 行为同样冻结：既有区块 ID、字段类型与含义、空值、`CanReplace`、接管与显隐规则。可接管的区块不改成只观察，只观察的也不改成可接管。
3. 新字段与新槽位（`group`、徽章槽位等）是可选升级；旧消费者不更新 SDK 也保持原有显示。

## 规范输出

4. `GetTooltipBasic` 返回时的 builder 就是规范输出。重排方从这里读，所以玩家能看到的信息都必须在 builder 里：只挂在渲染注解上的信息，对方一 `Clear` 就没了。
5. 区块 runs 的文本与 builder 里那一行逐字一致。SectionedTooltips 按整行文本把行归到小节，对不上就归不进去。
6. 渲染层只换写法、不换信息：换词（购买价格→进价）、单位（ml→L）、`<pos>` 列、描述折行、sprite 进度条。
7. 带信息的原版行改写（标签影响、卡路里、模组批注、带档位的液量、合并价格行）写进正文并走 `Emit`；要丢的行（估价行、空表头、已并入价格行的购买价格行）在逻辑层丢，不在渲染层藏。
8. 已被消费者解析的文本格式不改：合并价格行「售价…（进价…） 标价…」由 SectionedTooltips 的估价单解析，成分表单元格用全角空格分隔。要改，先与对方核对。

## 旧版兼容

9. 已承诺的旧版（SectionedTooltips 1.6.2 / 1.6.3）适配放在 `src/Compatibility/`，只按已核实版本的配置处理，未知版本不猜。存在显式布局回调或逐件显示注册时以协议为准，即便它返回 null、None 或抛错，也不再套用旧配置。
10. 兼容由我方维护：不以修改合作方代码代替兼容验收，不把新接口当成旧消费者的升级前提；移除旧路径前另行约定支持范围与迁移期。

## 改完核对

- `scripts/test.ps1`：包含冻结旧 SDK（`tests/test_legacy_compat.ps1`）、区块回放与契约样例。冻结旧 SDK 的夹具不随新 SDK 更新。
- 区块形状变了：用 `tests/test_section_replay.ps1 -Update` 重新生成 `tests/fixtures/sections/contract.txt`，并在提交说明写明哪些区块变了、为什么。
- 不在契约样例里的区块，第 5 条要靠读代码核对：runs 由哪次 `Add` 写出，builder 里就必须是同一段文字。
