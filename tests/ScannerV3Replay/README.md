# 旧版模组扫描器回放（scanner v3）

物品详情必须同时在两代模组扫描器下启用：v3（100-Rev12 及更早）和 v4（当前正式版）。
两代对同一份 IL 的判断并不相同，所以本目录保存 v3 扫描器的回放夹具，
`tests/test_mod_scan.ps1` 用它和游戏自带的 v4 扫描器一起检查**发布包里的那份 DLL**。

## 两代的差别

| 差异 | v3 | v4 |
|---|---|---|
| `System.Reflection.Emit.*` | 一律算 `dynamic-code` | 放行 `OpCode`、`OpCodes`、`ILGenerator`、`Label` 等纯数据与描述类型 |
| 字面量类型名的反射 | `AccessTools.TypeByName("Outer+Inner")` 追不到，判 `reflection-external` | 追得到，免费 |
| 预警字段 | 没有 `Pending` / `DeprecatedCapabilities` | 有，表示「今天只警告、下一版会拒」 |

2026-11-10 的 `AnimalGenes` 补丁就是第一条：`OpCode` 的 `!=` 在旧版被判 `dynamic-code`，
清单没有声明，旧版加载器直接拒绝启用。0.9.3 修的是第二条。两条经过都记在
`docs/experience/loader-mods-list-snapshot-and-reflection-sandbox.md`。

## 目录

| 路径 | 作用 |
|---|---|
| `ScannerV3Replay.csproj` | 用 v3 源码编出 `ScannerV3Replay.dll`，引用游戏目录里的 `Assembly-CSharp`、`DseCecil`、`0Harmony` |
| `GameBindings.cs` | 让 v3 源码沿用当前游戏程序集里的 `ModLoader.IsGameAssemblyName` |
| `sources/` | v3 扫描器反编译源码，**不入库**（游戏代码），来源见下 |

## sources/ 从哪来

`sources/` 是旧版游戏 `Assembly-CSharp.dll` 的反编译产物，属于游戏代码，不进仓库。
需要时从一个 `scannerVersion = 3` 的安装导出这五个类型：

```powershell
$old = '<旧版游戏目录>/Probably Stolen_Data/Managed/Assembly-CSharp.dll'
ilspycmd -t ModScanner $old > tests/ScannerV3Replay/sources/ModScanner.cs
ilspycmd -t ModScanResult $old > tests/ScannerV3Replay/sources/ModScanResult.cs
ilspycmd -t ModScanFinding $old > tests/ScannerV3Replay/sources/ModScanFinding.cs
ilspycmd -t ModSteamRules $old > tests/ScannerV3Replay/sources/ModSteamRules.cs
ilspycmd -t ModSteamUse $old > tests/ScannerV3Replay/sources/ModSteamUse.cs
```

判断手里这份是不是 v3：`ModTrust.Refresh` 里写的是 `scannerVersion = 3`（v4 写 4）。

## 怎么确认回放件真的是 v3

测试不看文件名，编译后直接反射判别：

- 回放件的 `ModScanner` **不能**有 `DynamicCodeExceptions` 白名单字段，`ModScanResult`
  **不能**有 `Pending` 属性——这两样是 v4 才有的；
- 游戏目录里的扫描器必须两样都有，否则说明这台机器的游戏不是 v4，结论不可用。

`sources/` 缺失时 `tests/test_mod_scan.ps1` 会明确失败并指向本文件。这条检查依赖游戏程序集，
所以只在本地 `scripts/test.ps1 -GameDir "<游戏目录>"` 里跑；CI 没有游戏程序集，不跑它。

## 判定口径

两版都必须满足：扫描方法数大于零、`Refused` 为假、`Capabilities` 为空、`HardFlags` 为空。
v4 另要求 `Pending` 与 `DeprecatedCapabilities` 为空。扫描对象是
`dist/item_details-<ModVersion>.zip` 里 manifest 声明的那份 DLL，不是 `bin/` 里的中间产物。
